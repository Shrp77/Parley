using System.Globalization;
using Microsoft.Data.Sqlite;
using Parley.Broker;
using Parley.Core;
using Parley.Store;

namespace Parley.Mcp;

/// <summary>
/// The one broker this process owns, and the gate that keeps it to one caller at a time.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Parley.Broker.Broker"/> documents that it is not thread-safe and that a host must
/// serialise access: it holds a single <see cref="SqliteConnection"/> for its lifetime, which is
/// not thread-safe either, and <c>StartedUtc</c> is plain mutable state that the ghosting clock
/// reads. An MCP server handles requests concurrently, so something has to reconcile those, and
/// this is it.
/// </para>
/// <para>
/// A semaphore rather than a per-call connection, which was the alternative. The broker's own
/// documentation sets out why: <c>StartedUtc</c> is per-process state that the ghosting clock
/// depends on, so an instance created for one call would not have it, and reworking that means a
/// connection factory rather than a connection — a larger change than it looks, because the
/// in-memory databases the tests use are private to their connection. At this scale the gate
/// costs nothing real: every operation is a handful of indexed queries against a local file, and
/// §4.1 already parallelises across projects by giving each its own process.
/// </para>
/// <para>
/// The sweep goes through the same gate as every tool call. It is the one piece of work that
/// arrives on a different thread by construction, so leaving it outside would reintroduce
/// exactly the race the gate exists to prevent — and it would do so rarely, which is the worst
/// frequency for a data race to occur at.
/// </para>
/// </remarks>
public sealed class BrokerSession : IAsyncDisposable
{
    /// <summary>
    /// What this server records as the adapter attaching its agent.
    /// </summary>
    /// <remarks>
    /// A capability name rather than a vendor one, matching §11.1's classification. Every client
    /// that speaks MCP reaches the broker the same way, so the vendor of the one that launched
    /// this process tells an operator nothing they can act on.
    /// </remarks>
    public const string Adapter = "mcp";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SqliteConnection _connection;
    private readonly Parley.Broker.Broker _broker;
    private readonly TimeProvider _clock;
    private int _disposed;

    private BrokerSession(
        SqliteConnection connection,
        Parley.Broker.Broker broker,
        TimeProvider clock)
    {
        _connection = connection;
        _broker = broker;
        _clock = clock;
    }

    /// <summary>
    /// Opens the database, migrates it, registers this agent, and attaches it.
    /// </summary>
    /// <param name="options">What this server was launched with.</param>
    /// <param name="clock">The clock to stamp operations from.</param>
    /// <param name="cancellationToken">Cancels start-up.</param>
    /// <returns>A started session.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="AgentRetiredException">This agent's registration has been withdrawn.</exception>
    /// <exception cref="SqliteException">The database could not be opened or migrated.</exception>
    /// <remarks>
    /// <para>
    /// The attach sequence runs here rather than lazily, because SPEC.md §14 makes it the thing
    /// that invalidates what the previous session believed — and every operation on the broker
    /// refuses until it has. Doing it at start-up means a launch either works or fails visibly,
    /// rather than producing a server whose every tool reports that the broker has not started.
    /// </para>
    /// <para>
    /// It also orphans the exchanges this agent owed when its last session ended, which is why
    /// the resume policy matters and why the default is <c>Never</c>: an interactive deployment
    /// should not have work resume that somebody stopped on purpose.
    /// </para>
    /// </remarks>
    public static async Task<BrokerSession> StartAsync(
        ServerOptions options,
        TimeProvider clock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        EnsureDirectory(options.DatabasePath);

        var connection = ParleyDatabase.Open(options.DatabasePath);

        try
        {
            RefuseAForeignDatabase(connection, options.DatabasePath);
            SchemaMigrator.Migrate(connection);

            await RegisterAsync(connection, options, cancellationToken).ConfigureAwait(false);

            var broker = new Parley.Broker.Broker(connection, BrokerOptionsFor(options));
            var attached = await broker
                .StartAsync(options.Agent, clock.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);

            // Zero rows marked live means the registry refused, and there is exactly one reason
            // it can: SPEC.md §14 has MarkLiveAsync decline a retired agent, because retirement
            // is a person's judgement and a process starting is not evidence against it. The
            // upsert above cannot have cleared it either — it leaves status alone by design.
            //
            // Refused here rather than left to the first tool call, because a retired agent is
            // refused every operation anyway, and a server that launches cleanly and then fails
            // everything reports the problem to the model, which cannot fix it, instead of to
            // the person who retired the agent.
            if (attached.AgentMarkedLive == 0)
            {
                throw new AgentRetiredException(options.Agent);
            }

            return new BrokerSession(connection, broker, clock);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Builds the broker configuration this server runs under.
    /// </summary>
    /// <param name="options">What this server was launched with.</param>
    /// <returns>The configuration.</returns>
    /// <remarks>
    /// <para>
    /// The liveness lease is derived from the sweep interval rather than configured beside it,
    /// because the two have a relationship an operator would have to know about to get right.
    /// The sweep is what renews the lease, so a lease shorter than a few ticks lapses between
    /// heartbeats on a perfectly healthy server — and an agent whose lease has lapsed has its
    /// live work released by the next session that attaches. Deriving it means setting
    /// <c>PARLEY_TICK_SECONDS</c> to an hour widens the lease to match instead of quietly
    /// breaking liveness.
    /// </para>
    /// <para>
    /// Three ticks tolerates two missed sweeps, which covers a pass that ran long or threw and
    /// waited for the next. The floor keeps a very short interval from producing a lease so
    /// tight that ordinary scheduling jitter expires it.
    /// </para>
    /// </remarks>
    private static BrokerOptions BrokerOptionsFor(ServerOptions options) =>
        BrokerOptions.Default with
        {
            LivenessLease = options.TickInterval * 3 > BrokerOptions.DefaultLivenessLease
                ? options.TickInterval * 3
                : BrokerOptions.DefaultLivenessLease,
            Retention = options.Retention
        };

    /// <summary>
    /// Refuses to migrate a database somebody else made.
    /// </summary>
    /// <param name="connection">The open, unmigrated connection.</param>
    /// <param name="databasePath">The configured path, for the message.</param>
    /// <exception cref="InvalidOperationException">The file holds a schema that is not Parley's.</exception>
    /// <remarks>
    /// <para>
    /// <c>PARLEY_DB</c> is a path in a launch configuration, and a path in a launch configuration
    /// is a thing people mistype. Pointed at an application's own SQLite file, the migration
    /// writes seven tables, their indexes and their triggers into it and bumps
    /// <c>user_version</c> from zero to one — which is silent, and which breaks whatever
    /// migration tooling that database had, because <c>user_version</c> is where most of it
    /// keeps its place.
    /// </para>
    /// <para>
    /// Tables present with a schema version of zero is the signature: a Parley database this
    /// server created carries a version from its first migration onwards, and the migration is
    /// transactional, so tables and version commit together and there is no half-state to
    /// mistake for this one. An empty file, or no file at all, is the ordinary first run and
    /// passes.
    /// </para>
    /// </remarks>
    private static void RefuseAForeignDatabase(SqliteConnection connection, string databasePath)
    {
        // Both reads under one transaction, which is not tidiness. They are two statements, so
        // outside one they can straddle another server's migration commit — version read before
        // it, tables read after — and the pair then reads exactly like somebody else's database.
        // Six servers starting together on a fresh project hit that on the third round.
        using var snapshot = connection.BeginTransaction();

        using var version = connection.CreateCommand();
        version.Transaction = snapshot;
        version.CommandText = "PRAGMA user_version;";

        if (Convert.ToInt64(version.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
        {
            return;
        }

        using var tables = connection.CreateCommand();
        tables.Transaction = snapshot;
        tables.CommandText =
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";

        if (Convert.ToInt64(tables.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"'{databasePath}' already holds a database that Parley did not create. Refusing to " +
            "write a schema into it. Check the path.");
    }

    /// <summary>
    /// Creates the directory the database file lives in, if it is missing.
    /// </summary>
    /// <param name="databasePath">The configured database path.</param>
    /// <remarks>
    /// <para>
    /// The first run of a project is the case: <c>PARLEY_DB</c> conventionally names
    /// <c>.parley/parley.db</c> inside the repository, and nothing has created <c>.parley</c>.
    /// SQLite reports that as "unable to open database file", which is the truth and tells
    /// somebody setting this up for the first time nothing about what to do.
    /// </para>
    /// <para>
    /// Here rather than in <see cref="ParleyDatabase"/>, which is a library another host embeds
    /// under its own policy — creating directories on its behalf is the adapter's decision to
    /// make and not the store's. Permissions are left to the platform: SPEC.md §9.1 asks for a
    /// user-scoped location, and a directory Parley invents inside one inherits that, while one
    /// invented outside cannot be rescued by an ACL applied afterwards.
    /// </para>
    /// <para>
    /// Failures are swallowed, because the open that follows produces the better message. A path
    /// with no directory part, an unwritable parent, or a name the platform refuses all end at
    /// the same place, and reporting them twice in different words helps nobody.
    /// </para>
    /// </remarks>
    private static void EnsureDirectory(string databasePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception)
#pragma warning restore CA1031
        {
            // See above: the open reports this better than a second message would.
        }
    }

    /// <summary>
    /// Records this agent in the registry, if it is not already there.
    /// </summary>
    /// <param name="connection">The open database.</param>
    /// <param name="options">What this server was launched with.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the registration is recorded.</returns>
    /// <remarks>
    /// <para>
    /// An agent registers itself on the way in, per SPEC.md §12.3. There is otherwise no way to
    /// bootstrap a deployment: <see cref="Parley.Broker.Broker.StartAsync"/> refuses an
    /// unregistered agent, so a repository whose session had never opened could not open one.
    /// A registration step the person has to remember is a step they will discover they forgot
    /// by watching every tool fail.
    /// </para>
    /// <para>
    /// This adds no authority the launch configuration did not already carry. The name comes
    /// from the environment the client supplies and never from a tool argument, so what an agent
    /// can register itself as is exactly what it can already act as — which is §13's guarantee,
    /// unchanged. What it emphatically does not do is register anybody <em>else</em>.
    /// </para>
    /// <para>
    /// <see cref="AgentRepository.RegisterIfAbsentAsync"/> writes nothing at all when the name is
    /// already known, so a second launch cannot revive a retirement by the side door — and,
    /// more to the point, cannot overwrite a registration an operator made. An agent described
    /// in a manifest as Headless, with the endpoint the broker needs in order to invoke it,
    /// would otherwise be rewritten as a configuration-free Pull agent by one person opening
    /// that repository once. The <see cref="AgentStatus.Registered"/> passed here therefore
    /// applies to the first launch only, which is the one where it is true.
    /// </para>
    /// <para>
    /// The tier is <see cref="AgentTier.Pull"/>, which is an honest claim rather than a
    /// conservative one, and applies only when this server is what introduced the agent. An MCP server cannot interrupt a running turn — nothing in the protocol
    /// pushes a message into a model's context between tool calls — so inbound traffic is seen
    /// when the agent drains its inbox and at no other time. Declaring <c>Full</c> would have
    /// the broker push to something that cannot be pushed to.
    /// </para>
    /// </remarks>
    private static async Task RegisterAsync(
        SqliteConnection connection,
        ServerOptions options,
        CancellationToken cancellationToken)
    {
        var agents = new AgentRepository(connection);

        await agents.RegisterIfAbsentAsync(
            new AgentRecord(
                options.Agent,
                Adapter,
                AgentTier.Pull,
                options.Workspace,
                SessionRef: null,
                AdapterConfig: null,
                LastSeenUtc: null,
                AgentStatus.Registered),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one unit of work against the broker, alone.
    /// </summary>
    /// <typeparam name="T">What the work returns.</typeparam>
    /// <param name="work">The work, given the broker and the instant to stamp it with.</param>
    /// <param name="cancellationToken">Cancels the wait for the gate and the work itself.</param>
    /// <returns>Whatever <paramref name="work"/> returned.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="work"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The instant is supplied rather than read inside the work, so that every write in one
    /// operation carries the same one. A broker method that read the clock twice could stamp a
    /// message and its exchange a millisecond apart, which is harmless until somebody orders by
    /// it and finds an answer preceding its own question.
    /// </remarks>
    public async Task<T> RunAsync<T>(
        Func<Parley.Broker.Broker, DateTimeOffset, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await work(_broker, _clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Takes the gate before closing the connection, so a tool call in flight finishes rather
    /// than meeting a disposed connection. A stdio server is shut down by its client closing the
    /// pipe, which can happen mid-call.
    /// </para>
    /// <para>
    /// Detaches on the way out, which marks the agent offline. Nothing depends on it — deadlines
    /// are enforced by whichever servers are still sweeping, and this agent's debts are released
    /// when it next attaches — so it is the courtesy that makes <c>parley_agents</c> show who is
    /// actually at their desk, and no more than that. A process that crashes calls nothing, and
    /// the deployment has to be correct anyway.
    /// </para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        // Idempotent, and reached that way rather than by misuse: Program disposes in a finally
        // and a stopped host may already have disposed the session on the way out, so a second
        // call is the tail of an ordinary clean run. Without the flag it would be an
        // ObjectDisposedException from the gate — a crash reported at the end of a run that
        // worked.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            try
            {
                await _broker.DetachAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception)
#pragma warning restore CA1031
            {
                // Swallowed, because this is a cosmetic write on a path that must not throw.
                // Dispose is reached from shutdown and from the failure paths of start-up, and
                // a mark nobody reads is not worth masking the exception that brought us here —
                // nor worth leaving the connection open over.
            }

            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
