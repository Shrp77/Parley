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
    /// <summary>The token each node said it gave the agent it is starting, until that agent attaches (SPEC.md §11.7).</summary>
    private readonly ConcurrentDictionary<AgentName, string> _launches = new();

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

    // The turns nodes have said are running and not yet said are over (SPEC.md §11.6). In memory on purpose: it is a
    // fact about live connections, like a node being joined, and a broker that has just started holds none — so a
    // restart forgetting them is right by construction, where a column would be false after every crash.
    private readonly ConcurrentDictionary<AgentName, RunningTurn> _turns = new();

    /// <summary>The silent participants each closure poll has already asked again, keyed by poll.</summary>
    /// <remarks>
    /// In memory, for the reason the pending mark is: a restart forgets, and the worst that costs is one more ask.
    /// Never pruned, because an entry is three short strings and one exists only for a participant that went silent
    /// on a poll it was asked — a count bounded by the conversations that ever stalled on a vote.
    /// </remarks>
    private readonly ConcurrentDictionary<string, byte> _askedAgain = new(StringComparer.Ordinal);

    /// <summary>Claims the one time a silent participant may be asked again in a poll (§5.0).</summary>
    /// <param name="conversationId">The conversation being polled.</param>
    /// <param name="agent">The participant that has not voted.</param>
    /// <param name="polledUtc">When its poll began, which identifies the poll.</param>
    /// <returns><see langword="true"/> the first time for that poll, and never again.</returns>
    /// <remarks>
    /// The bound is on the turns being spent rather than on which vote came before, because each turn is a
    /// collection and a collection frees the next wake: any rule about when to ask was one a participant could
    /// replay by repeating or alternating its own vote. Claimed atomically, so two votes landing at once ask once.
    /// </remarks>
    internal bool TryAskAgain(string conversationId, AgentName agent, DateTimeOffset polledUtc) =>
        _askedAgain.TryAdd(
            string.Join(
                PairKey.Separator,
                conversationId,
                agent.Value,
                polledUtc.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            0);

    /// <summary>
    /// When attention was last asked for on an agent's behalf, until that agent begins collecting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The bound that makes waking every message's recipient affordable.</b> SPEC.md §17
    /// question 15 once refused a wake on the grounds that it would be unbounded, and the answer is
    /// in question 1's own evidence: <c>inbox</c> with no limit collects everything pending, so one
    /// turn drains sixty messages and asking a second time buys nothing.
    /// </para>
    /// <para>
    /// <b>Two things the review gate changed about when it clears</b> (SPEC.md §5.4). It is removed as a
    /// collection begins rather than after it returns (<see cref="CollectAsync{T}"/>), so a message stored
    /// during one asks for its own turn. And a collection its caller limited to fewer than are waiting is
    /// followed by a wake for the rest (<c>AgentsService.DrainInbox</c>), because there asking again is what
    /// the messages left behind are owed.
    /// </para>
    /// <para>
    /// <b>Keyed on the drain, not on the attach, and that distinction is the whole of it.</b>
    /// <c>NodeCommand.StartAsync</c> dispatches a turn when the resident is already alive — so the
    /// node already refuses a duplicate <em>process</em> and cannot refuse a duplicate
    /// <em>turn</em>. A marker cleared by the attach would clear a second after the first
    /// message, leaving the other fifty-nine to buy a turn apiece. §4.2 already draws this
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

    /// <summary>
    /// Machines this broker has already woken agents on for work that was outstanding when it started.
    /// </summary>
    /// <remarks>
    /// Once per machine per broker lifetime, because what SPEC.md §14's resume policy governs is a restart.
    /// A node rejoining after a network blip has not restarted anything, and waking its agents again would
    /// buy turns for work they are already doing.
    /// </remarks>
    private readonly ConcurrentDictionary<string, bool> _resumedMachines = new(StringComparer.Ordinal);

    private readonly SqliteConnection _connection;
    private readonly Parley.Broker.Broker _house;
    private readonly Parley.Broker.BrokerOptions _options;

    private readonly bool _ownsConnection;

    // The claim on the store, for a runtime opened from a path. Null for one given a connection or
    // an in-memory database, neither of which is a file another broker could also be serving.
    private readonly StoreLock? _storeLock;

    private BrokerRuntime(
        ProjectName project,
        SqliteConnection connection,
        Parley.Broker.BrokerOptions options,
        TimeProvider clock,
        RetentionPolicy retention,
        FileLocationStore files,
        bool ownsConnection = true,
        StoreLock? storeLock = null)
    {
        Files = files;
        _connection = connection;
        _ownsConnection = ownsConnection;
        _storeLock = storeLock;
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

        // The two rows the broker writes itself (SPEC.md §12.3), before anything could want to send as, or to, either:
        // the facilitator, and the person seat an agent asks when no agent can help (principle 2.6). Nothing launches
        // either, which makes them the two identities no launch fixes (constitution, Principle III). Both are named for
        // the project, not a machine, because neither is hosted on one (§4.1.1).
        Project = project;
        Facilitator = new AgentRepository(connection).EnsureFacilitatorAsync(project).GetAwaiter().GetResult();
        Person = new AgentRepository(connection).EnsurePersonAsync(project).GetAwaiter().GetResult();

        // Last, once everything it reads through is in place: the scan it starts goes through the gate like any other
        // write, and changes made while no broker was running are found before anybody asks.
        Watcher = new FolderWatcher(this);
        Watcher.Start();
    }

    /// <summary>Gets the facilitator's name in this store (SPEC.md principle 2.11).</summary>
    public AgentName Facilitator { get; }

    /// <summary>Gets the person seat's name in this store (SPEC.md §12.3).</summary>
    public AgentName Person { get; }

    /// <summary>Gets the project this store serves, which both broker-provided participants are named for.</summary>
    public ProjectName Project { get; }

    /// <summary>Gets what notices changes made to the file location outside Parley (SPEC.md §9.5).</summary>
    internal FolderWatcher Watcher { get; }

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
    internal static readonly TimeSpan WakeCeiling = TimeSpan.FromMinutes(2);

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

    /// <summary>Gets the file location this broker's store keeps its placed files in (SPEC.md §9.5).</summary>
    /// <remarks>
    /// Owned by the runtime because it is the store's: the same process that alone opens the database alone writes
    /// the files, and the holds a download takes live in memory here, so a restart ends every download and every
    /// hold together.
    /// </remarks>
    public FileLocationStore Files { get; }

    /// <summary>Gets the fetches streamed to completion and not yet reported as written.</summary>
    public ServedFetches Fetches { get; } = new();

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
    /// <param name="project">
    /// The project this store serves, which the broker names its two own participants for (SPEC.md §4.1.1, §12.3).
    /// </param>
    /// <param name="databasePath">The store, on this machine.</param>
    /// <param name="options">Lifecycle configuration.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="retention">
    /// What to prune to, or <see langword="null"/> to read it from this process's environment.
    /// </param>
    /// <returns>The runtime, holding the store until it is disposed.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="StoreHeldException">Another broker is serving this store.</exception>
    /// <remarks>
    /// <para>
    /// Migration happens here, once, rather than in every client. It used to run in each
    /// adapter's launch path, which meant a deployment's schema version was whatever the
    /// most recently updated repository happened to carry — and two adapters at different
    /// versions raced to apply it.
    /// </para>
    /// <para>
    /// <b>The store is locked before anything else touches it, and the order is the fix.</b> The
    /// constructor clears every <c>connected</c> it finds, which is right for a broker that is
    /// about to serve and ruinous for one that is not: two launchers at logon — a scheduled task
    /// and a terminal restoring its last session — both reached it, the one that then failed to
    /// bind its port had already marked the other's live node disconnected, and seventeen agents
    /// read as stranded on the machine that was running them. Nothing put the flag back, because
    /// the live node's stream never ended. Taken here, a second broker is refused before it has
    /// opened a connection, run a migration or written a row.
    /// </para>
    /// </remarks>
    public static BrokerRuntime Open(
        ProjectName project,
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

        var held = StoreLock.Acquire(databasePath);
        SqliteConnection? connection = null;

        try
        {
            connection = ParleyDatabase.Open(databasePath);

            RefuseAForeignDatabase(connection, databasePath);
            SchemaMigrator.Migrate(connection);

            // After the lock, like everything else that touches what belongs to the store: emptying the staging
            // directory while another broker was mid-placement would destroy an upload it was about to commit.
            var files = new FileLocationStore(databasePath + StoreFiles.Files, databasePath + StoreFiles.FilesIncoming);
            files.Prepare();

            return new BrokerRuntime(
                project, connection, options, clock,
                retention ?? RetentionPolicyEnvironment.Read(Environment.GetEnvironmentVariable),
                files,
                storeLock: held);
        }
        catch
        {
            connection?.Dispose();
            held.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens an in-memory database and prepares it to be served.
    /// </summary>
    /// <param name="options">Lifecycle configuration.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="retention">What to prune to, or <see langword="null"/> for the default.</param>
    /// <param name="filesRoot">
    /// Where placed files go, or <see langword="null"/> for a temporary directory deleted when the runtime is.
    /// </param>
    /// <param name="project">The project it serves, or <see langword="null"/> for <c>test</c>.</param>
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
        RetentionPolicy? retention = null,
        string? filesRoot = null,
        ProjectName? project = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        var connection = ParleyDatabase.OpenInMemory();

        try
        {
            return new BrokerRuntime(
                project ?? TestProject, connection, options, clock, retention ?? RetentionPolicy.Default,
                FilesAt(filesRoot));
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
    /// <param name="filesRoot">
    /// Where placed files go, or <see langword="null"/> for a temporary directory deleted when the runtime is.
    /// </param>
    /// <param name="project">The project it serves, or <see langword="null"/> for <c>test</c>.</param>
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
        RetentionPolicy? retention = null,
        string? filesRoot = null,
        ProjectName? project = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        return new BrokerRuntime(
            project ?? TestProject, connection, options, clock, retention ?? RetentionPolicy.Default,
            FilesAt(filesRoot), ownsConnection: false);
    }

    /// <summary>The project a runtime opened for a test serves, when the test does not name one.</summary>
    /// <remarks>
    /// Only <see cref="OpenInMemory"/> and <see cref="Over"/> default it, and nothing but tests calls either. A runtime
    /// over a real store must be told its project (<see cref="Open"/>), because the name is written into the two rows
    /// the broker provides and cannot be corrected later. <c>test</c> is what 022's tests already named the
    /// facilitator's second half, so they read the same.
    /// </remarks>
    private static readonly ProjectName TestProject = ProjectName.Create("test");

    /// <summary>A file location for a runtime that has no store path to put one beside.</summary>
    /// <param name="root">The directory asked for, or <see langword="null"/> for a temporary one.</param>
    /// <returns>The location, prepared.</returns>
    /// <remarks>
    /// Temporary by default and deleted with the runtime, so a test never writes beside a real store and never
    /// leaves a location behind. The staging directory is beside the root, as it is for a real store.
    /// </remarks>
    private static FileLocationStore FilesAt(string? root)
    {
        var temporary = root is null;
        var location = root ?? Path.Combine(Path.GetTempPath(), "parley-files-" + Guid.NewGuid().ToString("N"));

        var files = new FileLocationStore(location, location + "-incoming", deleteOnDispose: temporary);
        files.Prepare();

        return files;
    }

    /// <summary>
    /// Registers an agent if it is unknown, attaches it, and holds it until its stream ends.
    /// </summary>
    /// <param name="registration">What the adapter said about itself.</param>
    /// <param name="cancellationToken">Cancels the attach.</param>
    /// <param name="launchToken">What the node that started this process put in its environment, or null (SPEC.md §11.7).</param>
    /// <returns>The attachment, which the caller disposes when the stream ends.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="registration"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The name is held by a different workspace (SPEC.md §4.1.1), or the agent is retired.
    /// </exception>
    public async Task<AttachedAgent> AttachAsync(
        AgentRecord registration,
        CancellationToken cancellationToken = default,
        string? launchToken = null)
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
                    "reinstates it from the agents pane in `parley`, or from its page in the web UI.");
            }

            // **Started by Parley only by the token its node reported for this launch** (SPEC.md §11.7), and spent here, so
            // a second process presenting it — an editor started from a shell that inherited it — reads as outside.
            var byParley = launchToken is { Length: > 0 }
                && _launches.TryGetValue(registration.Name, out var launched)
                && string.Equals(launched, launchToken, StringComparison.Ordinal)
                && _launches.TryRemove(new KeyValuePair<AgentName, string>(registration.Name, launched));

            var attached = new AttachedAgent(this, registration.Name, broker, started, byParley);

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
    /// <param name="reportsTurns">Whether the node said it reports the turns it gives (SPEC.md §11.6).</param>
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
        CancellationToken cancellationToken = default,
        bool reportsTurns = false)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(startableRoles);

        var joined = new JoinedNode(
            this, node.Machine, startableRoles.ToHashSet(StringComparer.Ordinal), reportsTurns);

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

        if (_options.ResumePolicy == ResumePolicy.Always && _resumedMachines.TryAdd(node.Machine, true))
        {
            await WakeForOutstandingWorkAsync(node.Machine, cancellationToken).ConfigureAwait(false);
        }

        return joined;
    }

    /// <summary>
    /// Wakes the agents on a machine that owe work outstanding from before the broker started (§14).
    /// </summary>
    /// <param name="machine">The machine whose node has just joined.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>A task that completes when every waking has been asked for.</returns>
    /// <remarks>
    /// <para>
    /// <b>The resume policy decides only whether agents are woken, never whether the work survives.</b> The
    /// work is open under every policy. Under <c>always</c> this asks the machine to start or give a turn to
    /// each agent that owes some of it and that the resume gate permits; under <c>never</c> and <c>ask</c>
    /// it is not called, and the work is flagged on the ordinary schedule for the person to act on.
    /// </para>
    /// <para>
    /// Here rather than when the broker opens its store, because a node joining is the first moment an
    /// agent on its machine can be started at all.
    /// </para>
    /// </remarks>
    private async Task WakeForOutstandingWorkAsync(string machine, CancellationToken cancellationToken)
    {
        var owing = await UseConnectionAsync(
            async (connection, _, ct) =>
            {
                var agents = (await new AgentRepository(connection).GetAllAsync(ct).ConfigureAwait(false))
                    .Where(a => a.Status == AgentStatus.Active && a.Name.Machine == machine)
                    // The two broker-provided participants are hosted on no machine; their second half is the project,
                    // so a node that happens to share a project's name must not be taken to host them (T013a).
                    .Where(a => !PersonSeat.Is(a.Name) && !Facilitation.Is(a.Name))
                    .Select(a => a.Name)
                    .ToList();

                var due = new List<AgentName>();

                foreach (var agent in agents)
                {
                    if ((await _house.OutstandingToWakeForAsync(agent, ct).ConfigureAwait(false)).Count > 0)
                    {
                        due.Add(agent);
                    }
                }

                return due;
            },
            cancellationToken).ConfigureAwait(false);

        foreach (var agent in owing)
        {
            _ = RequestAttentionForMail(agent, "work was outstanding when the broker started");
        }
    }

    /// <summary>Records that a node began a turn for one of its agents (SPEC.md §11.6).</summary>
    /// <param name="machine">The reporting node's machine.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="startedUtc">When the turn began, as the node says.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Whether the report was accepted.</returns>
    public async Task<bool> TurnStartedAsync(
        string machine, AgentName agent, DateTimeOffset startedUtc, CancellationToken cancellationToken = default)
    {
        if (!Plausible(startedUtc) || !_nodes.TryGetValue(machine, out var reporter))
        {
            return false;
        }

        var before = await AcceptedTurnAsync(machine, agent, cancellationToken).ConfigureAwait(false);

        if (before is not { } waiting)
        {
            return false;
        }

        var running = new RunningTurn(startedUtc, machine, waiting);

        _ = _turns.AddOrUpdate(agent, running, (_, was) => was.Started > startedUtc ? was : running);

        // The node may have left while the count was read, and its release swept the table before this landed. Taken
        // back, or the agent would read as working until its next report — the three hours 024 exists to end.
        if (!_nodes.TryGetValue(machine, out var still) || !ReferenceEquals(still, reporter))
        {
            _ = _turns.TryRemove(new KeyValuePair<AgentName, RunningTurn>(agent, running));
            return false;
        }

        return true;
    }

    /// <summary>Whether a node's word about when a turn began is within a day of this broker's clock.</summary>
    /// <param name="startedUtc">The node's time.</param>
    /// <returns>Whether to believe it.</returns>
    /// <remarks>
    /// Machines' clocks differ, so this is loose on purpose; what it stops is a start a year ahead, which would hold an
    /// agent "working" until then, or put a turn's end past the calendar (both found by the review gate).
    /// </remarks>
    private bool Plausible(DateTimeOffset startedUtc) =>
        (startedUtc - Clock.GetUtcNow()).Duration() <= TimeSpan.FromDays(1);

    /// <summary>Records that a node's turn for one of its agents ended (SPEC.md §11.6).</summary>
    /// <param name="machine">The reporting node's machine.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="ended">What the node said about the turn.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether the report was accepted.</returns>
    /// <remarks>
    /// <para>
    /// <b>Recorded in <c>invocations</c> with waiting counts the broker took itself</b> — before from when it heard the turn
    /// begin, after from now — because they are its inbox, which the node cannot see. An end whose beginning this broker
    /// never heard (it restarted mid-turn) is still recorded, counting before as after: nothing better is known.
    /// </para>
    /// <para>
    /// <b>An end clears a running turn only if that turn is not newer than it.</b> A slow report of one turn's end arriving
    /// after the next turn's start must not make the agent read as idle while it works.
    /// </para>
    /// </remarks>
    public async Task<bool> TurnEndedAsync(
        string machine, AgentName agent, EndedTurn ended, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ended);

        if (!Plausible(ended.StartedUtc))
        {
            return false;
        }

        var after = await AcceptedTurnAsync(machine, agent, cancellationToken).ConfigureAwait(false);

        if (after is not { } waitingAfter)
        {
            return false;
        }

        var before = _turns.TryGetValue(agent, out var running) && running.Started == ended.StartedUtc
            ? running.WaitingBefore
            : waitingAfter;

        if (running is not null && running.Started <= ended.StartedUtc)
        {
            _ = _turns.TryRemove(new KeyValuePair<AgentName, RunningTurn>(agent, running));
        }

        _ = await UseConnectionAsync(
            (connection, _, ct) => new InvocationRepository(connection).RecordAsync(
                agent,
                before,
                waitingAfter,
                ended.StartedUtc,
                ended.Elapsed,
                ended.Clean ? 0 : 1,
                ended.TimedOut,
                ended.CostUsd,
                ended.ModelTurns,
                ended.PermissionDenials,
                ct),
            cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>When the turn running now for an agent began, or null when none is.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>The start.</returns>
    public DateTimeOffset? TurnRunningSince(AgentName agent) =>
        _turns.TryGetValue(agent, out var running) ? running.Started : null;

    /// <summary>Records that a node is about to start an agent with a token in its environment (SPEC.md §11.7).</summary>
    /// <param name="machine">The reporting node's machine.</param>
    /// <param name="agent">The agent it is starting.</param>
    /// <param name="token">The token it chose for this launch.</param>
    /// <returns>Whether the report was accepted.</returns>
    /// <remarks>
    /// Only from the node joined for the agent's machine, and never for the person or Parley, whom no node starts — the
    /// same checks a turn report meets. A newer launch's token replaces an older one's, which is what a restart is.
    /// </remarks>
    public bool LaunchReported(string machine, AgentName agent, string token)
    {
        if (PersonSeat.Is(agent)
            || Facilitation.Is(agent)
            || string.IsNullOrWhiteSpace(token)
            || token.Length > 128
            || !_nodes.ContainsKey(machine)
            || !string.Equals(agent.Machine, machine, StringComparison.Ordinal))
        {
            return false;
        }

        _launches[agent] = token;

        return true;
    }

    /// <summary>Whether an agent is running only as processes Parley did not start (SPEC.md §11.7).</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>True when it is attached and no attachment presented its node's token.</returns>
    public bool OpenOutside(AgentName agent) =>
        _attached.TryGetValue(agent.Value, out var sessions)
        && !sessions.IsEmpty
        && sessions.All(session => !session.StartedByParley);

    /// <summary>Whether this agent's turns are reported (SPEC.md §11.6).</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>
    /// True only while a node that said so is joined for the agent's machine and can start it — never for the person or
    /// Parley, whom no node gives a turn.
    /// </returns>
    /// <remarks>
    /// <b>Per agent, not per machine</b> (the audit before the gate resumed). Decided by machine alone, an agent nobody's
    /// node runs — one opened in an editor on a machine whose node reports turns — read as reported, and could be shown
    /// stopped on a turn some earlier run left behind; and a node joined under a project's name made the person and Parley
    /// read as reported. <b>The limit is recorded rather than hidden</b>: an agent the node could start but somebody
    /// opened in an editor instead still reads as reported, because the broker sees two attachments of one name alike.
    /// </remarks>
    public bool TurnsReported(AgentName agent) =>
        !PersonSeat.Is(agent)
        && !Facilitation.Is(agent)
        && _nodes.TryGetValue(agent.Machine, out var node)
        && node.ReportsTurns
        && (_attached.TryGetValue(agent.Value, out var sessions) && !sessions.IsEmpty
            // Running: only as a process the node started, by its token (§11.7) — one opened outside is unknown.
            ? sessions.Any(session => session.StartedByParley)
            // Not running: what the node last said about a process it could start again.
            : node.Startable.Contains(agent.Role));

    /// <summary>Checks a turn report and counts the agent's waiting work, or says no.</summary>
    /// <param name="machine">The reporting machine.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>What the agent has waiting, or null when the report is not accepted.</returns>
    /// <remarks>
    /// <b>Only for an agent on the reporting node's own machine, from a node that is joined, about an agent this broker
    /// knows</b> (§11.6). §16 concedes anything that reaches a broker can claim to be anything; a node's claim about another
    /// machine's agent is the cheap forgery, and refusing it costs one comparison.
    /// </remarks>
    private async Task<long?> AcceptedTurnAsync(string machine, AgentName agent, CancellationToken cancellationToken)
    {
        // The person and Parley are hosted nowhere: the half of their names after '@' is the project, so the machine
        // comparison would admit any node joined under the project's name (WakeForOutstandingWorkAsync has the same
        // exclusion). No node gives either of them a turn.
        if (PersonSeat.Is(agent)
            || Facilitation.Is(agent)
            || !_nodes.ContainsKey(machine)
            || !string.Equals(agent.Machine, machine, StringComparison.Ordinal))
        {
            return null;
        }

        return await UseConnectionAsync(
            async (connection, _, ct) =>
                await new AgentRepository(connection).IsRegisteredAsync(agent, cancellationToken: ct).ConfigureAwait(false)
                    ? await new InvocationRepository(connection).CountWaitingAsync(agent, ct).ConfigureAwait(false)
                    : (long?)null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A turn a node has said began and not yet said ended.</summary>
    /// <param name="Started">When it began.</param>
    /// <param name="Machine">The node that said so, whose leaving ends the claim.</param>
    /// <param name="WaitingBefore">What the agent had waiting when it began.</param>
    private sealed record RunningTurn(DateTimeOffset Started, string Machine, long WaitingBefore);

    /// <summary>Releases a node when its stream has ended.</summary>
    /// <param name="joined">The join.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the registry has been told.</returns>
    internal async Task ReleaseNodeAsync(
        JoinedNode joined,
        CancellationToken cancellationToken = default)
    {
        _ = _nodes.TryRemove(new KeyValuePair<string, JoinedNode>(joined.Machine, joined));

        // **Nothing a departed node said is running is claimed to be running any more** (§11.6, specs/024 FR-006). A turn
        // reported as begun and never as ended would otherwise read as working for ever.
        foreach (var turn in _turns.Where(entry => entry.Value.Machine == joined.Machine).ToList())
        {
            _ = _turns.TryRemove(turn);
        }

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
    /// case — which read as correct and meant every message after the first reached nobody. A
    /// name that promised only a start is how that stayed invisible: the call did what it said,
    /// and what it said was not what the traffic needed.
    /// </para>
    /// <para>
    /// Silent and false when the machine has no node, or when that node did not list the role
    /// among what it can launch. Neither is an error: §5.5 sends work to a machine that is switched
    /// off deliberately, so that the work it owes waits and is flagged to the person rather than
    /// being silently absent (§6).
    /// </para>
    /// <para>
    /// Consults no pending mark, because its caller is a person's deliberate start rather than
    /// traffic; every message's wake goes through <see cref="RequestAttentionForMail"/>.
    /// </para>
    /// </remarks>
    public bool RequestAttention(AgentName agent, string because) =>
        Ask(agent, because) is WakeOutcome.Started or WakeOutcome.AlreadyRunning;

    /// <summary>
    /// Asks an agent's machine to see to its mail, unless something already has.
    /// </summary>
    /// <param name="agent">The agent a message is waiting for.</param>
    /// <param name="because">Why, for the operator's benefit.</param>
    /// <returns>What became of the asking.</returns>
    /// <remarks>
    /// <para>
    /// <b>The path every message's wake takes.</b> A Request, a Clarify and a Result all wake their
    /// recipient through here, so a burst from any number of senders costs one turn until the
    /// recipient collects, and a message will not stack a turn on top of one already coming: that
    /// turn collects it. 0.4 kept questions on <see cref="RequestAttention"/>, bounded instead by
    /// §7.1's per-pair cap; 0.5 has one message path and that cap is off by default.
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
        // Mail for the facilitator is an agent answering it or asking it something back, and the facilitation sweep is
        // what reads it (FacilitateAsync). No node runs the facilitator, so asking one would come back unreachable and
        // tell the sender nothing can see its message, which is untrue: the next sweep will.
        if (Facilitation.Is(agent))
        {
            return WakeOutcome.AlreadyComing;
        }

        // Mail for the person waits for the person (SPEC.md §5, §12.3), here or away. Nothing starts a person, so no node is
        // asked — asking one is what used to come back unreachable and tell an agent its question would never be read. A
        // surface the person has open has already been told something arrived: the nudge runs before this, for every
        // recipient. Not claimed either, because a claim bounds turns and a person takes none.
        if (PersonSeat.Is(agent))
        {
            return WakeOutcome.WithThePerson;
        }

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
            // Holding it would suppress every wake for the next two minutes on the strength
            // of a turn that was never asked for.
            //
            // **Given back by key and value, not by key.** Another wake can land between the
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
    /// <para>
    /// <b>A window either side of now, and the negative half is not symmetry for its own sake.</b> A
    /// clock does not only move forwards: an NTP correction steps it back and a resumed snapshot
    /// steps it back a long way, so a mark can be stamped in what is now the future. Comparing only
    /// against the ceiling answers <em>yes</em> for such a mark, which would suspend waking for that
    /// agent for the length of the step — this feature's own defect, reintroduced by arithmetic that
    /// reads as though it handles it.
    /// </para>
    /// <para>
    /// <b>But treating every negative elapsed as expired is worse, and an existing test caught it
    /// doing so.</b> Each caller reads the clock once and compares its own instant against another
    /// caller's stamp, so under a flood the callers whose read landed a fraction before the winner's
    /// write see a negative elapsed — and would each replace the mark and claim. Sixty concurrent
    /// notices went back to buying up to forty-three turns, which is the bound not holding at all.
    /// </para>
    /// <para>
    /// So the two cases are told apart by <em>how far</em> into the future the stamp is. Slightly
    /// ahead is a concurrent claim and counts as live; a whole ceiling ahead is a clock that moved
    /// and does not.
    /// </para>
    /// </remarks>
    private static bool Live(TimeSpan elapsed) =>
        elapsed < WakeCeiling && elapsed > -WakeCeiling;

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
    /// Records that an agent is collecting what was waiting for it.
    /// </summary>
    /// <param name="agent">The agent that drained.</param>
    /// <remarks>
    /// Reached in production only through <see cref="CollectAsync{T}"/>, which calls it before the drain reads, because
    /// the drain is the single place a collection is observable. Everything asked for before it is about to be seen,
    /// so the next message is free to ask again.
    /// </remarks>
    public void Collected(AgentName agent) => _ = _pending.TryRemove(agent.Value, out _);

    /// <summary>
    /// Collects an attached agent's mail, freeing its wake bound first.
    /// </summary>
    /// <typeparam name="T">What the drain returns.</typeparam>
    /// <param name="agent">The agent collecting.</param>
    /// <param name="drain">The drain, given the broker and the instant to stamp it with.</param>
    /// <param name="cancellationToken">Cancels the wait for the gate and the drain.</param>
    /// <returns>Whatever the drain returned.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="drain"/> is null.</exception>
    /// <exception cref="NotAttachedException">The agent has no open stream.</exception>
    /// <remarks>
    /// <para>
    /// <b>The mark is cleared before the drain reads, not after it returns.</b> Cleared after, a message stored in
    /// between found the mark still held, asked for nothing, and then lost the mark to a collection that had never
    /// seen it: the message sat with no turn coming, and nothing in 0.5 times out. Found by the review gate by
    /// replaying the two halves in their shipped order (lens 1 run 1.17).
    /// </para>
    /// <para>
    /// Cleared before, a message stored after the clear claims a turn of its own. If the drain then reads it anyway,
    /// that turn finds nothing — at most one, because the claim it made holds until the next collection. A spare turn
    /// is the cost the bound can afford; a lost one is not.
    /// </para>
    /// </remarks>
    public async Task<T> CollectAsync<T>(
        AgentName agent,
        Func<Parley.Broker.Broker, DateTimeOffset, CancellationToken, Task<T>> drain,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drain);

        Collected(agent);

        return await RunAsync(agent, drain, cancellationToken).ConfigureAwait(false);
    }

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
    internal bool AttentionPending(AgentName agent)
    {
        if (!_pending.TryGetValue(agent.Value, out var since))
        {
            return false;
        }

        if (Live(Clock.GetUtcNow() - since))
        {
            return true;
        }

        // Dropped on the way past rather than left to be re-read on every later wake.
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
    /// <b>Every wake marks, whatever asked for it.</b> A message arriving while a turn is already
    /// coming — for an earlier message, or a person's start — must find the mark set, or two in
    /// quick succession cost two turns for one reading.
    /// </remarks>
    private WakeOutcome Ask(AgentName agent, string because)
    {
        if (!_nodes.TryGetValue(agent.Machine, out var node))
        {
            return WakeOutcome.Unreachable;
        }

        // Attached already, so a start would duplicate a process. What the traffic needs is not a
        // start but a turn, and falling silent here is what left a second message reaching
        // nobody: written and never collected while the registry reported the agent live. The node gives the turn only to a resident of its own, so an
        // agent attached through somebody's editor window is left alone rather than duplicated.
        var running = _attached.TryGetValue(agent.Value, out var sessions) && !sessions.IsEmpty;

        // **Open only outside Parley** (§11.7): no node can give it a turn and starting a second process would put two
        // models in one checkout, so nobody is asked — and nothing is marked as coming, which before this suppressed
        // every wake after the first while the sender was told its receiver was running.
        if (running && sessions.All(session => !session.StartedByParley))
        {
            return WakeOutcome.OpenOutside;
        }

        if (!(running ? node.RequestTurn(agent, because) : node.RequestStart(agent, because)))
        {
            // The node holds this machine but does not name the role among what it can launch, so
            // nothing here will run it. Not marked pending: nothing is coming, and marking would
            // suppress the next wake on the strength of a turn that was never asked for.
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
        // only in when each attached -- which nothing reads now that 0.5 has no ghosting clock.
        // The newest is taken because it needs no comparison.
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
    /// Runs the broker's maintenance pass — polls, instructions nothing will report on, and retention — on the
    /// broker the host owns.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What it did.</returns>
    /// <remarks>
    /// The pass belongs to the process that owns the store, and this is the first arrangement
    /// in which such a process exists. It used to run inside every adapter, so a deployment of
    /// twenty repositories ran twenty passes over one file, each taking the write lock for the same work.
    /// </remarks>
    public Task<Parley.Broker.TickResult> TickAsync(CancellationToken cancellationToken = default) =>
        UseConnectionAsync(
            (_, now, ct) => _house.TickAsync(now, ct),
            cancellationToken);

    /// <summary>
    /// The facilitator's pass (SPEC.md principle 2.11, §10.2): asks every attached agent that is due, wakes each, and
    /// reads back what agents have answered since the last pass.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>The answers that arrived, each recorded and waiting on a decision.</returns>
    /// <remarks>
    /// <para>
    /// <b>On the sweep rather than on an event</b> (research R4). There is no attach event to hook, and a pass over the
    /// store is also what makes a broker restart harmless: nothing is remembered in memory that the store does not say,
    /// so an agent that was running before the broker started is asked on the first pass after it.
    /// </para>
    /// <para>
    /// <b>The decision about each answer is not made here.</b> Accepting one writes the agent's launch configuration,
    /// which this project cannot reach (research finding 12); the host that can, decides and calls back into
    /// <see cref="AcceptAutomaticallyAsync"/> or <see cref="HoldAsync"/>.
    /// </para>
    /// <para>
    /// Each agent asked is woken exactly as a Request from an agent wakes it, through the same bounded path, so the
    /// facilitator buys no turn an ordinary message would not have.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Parley.Broker.FacilitatorAnswer>> FacilitateAsync(
        CancellationToken cancellationToken = default)
    {
        var attached = _attached.Keys.ToArray();

        var (asked, answers) = await UseConnectionAsync(
            async (connection, now, ct) =>
            {
                var facilitator = new Parley.Broker.Facilitator(connection, _options);

                var asked = await facilitator.AskDueAsync(Facilitator, attached, now, ct).ConfigureAwait(false);
                var answers = await facilitator.ReadBackAsync(Facilitator, now, ct).ConfigureAwait(false);

                return (asked, answers);
            },
            cancellationToken).ConfigureAwait(false);

        foreach (var agent in asked)
        {
            await NudgeAsync(agent, cancellationToken).ConfigureAwait(false);
            _ = RequestAttentionForMail(agent, "Parley asked what it owns");
        }

        return answers;
    }

    /// <summary>Runs one unit of the facilitator's work, alone, under the store's gate.</summary>
    /// <typeparam name="T">What the work returns.</typeparam>
    /// <param name="work">The work, given the facilitator, its name in this store and the instant to stamp it with.</param>
    /// <param name="cancellationToken">Cancels the wait for the gate and the work.</param>
    /// <returns>Whatever the work returned.</returns>
    /// <remarks>
    /// For the operator's operations on Parley's questions (§10.2), which act through the facilitator rather than
    /// through <see cref="Parley.Broker.OperatorConsole"/>: that type may not send a Request, and asking again is one.
    /// </remarks>
    public Task<T> UseFacilitatorAsync<T>(
        Func<Parley.Broker.Facilitator, AgentName, DateTimeOffset, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        return UseConnectionAsync(
            (connection, now, ct) => work(new Parley.Broker.Facilitator(connection, _options), Facilitator, now, ct),
            cancellationToken);
    }

    /// <summary>Makes an answer the agent's line, once its launch configuration has been written.</summary>
    /// <param name="answer">The answer.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether it was recorded.</returns>
    public Task<bool> AcceptAutomaticallyAsync(
        Parley.Broker.FacilitatorAnswer answer,
        CancellationToken cancellationToken = default) =>
        UseConnectionAsync(
            (connection, now, ct) => new Parley.Broker.Facilitator(connection, _options)
                .AcceptAutomaticallyAsync(Facilitator, answer, now, ct),
            cancellationToken);

    /// <summary>Holds an answer for a person, saying why.</summary>
    /// <param name="answer">The answer.</param>
    /// <param name="because">Why it was not accepted automatically.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether it is now waiting.</returns>
    public Task<bool> HoldAsync(
        Parley.Broker.FacilitatorAnswer answer,
        string because,
        CancellationToken cancellationToken = default) =>
        UseConnectionAsync(
            (connection, _, ct) => new Parley.Broker.Facilitator(connection, _options).HoldAsync(answer, because, ct),
            cancellationToken);

    /// <summary>The project's Auto Accept switch.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Whether it is on.</returns>
    public Task<bool> AutoAcceptAsync(CancellationToken cancellationToken = default) =>
        UseConnectionAsync(
            (connection, _, ct) => new Parley.Broker.Facilitator(connection, _options).AutoAcceptAsync(ct),
            cancellationToken);

    /// <summary>
    /// Asks each agent's node to refresh it, once, after its first line was accepted automatically, when it owes nothing
    /// (SPEC.md §11.1).
    /// </summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>The agents a refresh was sent for.</returns>
    /// <remarks>
    /// <para>
    /// <b>Only when the agent owes no open exchange and no turn is on its way to it.</b> The broker cannot see a turn in
    /// progress (research finding 7), so it sends only when it has no reason to think one is coming, and the node — which
    /// can see — waits for the turn lock before stopping anything. An agent that is never idle is never refreshed, and
    /// its line stays pending, visibly.
    /// </para>
    /// <para>
    /// <b>Recorded as sent only once a node took it</b>, so an agent whose node has not joined yet is refreshed when it
    /// does, rather than never. Once recorded it is never sent again, whatever the node did with it.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<AgentName>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var due = await UseConnectionAsync(
            async (connection, _, ct) =>
            {
                var candidates = await new Parley.Broker.Facilitator(connection, _options).RefreshDueAsync(ct)
                    .ConfigureAwait(false);

                var idle = new List<(long Question, AgentName Agent)>();

                foreach (var candidate in candidates)
                {
                    if (await new ExchangeRepository(connection).CountOwedByAsync(candidate.Agent, ct)
                            .ConfigureAwait(false) == 0)
                    {
                        idle.Add(candidate);
                    }
                }

                return idle;
            },
            cancellationToken).ConfigureAwait(false);

        var sent = new List<AgentName>();

        foreach (var (question, agent) in due)
        {
            if (_pending.TryGetValue(agent.Value, out var claimed) && Live(Clock.GetUtcNow() - claimed))
            {
                continue;
            }

            if (!_nodes.TryGetValue(agent.Machine, out var node)
                || !node.RequestRefresh(agent, "its first ownership line was accepted automatically"))
            {
                continue;
            }

            await UseConnectionAsync(
                (connection, _, ct) => new Parley.Broker.Facilitator(connection, _options).RefreshSentAsync(question, ct),
                cancellationToken).ConfigureAwait(false);

            sent.Add(agent);
        }

        return sent;
    }

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
    /// SPEC.md §5.5 sends to a role whether or not its machines are awake, precisely so that
    /// work sent to something switched off waits and is flagged rather than silently omitted.
    /// </para>
    /// <para>
    /// Best effort. A notice is an optimisation over the drain the adapter would have done
    /// anyway, so failing to deliver one costs latency and never correctness — and a broker that
    /// failed a Result because it could not nudge the requester would be trading the durable
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
                    // Its last session: nothing left can report a fetch it was streamed (SPEC.md §9.5).
                    Fetches.Forget(attached.Name);
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
        // Before the gate and the connection go: a look it had scheduled would otherwise reach for both.
        await Watcher.DisposeAsync().ConfigureAwait(false);

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

        Files.Dispose();

        // Last, after the connection and its pool have let go of the file. Released any earlier, a
        // broker started the moment this one stopped could open a database this process still had
        // open -- two processes on one store, which is the thing the claim exists to stop.
        _storeLock?.Dispose();
    }
}
