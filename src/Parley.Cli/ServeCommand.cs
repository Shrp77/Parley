using System.Globalization;
using Parley.Broker;
using Parley.Configuration;
using Parley.Core;
using Parley.Server;
using Parley.Setup;
using Parley.Store;

namespace Parley.Cli;

/// <summary>
/// Runs the broker of SPEC.md §4: the one process that opens the store.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is the only command that touches a file, and the only one that migrates.</b> Everything
/// else here is a client. SQLite in WAL mode requires coherent shared memory between every
/// process touching the file, which no network filesystem provides, so more than one opener is a
/// constraint on where the machines can be rather than a performance note.
/// </para>
/// <para>
/// <b>Migration happens here and nowhere else.</b> One process that owns the file is also the one
/// process entitled to change its shape; adapters at different versions would otherwise race to
/// apply it.
/// </para>
/// </remarks>
public static class ServeCommand
{
    private const int Config = 78;     // EX_CONFIG
    private const int IoError = 74;    // EX_IOERR
    private const int Unavailable = 69; // EX_UNAVAILABLE

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
    /// web UI, which reads this machine's manifest rather than a single endpoint.
    /// Run once, and handed the first started broker's addresses as its fallback.
    /// </param>
    /// <param name="reread">
    /// Reads the manifest again, returning null when it has not changed or cannot be read, or
    /// <see langword="null"/> to serve only what was given. Given, a project named while this
    /// runs gets its broker within <see cref="WatchInterval"/>.
    /// </param>
    /// <param name="checkInstall">
    /// Whether to ask the other Parley programs on this machine what release they are, and warn about any that is
    /// not the broker's. Off by default so a test's broker starts no processes.
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
    /// zero would report success while doing less than it claimed.
    /// </para>
    /// <para>
    /// <b>A node is hosted per project, and each one joins its own broker.</b> There is one
    /// catalogue per machine (§4.1), so every project announces what the repositories say it may
    /// start. Each project's registry is its own file, so the same machine name in two of them
    /// collides no more than the same agent name does.
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
        bool checkInstall = false,
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

        // Synchronised whether or not there is a log: there are N sweeps, all writing the same
        // failure line to the same writer. A TextWriter is not thread-safe, and the output that
        // would be interleaved is the output an operator reads to find out which broker is unwell.
        output = TextWriter.Synchronized(output);
        error = TextWriter.Synchronized(error);

        // Said once for the process rather than once per project: one build serves all of them,
        // and a line repeated per project would read as though it could differ. Before the first
        // store is opened, so it appears even when no store opens.
        await output.WriteLineAsync(BuildIdentity.Line).ConfigureAwait(false);

        // **Asked of the other programs on this machine, in the background**: the parts of a
        // deployment are installed one at a time and a release can change what they share. Off for a test's broker, which
        // must not start processes, and never awaited here, so it cannot be why a broker does not start.
        if (checkInstall)
        {
            _ = Task.Run(() => InstalledTools.CheckAsync(output, cancellationToken), CancellationToken.None);
        }

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
                runtime = BrokerRuntime.Open(project.Name, project.Store, BrokerOptions.Default, TimeProvider.System);
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

            // **A store that is not the shape this build expects is said here, where the broker's log is read**, and again
            // to every surface that asks what this broker speaks. The schema number cannot say it while the schema is
            // edited in place.
            if (runtime.StoreProblems.Count > 0)
            {
                await output.WriteLineAsync(
                    $"{project.Name}: WARNING -- its store is not the shape this build expects: "
                    + string.Join("; ", runtime.StoreProblems.Take(5))
                    + (runtime.StoreProblems.Count > 5 ? $"; and {runtime.StoreProblems.Count - 5} more" : string.Empty)
                    + ". It was made by another build. Reset the project's store, or add what is missing.")
                    .ConfigureAwait(false);
            }

            if (!OnlyThisMachine(project.Endpoint))
            {
                await output.WriteLineAsync(
                    $"{project.Name}: WARNING -- this project answers on {project.Endpoint}, which is not "
                    + "loopback, so anything that can reach that address can claim to be any agent in it, "
                    + "read its mail, export its conversations and start agents. The wire "
                    + "carries no credentials. No command puts a project here; reach a broker from another "
                    + "machine with a tunnel instead (README, \"For an agent on another machine\").")
                    .ConfigureAwait(false);
            }

            // The sweep and, where there is one, the node, each contained on its own so that one
            // project's node failing leaves the others hosted.
            hosted.Tasks.Add(Quietly(SweepAsync(runtime, output, hosted.Stopping.Token)));
            hosted.Tasks.Add(Quietly(FacilitateAsync(runtime, project.Name.Value, output, hosted.Stopping.Token)));

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
            // Inside the try so that Ctrl+C during start-up, which cancels the pending StartAsync and
            // throws OperationCanceledException (deliberately not caught: an interrupt is not a
            // project failing), still reaches the finally that shuts down every broker already
            // running and holding its port and store handle.
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

            // **One rule, applied twice: stay up while something could still change.** A watcher is
            // what "could still change" means here — it re-reads the root and starts what appears,
            // so a project named next, or a store repaired next, is served without anything being
            // restarted. Where there is no watcher nothing can ever change, and a process that
            // stayed up would be a process achieving nothing for ever.
            //
            // A machine whose projects can none of them be opened, or which has no projects, stays
            // up and reports why: every store unreadable is a moved directory or a permissions
            // change somebody is actively repairing, and a scheduled task does not restart a
            // process that ran and stopped. Not "always stay up", which would have a broker report
            // healthy to its supervisor while serving nothing where nothing is watching.
            var watching = reread is not null;
            var awaitingFirstProject = projects.Count == 0 && watching;

            if (started.Count == 0)
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

                if (!watching)
                {
                    await error.WriteLineAsync(
                        "No broker started. Nothing is serving.").ConfigureAwait(false);

                    return Unavailable;
                }
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
                    "No project is named yet. Name the first one from `parley` — it gets " +
                    "its own store and its own port, and starts here within seconds. Ctrl+C stops this.")
                    .ConfigureAwait(false);
            }
            else if (started.Count == 0)
            {
                // **Staying up, and saying so without claiming to serve.** The distinction is the
                // whole of what this line is for: a person reading it must not take "still running"
                // for "working". The failures were already named one by one with their stores, so
                // this says what the process is now doing and what would change it.
                await output.WriteLineAsync(
                    "Nothing is being served: no project here could be opened. The failures are "
                    + "named above, with the store each one could not open. This is still watching "
                    + "the root, so a store repaired or a project named is served within seconds "
                    + "without restarting anything. Ctrl+C stops this.")
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
            // a port after the process was told to stop would outlive the interrupt.
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

    /// <summary>The <c>reread</c> a served root is watched through: its projects when they changed, or null.</summary>
    /// <param name="root">The machine's root.</param>
    /// <returns>A function giving the projects when they changed and read cleanly, and null otherwise.</returns>
    /// <remarks>
    /// <para>
    /// <b>Nothing changes on a read that left anything out, and the stamp is kept only from a clean one.</b> A project.json
    /// mid-replace is briefly unreadable and reading leaves it out; taking that as the project being removed would stop its
    /// broker. With the stamp unchanged the next tick reads again, until the read is clean.
    /// </para>
    /// <para>
    /// The cost is recorded rather than hidden: while any project's file stays unreadable, no change to any other is
    /// applied either. That is the promise <see cref="WatchAsync"/> already made — nothing is stopped on the strength of a
    /// file somebody is halfway through editing — kept the only way it can be.
    /// </para>
    /// <para>
    /// Here rather than inline in the program so the test drives the function the program uses, not a copy of it.
    /// </para>
    /// </remarks>
    internal static Func<IReadOnlyList<ProjectSettings>?> Rereader(string root)
    {
        var held = ProjectFolder.StampOf(root);

        return () =>
        {
            var stamp = ProjectFolder.StampOf(root);

            if (string.Equals(stamp, held, StringComparison.Ordinal))
            {
                return null;
            }

            if (!ProjectManifest.TryRead(root, out var current, out var leftOut) || leftOut is { Length: > 0 })
            {
                return null;
            }

            held = stamp;

            return current;
        };
    }

    /// <summary>What a broker says when it finds its store already being served, before it exits.</summary>
    /// <param name="label">The store, or the project and its store.</param>
    /// <param name="held">The refusal.</param>
    /// <returns>The line.</returns>
    /// <remarks>
    /// <b>It names the process, because "who holds it" is the question a refused launcher raises.</b>
    /// A second broker started by a terminal restoring its session leaves agents reading as
    /// stranded unless something says a second process exists. The log is shared, so this line
    /// reaches it even from the process that is leaving.
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
    /// this one, so a project named from the terminal answers within seconds and removing one
    /// does not restart every other project's broker.
    /// </para>
    /// <para>
    /// Stopping is not deleting. A removed project's store stays where it is; <c>remove --destroy</c>
    /// is the command that deletes, and it waits for this to have stopped the broker first. A
    /// running project whose store or endpoint changed is stopped and started on the new entry, because the manifest is
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
                // stopped and started rather than left as it was: matched by name alone, a
                // re-added "billing" would stay on the old entry's port for ever. The description
                // is not compared, because a broker does not run differently for having been described.
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

        // The same name serving the same store on the same endpoint: the parts a broker is
        // made of. A description is not one of them.
        static bool SameDeployment(ProjectSettings a, ProjectSettings b) =>
            SameName(a, b)
            && string.Equals(a.Store, b.Store, StringComparison.Ordinal)
            && string.Equals(a.Endpoint, b.Endpoint, StringComparison.Ordinal);
    }

    /// <summary>Whether a project's endpoint keeps its broker on the machine it runs on.</summary>
    /// <param name="endpoint">The endpoint the project records.</param>
    /// <returns><see langword="true"/> when nothing off this machine can reach it.</returns>
    /// <remarks>
    /// <para>
    /// <b>No command writes a non-loopback endpoint, which is exactly why this is checked.</b> A
    /// project is handed a loopback port when it is named and nothing offers to change it, so the
    /// only way to arrive here is somebody editing <c>project.json</c> by hand. That is permitted —
    /// SPEC.md §16 allows a private network the operator controls — and it is also the single most
    /// consequential edit anybody can make to this deployment, because all three services share the
    /// one port: reaching it grants the operator operations, which export conversations, purge
    /// agents and start others.
    /// </para>
    /// <para>
    /// <b>Said on every start rather than once at the edit.</b> Whoever made the change is not
    /// necessarily whoever reads the log six months later, and a broker started by a logon entry
    /// has no console at all — the log is the only place the fact exists. Warning rather than
    /// refusing, because refusing would break a deployment on a controlled network that the
    /// protocol explicitly permits.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// The rule is <see cref="BrokerEndpoint.OnlyThisMachine"/>, shared with `project list`:
    /// a listing and a start-up warning must not disagree about whether an endpoint is exposed.
    /// </remarks>
    internal static bool OnlyThisMachine(string endpoint) =>
        BrokerEndpoint.OnlyThisMachine(endpoint);

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
            // is; a project stopped on purpose is not a node failure.
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
    /// One sweep for the deployment: running it in every adapter would have twenty repositories
    /// taking the write lock for the same work.
    /// </para>
    /// <para>
    /// A failing pass is reported and the loop continues. The pass concludes polls whose last
    /// decline lapsed, and applies retention, so
    /// stopping on the first failure would leave both undone for as long as the broker ran —
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

    /// <summary>
    /// Parley's own loop for one project (SPEC.md principle 2.11): asks every attached agent that is due what it owns,
    /// decides what becomes of each answer, and refreshes an agent whose first line was accepted automatically.
    /// </summary>
    /// <param name="runtime">The project's broker.</param>
    /// <param name="project">The project's name, which is the launch configuration's key.</param>
    /// <param name="output">Where to report.</param>
    /// <param name="cancellationToken">Stops the loop.</param>
    /// <returns>A task that completes when it stops.</returns>
    /// <remarks>
    /// <para>
    /// <b>Here because this is the one host that can do both halves</b>: drive the broker, and write
    /// a repository's launch configuration through <see cref="OwnershipLine"/>. <c>Parley.Server</c> may not reach
    /// <c>Parley.Setup</c>, and <c>Parley.Setup</c> may not reach a store.
    /// </para>
    /// <para>
    /// On the sweep's interval and contained like it: a failing pass is reported and the next one runs, because the
    /// failures worth surviving are the transient ones a busy file produces, and a facilitator that stopped on the first
    /// would leave every later agent unasked for as long as the broker ran.
    /// </para>
    /// </remarks>
    private static async Task FacilitateAsync(
        BrokerRuntime runtime,
        string project,
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
                    await FacilitateOnceAsync(runtime, project, output, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    await output.WriteLineAsync($"{project}: Parley could not finish a pass: {exception.Message}")
                        .ConfigureAwait(false);
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // The project is being stopped, which is how this loop is meant to end.
        }
    }

    /// <summary>One facilitation pass. See <see cref="FacilitateAsync"/>.</summary>
    /// <param name="runtime">The project's broker.</param>
    /// <param name="project">The project's name.</param>
    /// <param name="output">Where to report.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>A task that completes when the pass is done.</returns>
    /// <remarks>
    /// <para>
    /// <b>The switch is read as each answer arrives, not when the question was asked</b>: turning Auto Accept
    /// off stops answers still to come from becoming lines, and turning it on leaves waiting answers waiting.
    /// </para>
    /// <para>
    /// <b>The file first, then the record.</b> A file with no record is repaired by the next
    /// acceptance; a record with no file would claim a line the agent will never carry. A write that fails is not an
    /// error: the answer waits, with the write's own sentence as the reason, which is what the review shows.
    /// </para>
    /// </remarks>
    internal static async Task FacilitateOnceAsync(
        BrokerRuntime runtime,
        string project,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(output);

        var answers = await runtime.FacilitateAsync(cancellationToken).ConfigureAwait(false);

        foreach (var answer in answers)
        {
            var autoAccept = await runtime.AutoAcceptAsync(cancellationToken).ConfigureAwait(false);
            string? because;
            bool accepted;

            // **Each answer is settled whatever happened to another.** FacilitateAsync has already marked every one
            // answered, so an answer this pass neither accepts nor holds is never visited again; one
            // repository's unreadable launch configuration must not strand every answer after it.
#pragma warning disable CA1031 // Whatever went wrong deciding one answer, it is held for a person rather than lost.
            try
            {
                because = await WhyNotAutomaticallyAsync(runtime, project, answer, autoAccept, cancellationToken)
                    .ConfigureAwait(false);

                accepted = because is null
                    && await runtime.AcceptAutomaticallyAsync(answer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failed) when (failed is not OperationCanceledException)
            {
                because = $"deciding it failed: {failed.Message}";
                accepted = false;
            }
#pragma warning restore CA1031

            if (accepted)
            {
                await output.WriteLineAsync(
                    $"{project}: {answer.Agent.Value} answered Parley, and its answer is now its ownership line.")
                    .ConfigureAwait(false);

                continue;
            }

            if (because is null)
            {
                // **The file was written and the record refused it**, which means a person
                // decided this question while the pass was deciding it. Their line goes back into the file, so what the
                // agent carries at its next start is the person's and not the answer they had already overruled.
                await RestoreAcceptedLineAsync(runtime, project, answer.Agent, cancellationToken).ConfigureAwait(false);
            }

            because ??= "the line could not be recorded, so it waits for a person.";

            _ = await runtime.HoldAsync(answer, because, cancellationToken).ConfigureAwait(false);

            await output.WriteLineAsync(
                $"{project}: {answer.Agent.Value} answered Parley; its answer waits for a person: {because}")
                .ConfigureAwait(false);
        }

        foreach (var refreshed in await runtime.RefreshAsync(cancellationToken).ConfigureAwait(false))
        {
            await output.WriteLineAsync(
                $"{project}: asked {refreshed.Machine}'s node to restart {refreshed.Value} once it is idle, so its peers " +
                "read its new ownership line.").ConfigureAwait(false);
        }
    }

    /// <summary>Writes the agent's recorded line back into its launch configuration, or takes the line out if none is.</summary>
    /// <param name="runtime">The broker.</param>
    /// <param name="project">The project whose launch entry it is.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>When the file agrees with the record again, as far as it can be made to.</returns>
    /// <remarks>
    /// Best effort and said nothing about: the same refusals as any write apply (another machine, a moved repository), and
    /// a file left disagreeing shows as pending on every surface, which is the truth.
    /// </remarks>
    private static async Task RestoreAcceptedLineAsync(
        BrokerRuntime runtime, string project, AgentName agent, CancellationToken cancellationToken)
    {
        var record = (await runtime.UseConnectionAsync(
                (connection, _, ct) => new AgentRepository(connection).GetAllAsync(ct),
                cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(row => row.Name == agent);

        if (record is null)
        {
            return;
        }

        _ = await OwnershipLine.WriteAsync(record.Workspace, project, agent, record.OwnsAccepted, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Why an answer cannot become the agent's line with no person involved, writing it to the launch configuration
    /// when it can.
    /// </summary>
    /// <returns>Null when the file was written and the line should be recorded; otherwise the reason it waits.</returns>
    private static async Task<string?> WhyNotAutomaticallyAsync(
        BrokerRuntime runtime,
        string project,
        Parley.Broker.FacilitatorAnswer answer,
        bool autoAccept,
        CancellationToken cancellationToken)
    {
        if (!autoAccept)
        {
            return "Auto Accept is off for this project.";
        }

        if (answer.Blocked)
        {
            return "the agent said it was blocked rather than answering.";
        }

        if (answer.Line.Length == 0)
        {
            return "the agent's answer was empty.";
        }

        if (!OwnershipStatement.IsValid(answer.Line, out var wrong))
        {
            return $"it cannot be a line as written: {wrong}";
        }

        var workspace = await runtime.UseConnectionAsync(
            (connection, _, ct) => new AgentRepository(connection).WorkspaceOfAsync(answer.Agent, ct),
            cancellationToken).ConfigureAwait(false);

        // OwnershipLine applies the machine rule itself — no surface writes for an agent whose machine is not this one —
        // and names the machine in its refusal, so an agent elsewhere waits with the sentence that says where to go.
        return await OwnershipLine.WriteAsync(workspace, project, answer.Agent, answer.Line, cancellationToken)
            .ConfigureAwait(false);
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
