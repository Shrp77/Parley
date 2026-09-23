using System.Globalization;
using Parley.Broker;
using Parley.Core;
using Parley.Server;
using Parley.Store;

namespace Parley.Cli;

/// <summary>
/// Runs the broker of SPEC.md §4: the one process that opens the store.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is the only command that touches a file, and the only one that migrates.</b> Everything
/// else here is a client. That division used to run the other way — the operator's tool opened
/// the database and the servers migrated it — and the reason it moved is not tidiness: SQLite in
/// WAL mode requires coherent shared memory between every process touching the file, which no
/// network filesystem provides, so more than one opener is a constraint on where the machines
/// can be rather than a performance note.
/// </para>
/// <para>
/// <b>Migration happens here and nowhere else.</b> It used to run in every adapter's launch
/// path, which meant a deployment's schema version was whatever the most recently updated
/// repository happened to carry, and two adapters at different versions raced to apply it. One
/// process that owns the file is also the one process entitled to change its shape.
/// </para>
/// </remarks>
public static class ServeCommand
{
    private const int Config = 78;     // EX_CONFIG
    private const int IoError = 74;    // EX_IOERR
    private const int Unavailable = 69; // EX_UNAVAILABLE

    /// <summary>
    /// Opens the store and serves it until the process is stopped.
    /// </summary>
    /// <param name="databasePath">The store, or <see langword="null"/> if nothing named one.</param>
    /// <param name="listenOn">Where to listen, or <see langword="null"/> for the default.</param>
    /// <param name="logPath">A file to copy output into, or <see langword="null"/> for none.</param>
    /// <param name="output">Where to report what it is doing.</param>
    /// <param name="error">Where to report why it could not.</param>
    /// <param name="whenListening">
    /// Something to run alongside the broker once it is listening, given the addresses it is
    /// listening on and the writer the broker reports through, or <see langword="null"/> to run
    /// nothing. Its exit code is folded into this one.
    /// </param>
    /// <param name="alongside">
    /// Something to run alongside the broker that is <em>not</em> bound to any one of them: the
    /// operator's console, which reads this machine's manifest rather than a single endpoint.
    /// Separate from <paramref name="whenListening"/> because that one joins exactly one broker
    /// and is refused across several projects, and a console is not.
    /// </param>
    /// <param name="cancellationToken">Stops the broker.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <remarks>
    /// <b>The hook is a callback rather than a reference to the node, and the indirection earns
    /// its keep.</b> Hosting this machine's node in the broker's process is what lets one
    /// supervisor entry cover a whole deployment — but a broker that knew how to be a client of
    /// itself would be a circular dependency in the one component SPEC.md §4 keeps free of them.
    /// What this class knows is that something may want to run once the port is open, which is
    /// the only fact it is in a position to report.
    /// </remarks>
    public static async Task<int> RunAsync(
        string? databasePath,
        string? listenOn,
        string? logPath,
        TextWriter output,
        TextWriter error,
        Func<IReadOnlyList<string>, TextWriter, CancellationToken, Task<int>>? whenListening = null,
        Func<IReadOnlyList<string>, TextWriter, CancellationToken, Task<int>>? alongside = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        // Copied rather than redirected, so a broker started from a terminal still prints to it
        // and one started by a supervisor still leaves a record. The alternative -- wrapping the
        // broker in a shell that redirects -- means quoting a path inside a command line inside
        // a task definition, for a feature whose absence is silent.
        using var log = Logging.Open(logPath, error);

        if (log is not null)
        {
            output = TextWriter.Synchronized(new TeeWriter(output, log));
            error = TextWriter.Synchronized(new TeeWriter(error, log));
        }
        else if (whenListening is not null)
        {
            // One broker means one sweep, which is why there is no log-less synchronisation here
            // ordinarily. A hosted node is a second writer on the same instance, running for as
            // long as the sweep does and saying more than it -- every agent it starts and stops.
            // A TextWriter is not thread-safe, and interleaved output is what an operator reads
            // to find out why a machine starts nothing.
            output = TextWriter.Synchronized(output);
        }

        // First, and before anything that can fail. A store this build cannot open, a port
        // already held, a manifest that will not parse -- each of those ends the process, and
        // the log then holds a failure with nothing saying which build produced it. The version
        // is most worth having in exactly the runs that print nothing else.
        await output.WriteLineAsync(BuildIdentity.Line).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(databasePath))
        {
            // Reached by `parley serve` with no project named and no store given. The way on is a project, which is how
            // a machine is deployed; the one-store options are named last and as what they are, because a refusal that
            // offered only them sent a person to set up a deployment by hand outside the manifest.
            await error.WriteLineAsync(
                "No database: no project is named, and no store was given. Name a project with " +
                "`parley project add <name> --db <path> --listen <url>`, and `parley serve` serves every project named " +
                "(`parley autostart install` does the same at every logon). --db <path> or " +
                $"{DeploymentEnvironment.DatabaseVariable} serves one store by hand, for testing only.").ConfigureAwait(false);

            return Config;
        }

        // Created if it is missing, unlike every other command here. This is the first run of a
        // project, and refusing it would leave nothing able to create the store at all now that
        // the adapters cannot.
        BrokerRuntime runtime;

        try
        {
            runtime = BrokerRuntime.Open(databasePath, BrokerOptions.Default, TimeProvider.System);
        }
        catch (StoreHeldException held)
        {
            // A duplicate launch, not a failure: a scheduled task and a terminal restoring its session
            // both start this. Nothing was written, and the broker already serving is the one to use,
            // so this says which process that is and goes.
            await output.WriteLineAsync(HeldElsewhere(databasePath, held)).ConfigureAwait(false);

            return 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync($"Could not open '{databasePath}': {exception.Message}")
                .ConfigureAwait(false);

            return IoError;
        }

        var endpoint = listenOn is { Length: > 0 } ? listenOn : BrokerEndpoint.Default;

        ParleyServer server;

        try
        {
            server = await ParleyServer.StartAsync(runtime, endpoint, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await runtime.DisposeAsync().ConfigureAwait(false);

            // Almost always the port, and almost always because a broker is already running.
            // Said plainly, because "address already in use" sends somebody looking for a
            // conflict with an unrelated program.
            await error.WriteLineAsync(
                $"Could not listen on {endpoint}: {exception.Message}. If a broker is already " +
                "running for this project, that is the one to use — there is one per project " +
                "and it owns the store.").ConfigureAwait(false);

            return IoError;
        }

        await using (server.ConfigureAwait(false))
        {
            await output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"Parley broker on {string.Join(", ", server.Addresses)}, store at {databasePath}, schema {SchemaMigrator.LatestVersion}."))
                .ConfigureAwait(false);

            // Said because somebody browsed to it. The broker's address is the first URL on the
            // screen and looks like the one to open, and what a browser gets there is "an HTTP/1.x
            // request was sent to an HTTP/2 only endpoint" -- an error about the wrong thing,
            // which reads as the deployment being broken rather than as the wrong address.
            await output.WriteLineAsync(
                "  That address is for agents and speaks gRPC. It is not a web page.")
                .ConfigureAwait(false);

            await output.WriteLineAsync(
                "Agents connect with PARLEY_AGENT set; machines join with `parley up`. Ctrl+C stops it.")
                .ConfigureAwait(false);

            // Started only now, and that ordering is the whole reason this is a callback rather
            // than two tasks kicked off together. A node does not retry its join: it reports "no
            // broker is listening" and exits, so a node racing the port it is about to connect to
            // loses that race often enough to matter and leaves the process serving a machine
            // that hosts nothing.
            var hosted = HostAsync(
                whenListening, alongside, server.Addresses, output, cancellationToken);

            await Task.WhenAll(SweepAsync(runtime, output, cancellationToken), hosted)
                .ConfigureAwait(false);

            return await hosted.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs one broker per configured project, in this process.
    /// </summary>
    /// <param name="projects">The projects to serve.</param>
    /// <param name="logPath">Where to copy output, or null.</param>
    /// <param name="output">Where to report what is running.</param>
    /// <param name="error">Where to report what is not.</param>
    /// <param name="nodeFor">
    /// Something to run for each project once its broker is listening — this machine's node,
    /// joined to that project — given the project, its addresses and the writer to report
    /// through, or <see langword="null"/> to host no node. Each is contained on its own.
    /// </param>
    /// <param name="alongside">
    /// Something to run alongside the brokers that is <em>not</em> bound to any one of them: the
    /// operator's console, which reads this machine's manifest rather than a single endpoint.
    /// Run once, and handed the first started broker's addresses as its fallback.
    /// </param>
    /// <param name="reread">
    /// Reads the manifest again, returning null when it has not changed or cannot be read, or
    /// <see langword="null"/> to serve only what was given. Given, a project named while this
    /// runs gets its broker within <see cref="WatchInterval"/>.
    /// </param>
    /// <param name="cancellationToken">Stops every broker.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <remarks>
    /// <para>
    /// SPEC.md §4.1: "One process may host many brokers." Isolation is unaffected because it lives
    /// in the storage layer — separate files, separate connections — not in the process boundary.
    /// What sharing a process changes is only what a process-fatal fault takes with it, which is
    /// the risk any single service carries.
    /// </para>
    /// <para>
    /// <b>A failure is contained and reported, and the exit code does not lie about it.</b> §4.1
    /// requires a corrupt database or a failed migration to stop that broker while the others keep
    /// running; what it does not settle is what the process tells its supervisor, and that is the
    /// part an operator cannot see afterwards. A run that starts two of three brokers and exits
    /// zero is the same shape as the defects this repository has already met twice — an unreadable
    /// catalogue read as empty, ten repositories written where nothing reads them — where a run
    /// reported success while doing less than it claimed.
    /// </para>
    /// <para>
    /// <b>A node is hosted per project, and each one joins its own broker.</b> An earlier version
    /// refused to host a node across several projects, reasoning from §4.1.1 that a machine has
    /// one identity and a process serving three projects could not choose which deployment it
    /// belonged to. That reasoning was right about the choosing and wrong about who makes it: the
    /// manifest names a catalogue <i>per project</i> — "the catalogue of agents this machine may
    /// start for it", §4.1 — so the projects this machine belongs to are exactly the ones that
    /// say so. Each project's registry is its own file, so the same machine name in two of them
    /// collides no more than the same agent name does. What is still refused, at the entry point,
    /// is one explicit catalogue offered to several projects, because that is the choice nobody
    /// made.
    /// </para>
    /// </remarks>
    public static async Task<int> RunAsync(
        IReadOnlyList<ProjectSettings> projects,
        string? logPath,
        TextWriter output,
        TextWriter error,
        Func<ProjectSettings, IReadOnlyList<string>, TextWriter, CancellationToken, Task<int>>? nodeFor = null,
        Func<IReadOnlyList<string>, TextWriter, CancellationToken, Task<int>>? alongside = null,
        Func<IReadOnlyList<ProjectSettings>?>? reread = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        using var log = Logging.Open(logPath, error);

        if (log is not null)
        {
            output = new TeeWriter(output, log);
            error = new TeeWriter(error, log);
        }

        // Synchronised whether or not there is a log, which the single-project overload does not
        // need to be: there is one sweep there and N here, all writing the same failure line to
        // the same writer. A TextWriter is not thread-safe, and the output that would be
        // interleaved is the output an operator reads to find out which broker is unwell.
        output = TextWriter.Synchronized(output);
        error = TextWriter.Synchronized(error);

        // Said once for the process rather than once per project: one build serves all of them,
        // and a line repeated per project would read as though it could differ. Before the first
        // store is opened, for the reason the single-store overload gives.
        await output.WriteLineAsync(BuildIdentity.Line).ConfigureAwait(false);

        // Every project serving right now. Grows when the watcher starts one named since the
        // process began and shrinks when it stops one removed, so it is read under its own lock.
        var started = new List<Hosted>();
        var failed = 0;

        // Starts one project's broker and everything that lives beside it. Called for the
        // initial set and again by the watcher for a project named while this runs; the two
        // never overlap, because the watcher starts after this loop is over.
        async Task<Start> StartAsync(ProjectSettings project)
        {
            BrokerRuntime runtime;

            try
            {
                runtime = BrokerRuntime.Open(project.Store, BrokerOptions.Default, TimeProvider.System);
            }
            catch (StoreHeldException held)
            {
                // Not a failure. Another launcher got there first -- a scheduled task and a terminal
                // restoring its session both start this at logon -- and the store is being served.
                await output.WriteLineAsync(
                    HeldElsewhere($"{project.Name}: its store at {project.Store}", held))
                    .ConfigureAwait(false);

                return Start.Held;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await error.WriteLineAsync(
                    $"{project.Name}: could not open '{project.Store}': {exception.Message}")
                    .ConfigureAwait(false);

                return Start.Failed;
            }

            ParleyServer server;

            try
            {
                server = await ParleyServer.StartAsync(runtime, project.Endpoint, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Disposed on every path out, an interrupt included: this runtime is not in
                // `started` yet, so the finally below has never heard of it and the open
                // database handle would go with the process rather than be released.
                await runtime.DisposeAsync().ConfigureAwait(false);

                if (exception is OperationCanceledException)
                {
                    throw;
                }

                await error.WriteLineAsync(
                    $"{project.Name}: could not listen on {project.Endpoint}: {exception.Message}")
                    .ConfigureAwait(false);

                return Start.Failed;
            }

            // Its own source, linked to the interrupt, so that this one project can be stopped
            // when its entry is removed while every other keeps serving -- and the interrupt
            // still stops them all.
            var hosted = new Hosted(
                project, server, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));

            await output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"{project.Name} on {string.Join(", ", server.Addresses)}, store at {project.Store}, schema {SchemaMigrator.LatestVersion}."))
                .ConfigureAwait(false);

            // The sweep and, where there is one, the node, each contained on its own so that one
            // project's node failing leaves the others hosted.
            hosted.Tasks.Add(Quietly(SweepAsync(runtime, output, hosted.Stopping.Token)));

            if (nodeFor is not null)
            {
                hosted.Tasks.Add(ContainAsync(
                    () => nodeFor(project, server.Addresses, output, hosted.Stopping.Token),
                    $"{project.Name}: this machine's node",
                    output,
                    hosted.Stopping.Token));
            }

            lock (started)
            {
                started.Add(hosted);
            }

            return Start.Serving;
        }

        // Stops one project's broker, its sweep and its node, and releases its store. What it
        // does not do is touch the store: removal is withdrawal (`project remove` says so), and
        // the one command that deletes says "destroy" in its name.
        async Task StopAsync(Hosted hosted, string why)
        {
            lock (started)
            {
                _ = started.Remove(hosted);
            }

            await output.WriteLineAsync(
                $"{hosted.Project.Name}: {why}; stopping its broker. Its store at " +
                $"{hosted.Project.Store} is untouched.").ConfigureAwait(false);

            await hosted.Stopping.CancelAsync().ConfigureAwait(false);

            try
            {
                _ = await Task.WhenAll(hosted.Tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The sweep ending the way a cancelled sweep ends.
            }

            await hosted.Server.DisposeAsync().ConfigureAwait(false);
            hosted.Stopping.Dispose();
        }

        try
        {
            // Inside the try, and that is the whole of why this loop is not where it reads most
            // naturally. Ctrl+C during start-up cancels the pending StartAsync, which throws
            // OperationCanceledException — deliberately not caught, because an interrupt is not a
            // project failing. Started outside the try, that exception left every broker already
            // running still holding its port and its store handle: the interrupt arrives, the
            // process exits, and nothing has been shut down. Which is FR-021 exactly, and the
            // defect fixed one commit earlier, reappearing in the window where it is least
            // visible — a machine that only ever starts one project never enters it.
            // Remembered by name, because the watcher may start one of these later -- a port
            // still held by the previous broker at logon is the usual reason -- and a project
            // serving at the end is not one that failed, whatever it did at the start.
            var notStarted = new HashSet<string>(StringComparer.Ordinal);

            var servedElsewhere = 0;

            foreach (var project in projects)
            {
                switch (await StartAsync(project).ConfigureAwait(false))
                {
                    case Start.Serving:
                        break;

                    case Start.Held:
                        servedElsewhere++;
                        break;

                    default:
                        failed++;
                        _ = notStarted.Add(project.Name.Value);
                        break;
                }
            }

            // **A manifest that names nothing yet, being watched, is a machine waiting for its first project**, not a
            // failure. The installer registers exactly this entry before anybody has named a project, and a person
            // names one from `parley terminal` or `parley project add`; the watcher starts it within seconds. Without a
            // watcher nothing could ever start here, and that stays the failure below says it is.
            var awaitingFirstProject = projects.Count == 0 && reread is not null;

            if (started.Count == 0 && !awaitingFirstProject)
            {
                // Everything named is already served by another broker: a second launcher at logon,
                // and a deployment that is working. Anything that genuinely failed beside it makes
                // this a failure again.
                if (failed == 0 && servedElsewhere > 0)
                {
                    await output.WriteLineAsync(
                        "Every project here is already being served by another Parley broker. Nothing to do; exiting.")
                        .ConfigureAwait(false);

                    return 0;
                }

                await error.WriteLineAsync(
                    "No broker started. Nothing is serving.").ConfigureAwait(false);

                return Unavailable;
            }

            if (failed > 0)
            {
                // Said again after the list, because the failures scrolled past while the
                // successes were being printed, and this is the line that decides whether an
                // operator investigates.
                await output.WriteLineAsync(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{failed} of {projects.Count} projects did not start. This process is serving a subset."))
                    .ConfigureAwait(false);
            }

            if (awaitingFirstProject)
            {
                await output.WriteLineAsync(
                    "No project is named yet. Name the first one from `parley terminal`, or with `parley project add " +
                    "<name> --db <path> --listen <url>`; it starts here within seconds. Ctrl+C stops this.")
                    .ConfigureAwait(false);
            }
            else
            {
                // Said here too. Several addresses rather than one makes the confusion likelier, not
                // less: whichever a person clicks, none of them is a web page.
                await output.WriteLineAsync(
                    "  Those addresses are for agents and speak gRPC. None of them is a web page.")
                    .ConfigureAwait(false);

                await output.WriteLineAsync(
                    "Agents connect with PARLEY_AGENT set; machines join with `parley up`. Ctrl+C stops them all.")
                    .ConfigureAwait(false);
            }

            // What runs beside the brokers rather than inside one of them: the web UI once,
            // against the first started broker as its fallback -- it reads the manifest for the
            // rest -- and the watcher. The web UI is not contained here: the entry point
            // contains it, and says what it does when a port is taken.
            var beside = new List<Task<int>>();

            if (alongside is not null)
            {
                // No broker to fall back to while no project is named: the web UI reads the manifest and says so.
                beside.Add(alongside(started.Count > 0 ? started[0].Server.Addresses : [], output, cancellationToken));
            }

            if (reread is not null)
            {
                beside.Add(WatchAsync(reread, StartAsync, StopAsync, started, output, cancellationToken));
            }

            // Until the interrupt. Nothing else ends this process: a project's own tasks end when
            // it is stopped and the others carry on, so waiting on them would either end too
            // early or need re-collecting every time the watcher changed the set.
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The interrupt, which is how this is meant to end.
            }

            // Every source is linked to the interrupt, so everything is now ending; awaited so
            // that a node's exit code still reaches the process's, and so that nothing is still
            // writing when the servers below are disposed.
            List<Task<int>> everything;

            lock (started)
            {
                everything = [.. beside, .. started.SelectMany(h => h.Tasks)];
            }

            // What the exit code says is the state at the end: a project that failed at start
            // and was serving by the time this stopped is not a failure to report to a
            // supervisor, and a node that exited non-zero is.
            lock (started)
            {
                failed = notStarted.Count(name =>
                    !started.Exists(h => string.Equals(h.Project.Name.Value, name, StringComparison.Ordinal)));
            }

            try
            {
                var codes = await Task.WhenAll(everything).ConfigureAwait(false);

                failed += codes.Count(code => code != 0);
            }
            catch (OperationCanceledException)
            {
                // A sweep ending the way a cancelled sweep ends.
            }
        }
        finally
        {
            // Everything started is stopped, including when one sweep threw. A broker left holding
            // a port after the process was told to stop is the failure Ctrl+C was just fixed for,
            // reappearing one level up.
            Hosted[] remaining;

            lock (started)
            {
                remaining = [.. started];
            }

            foreach (var hosted in remaining)
            {
                await hosted.Server.DisposeAsync().ConfigureAwait(false);
                hosted.Stopping.Dispose();
            }
        }

        return failed > 0 ? Unavailable : 0;
    }

    /// <summary>One project this process is serving, and everything that lives beside its broker.</summary>
    /// <param name="Project">What it is.</param>
    /// <param name="Server">The broker, listening.</param>
    /// <param name="Stopping">Ends this project alone; linked to the interrupt, which ends them all.</param>
    private sealed record Hosted(ProjectSettings Project, ParleyServer Server, CancellationTokenSource Stopping)
    {
        /// <summary>Its sweep and, where there is one, its node.</summary>
        public List<Task<int>> Tasks { get; } = [];
    }

    /// <summary>How often the manifest is looked at for a project named since the start.</summary>
    /// <remarks>
    /// Two seconds is the time between somebody pressing Enter on the naming form and the list
    /// they return to showing the project answering. Reading a small JSON file that often costs
    /// nothing worth measuring, and the entry point reads it only when its stamp has moved.
    /// </remarks>
    internal static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(2);

    /// <summary>What a broker says when it finds its store already being served, before it exits.</summary>
    /// <param name="label">The store, or the project and its store.</param>
    /// <param name="held">The refusal.</param>
    /// <returns>The line.</returns>
    /// <remarks>
    /// <b>It names the process, because that is the question the owner had to answer by hand.</b>
    /// Seventeen agents read as stranded and the cause was a second broker started by a terminal
    /// restoring its session; nothing on screen or in the log said a second process existed. The log
    /// is shared now, so this line reaches it even from the process that is leaving.
    /// </remarks>
    internal static string HeldElsewhere(string label, StoreHeldException held)
    {
        ArgumentNullException.ThrowIfNull(held);

        var who = held.Owner is { } owner
            ? "process " + owner.ProcessId.ToString(CultureInfo.InvariantCulture)
              + ", started " + owner.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
              + (owner.Build is { } build ? ", parley " + build : string.Empty)
            : "a process whose details could not be read";

        return $"{label} is already being served by another Parley broker ({who}), so this process is " +
               "not serving it.";
    }

    /// <summary>What became of starting one project's broker.</summary>
    private enum Start
    {
        /// <summary>It is serving, from this process.</summary>
        Serving,

        /// <summary>Another broker already serves its store, so this process does not.</summary>
        Held,

        /// <summary>Its store could not be opened for another reason, or its endpoint listened on.</summary>
        Failed,
    }

    /// <summary>Keeps what is serving in step with what the manifest names.</summary>
    /// <param name="reread">Reads the manifest again, or returns null when it has not changed or cannot be read.</param>
    /// <param name="start">Starts one project, and says what became of it.</param>
    /// <param name="stop">Stops one project, for the reason given.</param>
    /// <param name="started">Every project serving right now.</param>
    /// <param name="output">Where to say that a project was named since the start.</param>
    /// <param name="cancellationToken">Ends the watch.</param>
    /// <returns>Zero, always: a project that will not start is reported by <paramref name="start"/>.</returns>
    /// <remarks>
    /// <para>
    /// <b>A project has its own broker, so naming one is asking for that broker now — and
    /// removing one is asking for it to stop.</b> SPEC.md §4.1 gives every project a store and
    /// an endpoint of its own and lets one process host them all; the process that hosts them is
    /// this one, and before this it read the manifest once at start. A project named from the
    /// terminal was therefore "not answering" until the next logon, with the form politely
    /// saying so — which is documenting a gap rather than closing it, and was said to be exactly
    /// that by the first person it happened to. Removal followed for the same reason: the only
    /// way to stop one project's broker was to restart every other project's with it.
    /// </para>
    /// <para>
    /// Stopping is not deleting. A removed project's store stays where it is, as <c>project
    /// remove</c> has always said; <c>remove --destroy</c> is the command that deletes, and it
    /// waits for this to have stopped the broker first. A running project whose store, endpoint
    /// or catalogue changed is stopped and started on the new entry, because the manifest is
    /// what this serves and a changed entry is a different broker. And a project that would not
    /// start is tried once per change to its entry rather than once per tick, because a store
    /// that is a directory does not become a file by being asked again every two seconds, and
    /// the failure line would fill the log.
    /// </para>
    /// </remarks>
    private static async Task<int> WatchAsync(
        Func<IReadOnlyList<ProjectSettings>?> reread,
        Func<ProjectSettings, Task<Start>> start,
        Func<Hosted, string, Task> stop,
        List<Hosted> started,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        // What was tried and did not start, so it is not tried again until its entry changes.
        // Records compare by value, so a changed entry is a different member.
        var failing = new HashSet<ProjectSettings>();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(WatchInterval, cancellationToken).ConfigureAwait(false);

                IReadOnlyList<ProjectSettings>? current;

                try
                {
                    current = reread();
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // An unreadable manifest is reported by every command that reads it, to the
                    // person editing it. Said again here every two seconds it would be noise --
                    // and, more to the point, nothing is stopped on the strength of a file
                    // somebody is halfway through editing.
                    current = null;
                }

                if (current is null)
                {
                    continue;
                }

                Hosted[] serving;

                lock (started)
                {
                    serving = [.. started];
                }

                // Removed or changed first, so a project removed and re-added under the same
                // name in one edit -- or given a new port or store with `project set` -- is
                // stopped and started rather than left as it was. Found by probing: matched by
                // name alone, a re-added "billing" was "already serving" and stayed on the old
                // entry's port for ever. The description is not compared, because a broker does
                // not run differently for having been described.
                var restarting = new HashSet<string>(StringComparer.Ordinal);

                foreach (var hosted in serving)
                {
                    var now = current.FirstOrDefault(p => SameName(p, hosted.Project));

                    if (now is null)
                    {
                        await stop(hosted, "removed from the manifest").ConfigureAwait(false);
                    }
                    else if (!SameDeployment(now, hosted.Project))
                    {
                        await stop(hosted, "its entry changed").ConfigureAwait(false);
                        _ = restarting.Add(hosted.Project.Name.Value);
                    }
                }

                _ = failing.RemoveWhere(f => !current.Any(p => SameName(p, f)));

                foreach (var project in current)
                {
                    bool already;

                    lock (started)
                    {
                        already = started.Exists(h => SameDeployment(h.Project, project));
                    }

                    if (already || failing.Contains(project))
                    {
                        continue;
                    }

                    await output.WriteLineAsync(
                        restarting.Contains(project.Name.Value)
                            ? $"{project.Name}: starting it on its changed entry."
                            : $"{project.Name}: named in the manifest since this started; starting it.")
                        .ConfigureAwait(false);

                    // Held goes with failed here: another broker serves it, and a project is tried
                    // again only when its entry changes -- the rule a busy port already follows.
                    if (await start(project).ConfigureAwait(false) != Start.Serving)
                    {
                        _ = failing.Add(project);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The interrupt, which is how this is meant to end.
        }

        return 0;

        static bool SameName(ProjectSettings a, ProjectSettings b) =>
            string.Equals(a.Name.Value, b.Name.Value, StringComparison.Ordinal);

        // The same name serving the same store on the same endpoint from the same catalogue:
        // the parts a broker is made of. A description is not one of them.
        static bool SameDeployment(ProjectSettings a, ProjectSettings b) =>
            SameName(a, b)
            && string.Equals(a.Store, b.Store, StringComparison.Ordinal)
            && string.Equals(a.Endpoint, b.Endpoint, StringComparison.Ordinal);
    }

    /// <summary>Runs whatever wanted the port to be open, and survives it failing.</summary>
    /// <param name="whenListening">What to run, or null to run nothing.</param>
    /// <param name="alongside">What to run that is not bound to one broker, or null.</param>
    /// <param name="addresses">Where the broker is listening.</param>
    /// <param name="output">Where to report a failure worth seeing.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>Its exit code, or zero when there was nothing to run.</returns>
    /// <remarks>
    /// <b>Contained, for the same reason §4.1 contains a failing project.</b> Hosting this
    /// machine's node in the broker's process is a deployment convenience, and it must not become
    /// a way for a node to take the broker down: a node fault would otherwise propagate out of
    /// the sweep's <c>WhenAll</c> and end a process that every agent in the deployment is
    /// connected to, converting one machine's supervision problem into everybody's outage.
    /// <para>
    /// The exit code still carries it. A run that hosted nothing must not report success — that
    /// is the same lie as a run that started two of three brokers and exited zero.
    /// </para>
    /// </remarks>
    private static async Task<int> HostAsync(
        Func<IReadOnlyList<string>, TextWriter, CancellationToken, Task<int>>? whenListening,
        Func<IReadOnlyList<string>, TextWriter, CancellationToken, Task<int>>? alongside,
        IReadOnlyList<string> addresses,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (alongside is not null)
        {
            // Both, running together rather than one waiting out the other: each is meant to last
            // as long as the broker does. Recursed with a null `alongside` so the containment
            // below applies to the node exactly as it did before this parameter existed.
            var beside = alongside(addresses, output, cancellationToken);

            var codes = await Task.WhenAll(
                beside,
                HostAsync(whenListening, null, addresses, output, cancellationToken))
                .ConfigureAwait(false);

            return Array.Find(codes, code => code != 0);
        }

        if (whenListening is null)
        {
            return 0;
        }

        return await ContainAsync(
            () => whenListening(addresses, output, cancellationToken),
            "This machine's node",
            output,
            cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>A sweep, as a task that reports nothing: it ends only when the process does.</summary>
    /// <param name="sweep">The sweep.</param>
    /// <returns>Zero once it has ended.</returns>
    private static async Task<int> Quietly(Task sweep)
    {
        await sweep.ConfigureAwait(false);

        return 0;
    }

    /// <summary>Runs a hosted node and survives it failing.</summary>
    /// <param name="run">The node.</param>
    /// <param name="who">How to name it in the failure line.</param>
    /// <param name="output">Where to report a failure worth seeing.</param>
    /// <param name="stopping">The token the node was given, so that its ending on it is not a failure.</param>
    /// <returns>Its exit code, or the unavailable code when it threw.</returns>
    private static async Task<int> ContainAsync(
        Func<Task<int>> run, string who, TextWriter output, CancellationToken stopping)
    {
        try
        {
            // The broker's writer, not the console. Under a supervisor there is no console at
            // all, so a node reporting to one reports to nothing -- and what it reports is
            // exactly what somebody debugging a machine that starts nothing needs: which
            // catalogue it read, what it can start, and which registered agents are missing from
            // it. Writing where the broker writes puts all of that in --log.
            return await run().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The interrupt, which is how this is meant to end.
            return 0;
        }
        catch (Exception) when (stopping.IsCancellationRequested)
        {
            // Also how it is meant to end. A node cancelled mid-call sees its gRPC call
            // cancelled, and that arrives as an RpcException rather than the cancellation it
            // is -- so a project stopped on purpose was reported as its node having failed,
            // "nothing will be started until `parley up` runs again", and the process exited
            // non-zero for a stop it had been asked for. Found by probing the watcher.
            return 0;
        }
        catch (Exception failure)
        {
            await output.WriteLineAsync(
                $"{who} stopped: {failure.Message}. The broker is still serving; " +
                "nothing on this machine will be started until `parley up` runs again.")
                .ConfigureAwait(false);

            return Unavailable;
        }
    }

    /// <summary>Runs the sweep of SPEC.md §6 until the broker stops.</summary>
    /// <param name="runtime">The broker state.</param>
    /// <param name="output">Where to report a failure worth seeing.</param>
    /// <param name="cancellationToken">Stops the sweep.</param>
    /// <returns>A task that completes when it stops.</returns>
    /// <remarks>
    /// <para>
    /// One sweep for the deployment, which is the arrangement that first becomes possible here.
    /// It used to run inside every adapter, so twenty repositories ran twenty sweeps over one
    /// file — each taking the write lock for the same work, and none of them wrong in a way anybody
    /// could see.
    /// </para>
    /// <para>
    /// A failing pass is reported and the loop continues. The pass concludes polls whose last
    /// decline lapsed, settles instructions nothing will report on and applies retention, so
    /// stopping on the first failure would leave all three undone for as long as the broker ran —
    /// and the failures worth surviving are the transient ones a busy file produces.
    /// </para>
    /// </remarks>
    private static async Task SweepAsync(
        BrokerRuntime runtime,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Interval());

        try
        {
            do
            {
                try
                {
                    _ = await runtime.TickAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    await output.WriteLineAsync($"Sweep failed: {exception.Message}")
                        .ConfigureAwait(false);
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C, which is how this command is meant to end.
        }
    }

    /// <summary>How often to sweep.</summary>
    /// <returns>The interval.</returns>
    /// <remarks>
    /// Bounded rather than trusted. A pass costs a handful of indexed queries, and a mistyped
    /// "0" would turn that into a spin against the one file the whole deployment depends on.
    /// </remarks>
    private static TimeSpan Interval()
    {
        var configured = Environment.GetEnvironmentVariable("PARLEY_TICK_SECONDS");

        return int.TryParse(
            configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 3600))
            : TimeSpan.FromSeconds(5);
    }
}
