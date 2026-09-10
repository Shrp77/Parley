using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;

namespace Parley.Server;

/// <summary>
/// The broker process of SPEC.md §4: one database, one gate, and every attached agent.
/// </summary>
/// <remarks>
/// <para>
/// <b>It owns the only connection.</b> That is the point of the whole architecture rather than
/// a detail of this class. SQLite in WAL mode needs coherent shared memory between every process
/// touching the file, which no network filesystem provides, so a store on a share is corrupt
/// rather than slow — and confining the file to one process is what makes agents on other
/// machines possible at all.
/// </para>
/// <para>
/// <b>One broker per attached agent, sharing that connection.</b>
/// <see cref="Parley.Broker.Broker"/> is bound to the agent it attached for: it holds that name,
/// and its <see cref="Parley.Broker.Broker.StartedUtc"/> is when that agent came up. So the
/// runtime keeps one per attachment and looks it up by name. They all queue behind one gate,
/// because the connection is not thread-safe and the brokers say so in their own remarks.
/// </para>
/// <para>
/// <b>The gate is a real bound on throughput and is the right trade here.</b> Every operation is
/// a handful of indexed queries against a local file, and the alternative — a connection per
/// operation — would mean the in-memory databases the tests use were no longer shared between
/// the units of work that make up one test. The place this stops being right is a deployment
/// whose broker is busy enough to queue, and the shape that fits then is a connection factory,
/// which is a larger change than it looks and not worth making against load nobody has measured.
/// </para>
/// <para>
/// <b>Attachment is a fact about a stream, not a row somebody remembered to write.</b> An agent
/// is here because its <c>Attach</c> stream is open, and it leaves when the stream ends — however
/// it ends. Nothing renews anything, so there is no lease to lapse and no timestamp for a reader
/// to interpret.
/// </para>
/// </remarks>
public sealed class BrokerRuntime : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Every attachment for a name, not the latest. SPEC.md §14 treats two windows open on one
    // repository as two sessions for one agent -- `connected` counts them rather than flagging
    // one -- so a map holding only the newest would leave the older window connected, counted,
    // and silent: it holds its own stream and would simply never be written to again.
    //
    // Immutable arrays under AddOrUpdate rather than a lock around a list. Nudging reads this on
    // every write to any inbox, and a reader that had to take a lock would be contending with
    // attach and detach for the privilege of sending a notice that is disposable anyway.
    private readonly ConcurrentDictionary<string, ImmutableArray<AttachedAgent>> _attached =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Callers waiting on a node's report, keyed by instruction.
    /// </summary>
    /// <remarks>
    /// The command stream to a node runs one way, so an instruction's outcome arrives as its own
    /// call some time later. This is what joins the two: the operator's request parks here, the
    /// node's report completes it. Keyed by instruction rather than by agent, because an operator
    /// may reasonably instruct the same agent twice before the first has settled.
    /// </remarks>
    private readonly ConcurrentDictionary<long, TaskCompletionSource<InstructionResult>> _awaited =
        new();

    private readonly ConcurrentDictionary<string, JoinedNode> _nodes =
        new(StringComparer.Ordinal);

    /// <summary>
    /// When attention was last asked for on an agent's behalf, until that agent drains.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The bound that makes waking a notification's recipient affordable.</b> SPEC.md §17
    /// question 15 refused the wake on the grounds that it would be unbounded, and the answer is
    /// in question 1's own evidence: <c>inbox</c> collects everything pending, so one turn drains
    /// sixty notifications. Asking a second time buys nothing.
    /// </para>
    /// <para>
    /// <b>Keyed on the drain, not on the attach, and that distinction is the whole of it.</b>
    /// <c>NodeCommand.StartAsync</c> dispatches a turn when the resident is already alive — so the
    /// node already refuses a duplicate <em>process</em> and cannot refuse a duplicate
    /// <em>turn</em>. A marker cleared by the attach would clear a second after the first
    /// notification, leaving the other fifty-nine to buy a turn apiece. §4.2 already draws this
    /// line: waking an adapter is not the model taking a turn, and only a drain proves the mail
    /// was read.
    /// </para>
    /// <para>
    /// Per recipient across all senders, because the recipient is the party that pays. A per-pair
    /// key would leave one agent wakeable once per peer, so a deployment of twenty could hand it
    /// twenty turns for what a single turn collects together.
    /// </para>
    /// <para>
    /// In memory rather than stored, deliberately. A broker that has just started holds no
    /// residents, so a remembered wake would be a claim about a process that no longer exists —
    /// the same reasoning that clears <c>connected</c> on startup.
    /// </para>
    /// </remarks>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _pending =
        new(StringComparer.Ordinal);

    private readonly SqliteConnection _connection;
    private readonly Parley.Broker.Broker _house;
    private readonly Parley.Broker.BrokerOptions _options;

    private readonly bool _ownsConnection;

    private BrokerRuntime(
        SqliteConnection connection,
        Parley.Broker.BrokerOptions options,
        TimeProvider clock,
        RetentionPolicy retention,
        bool ownsConnection = true)
    {
        _connection = connection;
        _ownsConnection = ownsConnection;
        _options = options;
        Clock = clock;
        Retention = retention;

        _house = new Parley.Broker.Broker(connection, options);
        _house.ServeFrom(clock.GetUtcNow());

        // A connection is evidence (SPEC.md §4.0), and a broker that has just started holds
        // none -- so every `connected` in the database is a claim left over from the process
        // that died. Cleared here rather than on the way out, because the shutdown this most
        // needs to survive is the one where nothing got to run.
        var now = clock.GetUtcNow();
        _ = new NodeRepository(connection).DisconnectAllAsync(now).GetAwaiter().GetResult();
        _ = new AgentRepository(connection).DisconnectAllAsync(now).GetAwaiter().GetResult();
    }

    /// <summary>
    /// How long a wake stays pending before another may be asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A failure valve, not the mechanism.</b> What ordinarily clears a pending wake is the
    /// recipient draining. This exists only for the wake that never produces one — a machine whose
    /// node has gone, a launch that fails, a process that attaches and dies before reading
    /// anything — so that the first failed start does not silence waking permanently.
    /// </para>
    /// <para>
    /// <b>Two minutes, because the error is asymmetric and this errs long.</b> It has to
    /// comfortably exceed cold-start-to-drain on a slow machine: a process launching, a server
    /// connecting, an adapter attaching, a model taking its first turn. Too long only delays a
    /// retry of something that has already failed once. Too short reintroduces exactly the
    /// duplicate turns this exists to prevent, on the machines slowest to start — the ones least
    /// able to afford them.
    /// </para>
    /// <para>
    /// <b>Provisional, and on a record rather than on judgement alone.</b> Rule 8.5's cap was set
    /// from <c>refusals</c> rather than argued in advance, and the same route exists here:
    /// <c>invocations</c> records when starts happened and how long turns took, so
    /// start-to-first-drain is measurable on a real deployment. It moves on that, not because a
    /// case felt slow.
    /// </para>
    /// <para>
    /// A constant rather than a schema fact, and the contrast is the argument. §13.1's
    /// deduplication window is in the schema because it decides the meaning of buckets already
    /// stored, so changing it changes what stored rows mean. This governs no stored row; changing
    /// it invalidates nothing that exists, and a migration would be ceremony.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan WakeCeiling = TimeSpan.FromMinutes(2);

    /// <summary>Gets the clock every operation is stamped from.</summary>
    public TimeProvider Clock { get; }

    /// <summary>
    /// Gets how long <c>Instruct</c> waits for a turn before answering that it is still running.
    /// </summary>
    /// <remarks>
    /// Surfaced from the options rather than written in the service, because it was written in the
    /// service and that is most of why it was wrong: nothing could compare it against the turn
    /// budget it silently contradicted, and nothing could shorten it for a test that needed to
    /// watch the wait elapse.
    /// </remarks>
    public TimeSpan InstructionPatience => _options.InstructionPatience;

    /// <summary>
    /// Gets the retention policy this deployment prunes to.
    /// </summary>
    /// <remarks>
    /// The broker's, read from the broker's own environment. It used to be read by whoever ran
    /// the CLI, which meant an operator on another machine could prune to a different floor than
    /// the sweep used — from a box that does not hold the store and cannot see what the setting
    /// there is.
    /// </remarks>
    public RetentionPolicy Retention { get; }

    /// <summary>Gets the agents currently attached, by name.</summary>
    public IReadOnlyCollection<string> Attached => (IReadOnlyCollection<string>)_attached.Keys;

    /// <summary>Gets how many sessions are open for an agent.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>The count, which is zero when nothing is attached.</returns>
    public int SessionsFor(AgentName agent) =>
        _attached.TryGetValue(agent.Value, out var sessions) ? sessions.Length : 0;

    /// <summary>Gets the machines currently joined, by name.</summary>
    public IReadOnlyCollection<string> Nodes => (IReadOnlyCollection<string>)_nodes.Keys;

    /// <summary>
    /// Opens a database and prepares it to be served.
    /// </summary>
    /// <param name="databasePath">The store, on this machine.</param>
    /// <param name="options">Lifecycle configuration.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="retention">
    /// What to prune to, or <see langword="null"/> to read it from this process's environment.
    /// </param>
    /// <returns>The runtime.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <remarks>
    /// Migration happens here, once, rather than in every client. It used to run in each
    /// adapter's launch path, which meant a deployment's schema version was whatever the
    /// most recently updated repository happened to carry — and two adapters at different
    /// versions raced to apply it.
    /// </remarks>
    public static BrokerRuntime Open(
        string databasePath,
        Parley.Broker.BrokerOptions options,
        TimeProvider clock,
        RetentionPolicy? retention = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = ParleyDatabase.Open(databasePath);

        try
        {
            RefuseAForeignDatabase(connection, databasePath);
            SchemaMigrator.Migrate(connection);

            return new BrokerRuntime(
                connection, options, clock,
                retention ?? RetentionPolicyEnvironment.Read(Environment.GetEnvironmentVariable));
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens an in-memory database and prepares it to be served.
    /// </summary>
    /// <param name="options">Lifecycle configuration.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="retention">What to prune to, or <see langword="null"/> for the default.</param>
    /// <returns>The runtime.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <remarks>
    /// For tests, and honest about it. An in-memory database lives inside its connection, so
    /// this arrangement is only coherent because the runtime holds exactly one — which is the
    /// same property the real deployment relies on, reached from the other direction.
    /// </remarks>
    public static BrokerRuntime OpenInMemory(
        Parley.Broker.BrokerOptions options,
        TimeProvider clock,
        RetentionPolicy? retention = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        var connection = ParleyDatabase.OpenInMemory();

        try
        {
            return new BrokerRuntime(
                connection, options, clock, retention ?? RetentionPolicy.Default);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Refuses to write a schema into a database Parley did not create.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="databasePath">The path, for the message.</param>
    /// <exception cref="InvalidOperationException">The file holds somebody else's database.</exception>
    /// <remarks>
    /// <para>
    /// A typo should not install a schema. Pointed at an application's database by a mistyped
    /// path, migration writes Parley's tables and triggers into it, moves <c>user_version</c>
    /// from zero to one, and reports a healthy start — and the person who typed it has no reason
    /// to look.
    /// </para>
    /// <para>
    /// This check used to live in the adapter, which was where migration used to happen. It
    /// moved here with the migration rather than being dropped with the file it was in, because
    /// the risk moved too: one process now creates every store, so it is the only thing left
    /// that can create one in the wrong place.
    /// </para>
    /// <para>
    /// Both reads under one transaction, which is not tidiness. They are two statements, so
    /// outside one they can straddle a migration commit — version read before it, tables read
    /// after — and the pair then reads exactly like somebody else's database.
    /// </para>
    /// </remarks>
    private static void RefuseAForeignDatabase(SqliteConnection connection, string databasePath)
    {
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
    /// Serves a connection somebody else opened.
    /// </summary>
    /// <param name="connection">An open, already-migrated connection. Not disposed by this.</param>
    /// <param name="options">Lifecycle configuration.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="retention">What to prune to, or <see langword="null"/> for the default.</param>
    /// <returns>The runtime.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <remarks>
    /// <para>
    /// For a host that has to hold the connection itself, which in practice means a test over an
    /// in-memory database: such a database lives inside its connection, so seeding one and
    /// serving it are only the same database if they are the same connection.
    /// </para>
    /// <para>
    /// It does not take ownership. <see cref="DisposeAsync"/> leaves the connection open, because
    /// a type that closed something it was handed makes the two lifetimes hard to reason about —
    /// the same argument <see cref="Parley.Broker.Broker"/> makes about the connection it borrows.
    /// </para>
    /// </remarks>
    public static BrokerRuntime Over(
        SqliteConnection connection,
        Parley.Broker.BrokerOptions options,
        TimeProvider clock,
        RetentionPolicy? retention = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        return new BrokerRuntime(
            connection, options, clock, retention ?? RetentionPolicy.Default, ownsConnection: false);
    }

    /// <summary>
    /// Registers an agent if it is unknown, attaches it, and holds it until its stream ends.
    /// </summary>
    /// <param name="registration">What the adapter said about itself.</param>
    /// <param name="cancellationToken">Cancels the attach.</param>
    /// <returns>The attachment, which the caller disposes when the stream ends.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="registration"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The name is held by a different workspace (SPEC.md §4.1.1), or the agent is retired.
    /// </exception>
    public async Task<AttachedAgent> AttachAsync(
        AgentRecord registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var agents = new AgentRepository(_connection);

            // §4.1.1: a name is held by one repository. Two checkouts on one machine agree about
            // the role and the machine and therefore about the whole name, so without this they
            // share a registry row and an identity -- and nothing anywhere looks wrong. Messages
            // for one arrive at whichever happens to be running, and the symptom is a peer
            // receiving replies about a repository it never asked about.
            //
            // The workspace is the only distinguishing fact available, so a registration that
            // supplies none cannot be checked. That is the honest limit rather than a reason to
            // guess.
            // Compared through WorkspacePath rather than as text. Two spellings of one directory
            // were being refused as two repositories -- `C:/repo` against `C:\repo` -- and two
            // directories differing only in case were being admitted as one, which on a
            // case-sensitive filesystem is the collision this check exists to refuse.
            if (registration.Workspace is { Length: > 0 } here
                && await agents.WorkspaceOfAsync(registration.Name, cancellationToken)
                    .ConfigureAwait(false) is { Length: > 0 } held
                && !WorkspacePath.SameRepository(here, held))
            {
                throw new InvalidOperationException(
                    $"'{registration.Name.Value}' is already registered to '{held}', and this " +
                    $"one is attaching from '{here}'. An agent name belongs to one repository: " +
                    "give this one a different role, or retire the other.");
            }

            await agents.RegisterIfAbsentAsync(registration, cancellationToken)
                .ConfigureAwait(false);

            // Then the one column the attach owns, which the insert will not have written if the
            // agent was already known. Without this an agent could say what it owns exactly once
            // -- on its first attach ever -- and never correct it, and every peer routes by that
            // statement.
            await agents.RefreshOwnershipAsync(
                registration.Name, registration.Owns, cancellationToken).ConfigureAwait(false);

            var broker = new Parley.Broker.Broker(_connection, _options);

            var started = await broker
                .StartAsync(registration.Name, Clock.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);

            // Zero rows marked live means the registry refused, and there is exactly one reason
            // it can: §14 has MarkLiveAsync decline a retired agent, because retirement is a
            // person's judgement and a process starting is not evidence against it.
            if (started.AgentMarkedLive == 0)
            {
                throw new InvalidOperationException(
                    $"'{registration.Name.Value}' is retired and cannot attach. An operator " +
                    "reinstates it with `parley reinstate`.");
            }

            var attached = new AttachedAgent(this, registration.Name, broker, started);

            // Last, so that a failure above leaves nothing to find. Appended rather than
            // assigned: the name may already be here, and both sessions are real.
            _ = _attached.AddOrUpdate(
                registration.Name.Value,
                [attached],
                (_, existing) => existing.Add(attached));

            return attached;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Records that a machine's node is connected and holds it until its stream ends.
    /// </summary>
    /// <param name="node">What the node observed about its machine.</param>
    /// <param name="startableRoles">The roles it says it can launch.</param>
    /// <param name="cancellationToken">Cancels the join.</param>
    /// <returns>The join, which the caller disposes when the stream ends.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A node is already joined for that machine.</exception>
    /// <remarks>
    /// A second node for one machine is refused rather than counted, unlike a second session for
    /// one agent. SPEC.md §4 gives a machine one node; two supervisors on one box would each
    /// receive the same start command and race to launch the same agent, and nothing downstream
    /// would show it — the loser simply attaches a second session for an agent that needed one.
    /// </remarks>
    public async Task<JoinedNode> JoinNodeAsync(
        NodeRecord node,
        IEnumerable<string> startableRoles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(startableRoles);

        var joined = new JoinedNode(
            this, node.Machine, startableRoles.ToHashSet(StringComparer.Ordinal));

        if (!_nodes.TryAdd(node.Machine, joined))
        {
            throw new InvalidOperationException(
                $"A node is already connected for '{node.Machine}'. SPEC.md §4 gives a machine " +
                "one node: two would each be told to start the same agents.");
        }

        try
        {
            _ = await UseConnectionAsync(
                async (connection, now, ct) =>
                {
                    await new NodeRepository(connection).JoinAsync(node, now, ct)
                        .ConfigureAwait(false);

                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _ = _nodes.TryRemove(new KeyValuePair<string, JoinedNode>(node.Machine, joined));
            throw;
        }

        return joined;
    }

    /// <summary>Releases a node when its stream has ended.</summary>
    /// <param name="joined">The join.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the registry has been told.</returns>
    internal async Task ReleaseNodeAsync(
        JoinedNode joined,
        CancellationToken cancellationToken = default)
    {
        _ = _nodes.TryRemove(new KeyValuePair<string, JoinedNode>(joined.Machine, joined));

        _ = await UseConnectionAsync(
            async (connection, now, ct) =>
            {
                await new NodeRepository(connection).LeaveAsync(joined.Machine, now, ct)
                    .ConfigureAwait(false);

                // **A departing node does NOT settle the instructions it was holding, and the
                // temptation to make it do so is why this comment is here.**
                //
                // It looks obviously right: §11.5 gives delivery to the node, so when the node goes
                // its turns went with it, and `failed` is exactly "the turn was attempted and did
                // not complete". It was built that way and then probed, and the probe is in
                // `InstructionSurvivesTests`.
                //
                // A *stream* ending is not a node process dying. A network blip ends the stream
                // while the agent carries on working, and settling on that destroys the answer when
                // it arrives — settlement is first-come, so the real report is refused. That is
                // precisely the defect this feature exists to remove, reintroduced through a
                // different door, and it gets likelier the moment a deployment is reached over a
                // private network rather than loopback, which §16 explicitly permits.
                //
                // So the broker does not claim to know. A turn whose node has gone stays *still
                // running* until the sweep presumes it dead past TurnBudget.PresumedDead — an
                // unknown honestly reported, rather than a false claim that also destroys evidence.

                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Delivers an operator's instruction, and waits for the agent to have its turn.</summary>
    /// <param name="instructionId">The instruction being delivered.</param>
    /// <param name="agent">Who it is for.</param>
    /// <param name="text">What the operator said.</param>
    /// <param name="patience">How long to wait for the node's report.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>What became of it.</returns>
    /// <remarks>
    /// <para>
    /// Undeliverable rather than failed when no node holds the agent's machine, and the
    /// distinction is the point: §11.5 says an agent whose node is not connected cannot be
    /// instructed and must be told so rather than left looking as though it was. The remedies
    /// differ — one is a machine that is switched off, and re-sending will not change it.
    /// </para>
    /// <para>
    /// A turn is a model thinking, so the wait is generous and bounded rather than indefinite. An
    /// instruction that outlives its patience is reported as failed and the row settles: the
    /// alternative is an operator holding a page open against a turn that may never end.
    /// </para>
    /// </remarks>
    public async Task<InstructionResult> DeliverInstructionAsync(
        long instructionId,
        AgentName agent,
        string text,
        TimeSpan patience,
        CancellationToken cancellationToken = default)
    {
        if (!_nodes.TryGetValue(agent.Machine, out var node))
        {
            return new InstructionResult(InstructionOutcome.Undeliverable, null);
        }

        var waiting = new TaskCompletionSource<InstructionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_awaited.TryAdd(instructionId, waiting))
        {
            return new InstructionResult(InstructionOutcome.Failed, null);
        }

        try
        {
            if (!node.RequestInstruction(instructionId, agent, text))
            {
                return new InstructionResult(InstructionOutcome.Undeliverable, null);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(patience);

            // **Pending, not failed, and this is the same defect one layer in.** Removing the
            // settling write stopped the broker *recording* a verdict it had no grounds for; this
            // stops it *reporting* one. `Failed` is defined as "the turn was attempted and did not
            // complete", which is a claim about the turn — and while the patience elapses the turn
            // is simply still running. An operator told `failed` reissues the instruction, which
            // §10.2 names as the one response that makes the situation worse.
            //
            // Note that the linked token covers the caller giving up as well as the patience
            // elapsing, and both mean the same thing here: this call is over and the turn is not.
            // Which of the two happened is a fact about the operator, and neither is a fact about
            // the agent.
            await using var registration = timeout.Token.Register(
                () => waiting.TrySetResult(new InstructionResult(InstructionOutcome.Pending, null)))
                .ConfigureAwait(false);

            return await waiting.Task.ConfigureAwait(false);
        }
        finally
        {
            _ = _awaited.TryRemove(instructionId, out _);
        }
    }

    /// <summary>Completes whoever is waiting on an instruction's outcome.</summary>
    /// <param name="instructionId">The instruction.</param>
    /// <param name="result">What became of it.</param>
    /// <returns><see langword="true"/> if somebody was waiting.</returns>
    /// <remarks>
    /// False is ordinary rather than an error: the operator's request may have given up before
    /// the node answered, and a node reporting into an empty slot has still done its job. The row
    /// is settled by the reporting path regardless, so the record does not depend on anybody
    /// still listening.
    /// </remarks>
    public bool CompleteInstruction(long instructionId, InstructionResult result) =>
        _awaited.TryGetValue(instructionId, out var waiting) && waiting.TrySetResult(result);

    /// <summary>
    /// Asks an agent's machine to see to it: a start if it is not running, a turn if it is.
    /// </summary>
    /// <param name="agent">The agent a question is waiting for.</param>
    /// <param name="because">Why, for the operator's benefit.</param>
    /// <returns><see langword="true"/> if the machine was asked for either.</returns>
    /// <remarks>
    /// <para>
    /// This is what makes SPEC.md §5.5 more than a promise. A question may be addressed to an
    /// agent that is registered and not running, and the answer to "what happens then" is that
    /// its machine is told to start it — over the connection that machine already holds, so
    /// nothing here dials anything and no address in the registry is ever used as a route.
    /// </para>
    /// <para>
    /// <b>Not named for the start, because the start is only half of it.</b> An agent that is
    /// already attached needs a turn rather than a process, and this used to fall silent in that
    /// case — which read as correct and meant every question after the first reached nobody. A
    /// name that promised only a start is how that stayed invisible: the call did what it said,
    /// and what it said was not what the traffic needed.
    /// </para>
    /// <para>
    /// Silent and false when the machine has no node, or when that node did not list the role
    /// among what it can launch. Neither is an error: §5.5 asks a machine that is switched off
    /// deliberately, so that its silence arrives as an orphan the asker is told about rather than
    /// an answer nobody noticed was missing.
    /// </para>
    /// </remarks>
    public bool RequestAttention(AgentName agent, string because) =>
        Ask(agent, because) is WakeOutcome.Started or WakeOutcome.AlreadyRunning;

    /// <summary>
    /// Asks an agent's machine to see to its mail, unless something already has.
    /// </summary>
    /// <param name="agent">The agent a notification is waiting for.</param>
    /// <param name="because">Why, for the operator's benefit.</param>
    /// <returns>What became of the asking.</returns>
    /// <remarks>
    /// <para>
    /// <b>The only caller that consults the pending mark, and that asymmetry is deliberate.</b>
    /// A question keeps <see cref="RequestAttention"/>'s behaviour because §7.1's per-pair cap
    /// already bounds questions — which is exactly the bound SPEC.md §17 question 15 identified as
    /// holding the line today. What changes is that a notification will not stack a turn on top of
    /// one a question has already asked for: that turn will collect it.
    /// </para>
    /// <para>
    /// Returns an outcome rather than a flag because a sender told only that its message was
    /// stored cannot tell delivery from silence, which is the half of the reported defect that was
    /// not about waking at all. None of the outcomes is a refusal — see
    /// <see cref="WakeOutcome"/>.
    /// </para>
    /// </remarks>
    public WakeOutcome RequestAttentionForMail(AgentName agent, string because)
    {
        // **Claimed before asking, not checked before asking.** Reading the mark and then acting on
        // it is two operations, and the flood this bound exists for is precisely the case where
        // sixty of them interleave: every one would read "nothing pending" and every one would ask.
        // The claim is the atomic step, so exactly one caller proceeds.
        var claimed = Clock.GetUtcNow();

        if (!TryClaimAttention(agent, claimed))
        {
            return WakeOutcome.AlreadyComing;
        }

        var outcome = Ask(agent, because);

        if (outcome is WakeOutcome.Unreachable)
        {
            // Nothing is coming after all, so the claim is given back rather than left to expire.
            // Holding it would suppress every notification for the next two minutes on the strength
            // of a turn that was never asked for.
            //
            // **Given back by key and value, not by key.** A question's wake can land between the
            // claim above and this line -- a node joining in the gap is all it takes -- and a bare
            // removal would then throw away a mark for a turn that really is coming, buying a
            // duplicate. Narrow, and the sort of narrow this bound exists to be exact about.
            _ = ((ICollection<KeyValuePair<string, DateTimeOffset>>)_pending)
                .Remove(new KeyValuePair<string, DateTimeOffset>(agent.Value, claimed));
        }

        return outcome;
    }

    /// <summary>Whether a mark claimed that long ago still counts.</summary>
    /// <param name="elapsed">How long since it was claimed, by this broker's clock.</param>
    /// <returns><see langword="true"/> while it is inside the ceiling.</returns>
    /// <remarks>
    /// <b>Negative is expired, not fresh, and this was found by probing rather than by reading.</b>
    /// A clock does not only move forwards: an NTP correction steps it back, and a machine resuming
    /// from a snapshot steps it back a long way. The obvious comparison — elapsed under the ceiling
    /// — answers <em>yes</em> for a mark claimed in what is now the future, so a step back of an
    /// hour would suspend waking for that agent for an hour. That is this feature's own defect,
    /// reintroduced by arithmetic that reads as though it handles it.
    /// </remarks>
    private static bool Live(TimeSpan elapsed) =>
        elapsed >= TimeSpan.Zero && elapsed < WakeCeiling;

    /// <summary>Takes the pending mark for an agent, if nothing live already holds it.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="now">The instant to stamp the claim with.</param>
    /// <returns><see langword="true"/> if this caller took it.</returns>
    /// <remarks>
    /// A compare-and-set loop rather than <c>AddOrUpdate</c>, because that method's update delegate
    /// may be invoked more than once under contention and only one of those invocations wins — so a
    /// flag set inside it would report a claim to a caller that did not get one, which is the exact
    /// failure this method exists to prevent.
    /// </remarks>
    private bool TryClaimAttention(AgentName agent, DateTimeOffset now)
    {
        while (true)
        {
            if (_pending.TryAdd(agent.Value, now))
            {
                return true;
            }

            if (!_pending.TryGetValue(agent.Value, out var since))
            {
                // Removed between the two calls -- a drain landing right here. Try again.
                continue;
            }

            if (Live(now - since))
            {
                return false;
            }

            if (_pending.TryUpdate(agent.Value, now, since))
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Records that an agent has collected what was waiting for it.
    /// </summary>
    /// <param name="agent">The agent that drained.</param>
    /// <remarks>
    /// Called from the drain and from nowhere else, because the drain is the single place a
    /// collection is observable. Everything asked for before it has now been seen, so the next
    /// notification is free to ask again.
    /// </remarks>
    public void Collected(AgentName agent) => _ = _pending.TryRemove(agent.Value, out _);

    /// <summary>
    /// Whether a turn has been asked for on an agent's behalf that it has not yet answered by
    /// draining.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <returns><see langword="true"/> while one is outstanding and inside the ceiling.</returns>
    /// <remarks>
    /// The ceiling is what keeps a wake that never produced a drain from silencing this for ever —
    /// a machine with no node, a launch that fails, a process that attaches and dies before
    /// reading anything. Without it the first failed start would be the last one ever attempted.
    /// </remarks>
    public bool AttentionPending(AgentName agent)
    {
        if (!_pending.TryGetValue(agent.Value, out var since))
        {
            return false;
        }

        if (Live(Clock.GetUtcNow() - since))
        {
            return true;
        }

        // Dropped on the way past rather than left to be re-read on every later notification.
        // Removed by key and value together, so a wake that landed between the read above and
        // here is not thrown away by this one.
        _ = ((ICollection<KeyValuePair<string, DateTimeOffset>>)_pending)
            .Remove(new KeyValuePair<string, DateTimeOffset>(agent.Value, since));

        return false;
    }

    /// <summary>Asks the agent's machine for whatever it needs, and marks attention pending.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="because">Why, for the operator's benefit.</param>
    /// <returns>What became of the asking.</returns>
    /// <remarks>
    /// <b>Every wake marks, whatever asked for it.</b> A notification arriving while a question's
    /// turn is already coming must find the mark set, or the common case — ask a peer, then tell it
    /// something — costs two turns for one reading.
    /// </remarks>
    private WakeOutcome Ask(AgentName agent, string because)
    {
        if (!_nodes.TryGetValue(agent.Machine, out var node))
        {
            return WakeOutcome.Unreachable;
        }

        // Attached already, so a start would duplicate a process. What the traffic needs is not a
        // start but a turn, and falling silent here is what left a second question reaching
        // nobody: written, never collected, ghosted on the ordinary schedule while the registry
        // reported the agent live. The node gives the turn only to a resident of its own, so an
        // agent attached through somebody's editor window is left alone rather than duplicated.
        var running = _attached.TryGetValue(agent.Value, out var sessions) && !sessions.IsEmpty;

        if (!(running ? node.RequestTurn(agent, because) : node.RequestStart(agent, because)))
        {
            // The node holds this machine but does not name the role among what it can launch, so
            // nothing here will run it. Not marked pending: nothing is coming, and marking would
            // suppress the next notification on the strength of a turn that was never asked for.
            return WakeOutcome.Unreachable;
        }

        _pending[agent.Value] = Clock.GetUtcNow();

        return running ? WakeOutcome.AlreadyRunning : WakeOutcome.Started;
    }

    /// <summary>
    /// Runs one unit of work against an attached agent's broker, alone.
    /// </summary>
    /// <typeparam name="T">What the work returns.</typeparam>
    /// <param name="agent">The agent the work is on behalf of.</param>
    /// <param name="work">The work, given the broker and the instant to stamp it with.</param>
    /// <param name="cancellationToken">Cancels the wait for the gate and the work.</param>
    /// <returns>Whatever the work returned.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="work"/> is null.</exception>
    /// <exception cref="NotAttachedException">The agent has no open stream.</exception>
    /// <remarks>
    /// The instant is supplied rather than read inside the work, so that every write in one
    /// operation carries the same one. A broker method that read the clock twice could stamp a
    /// message and its exchange a millisecond apart, which is harmless until somebody orders by
    /// it and finds an answer preceding its own question.
    /// </remarks>
    public async Task<T> RunAsync<T>(
        AgentName agent,
        Func<Parley.Broker.Broker, DateTimeOffset, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (!_attached.TryGetValue(agent.Value, out var sessions) || sessions.IsEmpty)
        {
            throw new NotAttachedException(agent);
        }

        // Any of them. They are brokers for the same agent over the same connection, and differ
        // only in when each attached -- which the ghosting clock reads. The newest is chosen
        // because it is the one whose grace period is still running: an agent that reopened its
        // editor a moment ago should not have its orphans measured from a session it opened
        // yesterday and left.
        var attached = sessions[^1];

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await work(attached.Broker, Clock.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs one unit of work against the connection, alone.
    /// </summary>
    /// <typeparam name="T">What the work returns.</typeparam>
    /// <param name="work">The work, given the connection and the current instant.</param>
    /// <param name="cancellationToken">Cancels the wait for the gate and the work.</param>
    /// <returns>Whatever the work returned.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="work"/> is null.</exception>
    /// <remarks>
    /// For the operator surface and for the node registry, neither of which speaks for an agent
    /// and neither of which therefore has a broker. It goes through the gate, and that is the
    /// whole reason it exists rather than a connection property: a caller holding the connection
    /// directly would be one <c>await</c> away from two writers.
    /// </remarks>
    public async Task<T> UseConnectionAsync<T>(
        Func<SqliteConnection, DateTimeOffset, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await work(_connection, Clock.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs the sweep of SPEC.md §6, on the broker the host owns.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What it did.</returns>
    /// <remarks>
    /// The sweep belongs to the process that owns the store, and this is the first arrangement
    /// in which such a process exists. It used to run inside every adapter, so a deployment of
    /// twenty repositories ran twenty sweeps over one file, each with its own idea of when the
    /// ghosting clock started.
    /// </remarks>
    public Task<Parley.Broker.TickResult> TickAsync(CancellationToken cancellationToken = default) =>
        UseConnectionAsync(
            (_, now, ct) => _house.TickAsync(now, ct),
            cancellationToken);

    /// <summary>
    /// Tells an agent that something is waiting, if it is attached.
    /// </summary>
    /// <param name="agent">The agent whose inbox grew.</param>
    /// <param name="cancellationToken">Cancels the count.</param>
    /// <returns>A task that completes when the notice has been queued, or found unnecessary.</returns>
    /// <remarks>
    /// <para>
    /// Counts only, per SPEC.md §13 and the note on <c>Notice</c> in the contract: a peer's words
    /// reach a model wrapped as data, the envelope is applied by the drain, and a notice carrying
    /// the words would be a second path for the same text with no envelope on it.
    /// </para>
    /// <para>
    /// Silent when the agent is not attached, which is the ordinary case rather than a failure.
    /// SPEC.md §5.5 asks a role whether or not its machines are awake, precisely so that a
    /// question to something switched off becomes an orphan the asker is told about rather than
    /// an answer nobody noticed was missing.
    /// </para>
    /// <para>
    /// Best effort. A notice is an optimisation over the drain the adapter would have done
    /// anyway, so failing to deliver one costs latency and never correctness — and a broker that
    /// failed an <c>answer</c> because it could not nudge the asker would be trading the durable
    /// half of the operation for the disposable one.
    /// </para>
    /// </remarks>
    public async Task NudgeAsync(AgentName agent, CancellationToken cancellationToken = default)
    {
        if (!_attached.TryGetValue(agent.Value, out var sessions) || sessions.IsEmpty)
        {
            return;
        }

        try
        {
            var counts = await UseConnectionAsync(
                async (connection, _, ct) =>
                {
                    var waiting = await new MessageRepository(connection)
                        .CountPendingAsync(agent, ct).ConfigureAwait(false);

                    var polls = await new ConversationRepository(connection)
                        .CountOutstandingPollsAsync(agent, ct).ConfigureAwait(false);

                    return (waiting, polls);
                },
                cancellationToken).ConfigureAwait(false);

            // Every session, because every one of them is a window somebody has open. The
            // counts are the same for all of them -- an inbox belongs to an agent, not to a
            // session -- so this is one query and N cheap writes.
            foreach (var session in sessions)
            {
                session.Post(counts.waiting, counts.polls);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Deliberately swallowed. See the remarks: the notice is disposable and the
            // operation that produced it is not.
        }
    }

    /// <summary>Releases an attachment when its stream has ended.</summary>
    /// <param name="attached">The attachment.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the registry has been told.</returns>
    /// <remarks>
    /// Called by <see cref="AttachedAgent.DisposeAsync"/> rather than by a client, because the
    /// end of a stream is the only thing that means this and a client cannot be relied upon to
    /// still be there to say so.
    /// </remarks>
    internal async Task ReleaseAsync(
        AttachedAgent attached,
        CancellationToken cancellationToken = default)
    {
        // This attachment only. Two sessions for one agent are legitimate (§14), so one ending
        // must remove itself and leave the other where it is -- and the name goes only when the
        // last of them has gone.
        while (_attached.TryGetValue(attached.Name.Value, out var sessions))
        {
            var remaining = sessions.Remove(attached);

            if (remaining.IsEmpty)
            {
                if (_attached.TryRemove(
                    new KeyValuePair<string, ImmutableArray<AttachedAgent>>(
                        attached.Name.Value, sessions)))
                {
                    break;
                }
            }
            else if (_attached.TryUpdate(attached.Name.Value, remaining, sessions))
            {
                break;
            }

            // Somebody attached or detached between the read and the write. Read again rather
            // than assuming: the alternative is dropping a session that arrived in the gap, and
            // it would be dropped silently.
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _ = await attached.Broker.DetachAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Ends every held stream, without touching the registry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called when the host begins stopping, before it waits for its handlers. An <c>Attach</c>
    /// handler blocks on a read that completes only when its channel does, so without this a
    /// graceful shutdown waits the full shutdown timeout for every attached agent — thirty
    /// seconds by default, and the same thirty whether one agent is attached or twenty.
    /// </para>
    /// <para>
    /// The registry is left alone deliberately. A broker that is stopping has <c>connected</c>
    /// counts that mean nothing until it comes back, and it clears them at startup rather than
    /// racing every handler on the way out — which is also the only arrangement that survives
    /// the shutdown it most needs to, the one where it did not get to run anything.
    /// </para>
    /// </remarks>
    public void CloseStreams()
    {
        foreach (var sessions in _attached.Values)
        {
            foreach (var attached in sessions)
            {
                attached.Close();
            }
        }

        foreach (var node in _nodes.Values)
        {
            node.Close();
        }
    }

    /// <summary>Closes the database.</summary>
    /// <returns>A task that completes when it is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        CloseStreams();

        _attached.Clear();
        _nodes.Clear();
        _gate.Dispose();

        if (_ownsConnection)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);

            // Disposing returns the connection to Microsoft.Data.Sqlite's pool, which keeps the
            // file open. For a broker stopped because its project was removed, that is the
            // difference between `remove --destroy` deleting the store and being told something
            // still has it open -- by this process, which has just said it stopped. This pool
            // only: the other projects' connections are theirs.
            SqliteConnection.ClearPool(_connection);
        }
    }
}
