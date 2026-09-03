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

        if (string.IsNullOrWhiteSpace(databasePath))
        {
            await error.WriteLineAsync(
                "No database. Pass --db <path>, or set " +
                $"{DeploymentEnvironment.DatabaseVariable}. The broker owns one file and every " +
                "agent in the project reaches it through this process.").ConfigureAwait(false);

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

        var started = new List<(ProjectSettings Project, BrokerRuntime Runtime, ParleyServer Server)>();
        var failed = 0;

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
            foreach (var project in projects)
            {
                BrokerRuntime runtime;

                try
                {
                    runtime = BrokerRuntime.Open(project.Store, BrokerOptions.Default, TimeProvider.System);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    await error.WriteLineAsync(
                        $"{project.Name}: could not open '{project.Store}': {exception.Message}")
                        .ConfigureAwait(false);

                    failed++;
                    continue;
                }

                try
                {
                    var server = await ParleyServer.StartAsync(runtime, project.Endpoint, cancellationToken)
                        .ConfigureAwait(false);

                    started.Add((project, runtime, server));
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

                    failed++;
                }
            }

            if (started.Count == 0)
            {
                await error.WriteLineAsync(
                    "No broker started. Nothing is serving.").ConfigureAwait(false);

                return Unavailable;
            }

            foreach (var (project, _, server) in started)
            {
                await output.WriteLineAsync(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{project.Name} on {string.Join(", ", server.Addresses)}, store at {project.Store}, schema {SchemaMigrator.LatestVersion}."))
                    .ConfigureAwait(false);
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

            // Said here too. Several addresses rather than one makes the confusion likelier, not
            // less: whichever a person clicks, none of them is a web page.
            await output.WriteLineAsync(
                "  Those addresses are for agents and speak gRPC. None of them is a web page.")
                .ConfigureAwait(false);

            await output.WriteLineAsync(
                "Agents connect with PARLEY_AGENT set; machines join with `parley up`. Ctrl+C stops them all.")
                .ConfigureAwait(false);

            // The console once, against the first started broker as its fallback -- it reads the
            // manifest for the rest -- and a node per started project, each contained on its own
            // so that one project's node failing leaves the others hosted.
            var hosted = HostAllAsync(
                nodeFor,
                alongside,
                [.. started.Select(s => (s.Project, s.Server.Addresses))],
                output,
                cancellationToken);

            // One sweep per broker, all cancelled by the one token the interrupt owns.
            await Task.WhenAll(
                [.. started.Select(s => SweepAsync(s.Runtime, output, cancellationToken)), hosted])
                .ConfigureAwait(false);

            if (await hosted.ConfigureAwait(false) != 0)
            {
                failed++;
            }
        }
        finally
        {
            // Everything started is stopped, including when one sweep threw. A broker left holding
            // a port after the process was told to stop is the failure Ctrl+C was just fixed for,
            // reappearing one level up.
            foreach (var (_, _, server) in started)
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }
        }

        return failed > 0 ? Unavailable : 0;
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
            () => whenListening(addresses, output, cancellationToken), "This machine's node", output)
            .ConfigureAwait(false);
    }

    /// <summary>Hosts the console once and a node per started project, all contained.</summary>
    /// <param name="nodeFor">What to run for each project, or null to host no node.</param>
    /// <param name="alongside">What to run that is not bound to one broker, or null.</param>
    /// <param name="started">Every project that came up, with where it listens.</param>
    /// <param name="output">Where to report a failure worth seeing.</param>
    /// <param name="cancellationToken">Stops them all.</param>
    /// <returns>The first non-zero exit code, or zero.</returns>
    private static async Task<int> HostAllAsync(
        Func<ProjectSettings, IReadOnlyList<string>, TextWriter, CancellationToken, Task<int>>? nodeFor,
        Func<IReadOnlyList<string>, TextWriter, CancellationToken, Task<int>>? alongside,
        IReadOnlyList<(ProjectSettings Project, IReadOnlyList<string> Addresses)> started,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var hosting = new List<Task<int>>();

        if (alongside is not null)
        {
            hosting.Add(alongside(started[0].Addresses, output, cancellationToken));
        }

        if (nodeFor is not null)
        {
            foreach (var (project, addresses) in started)
            {
                hosting.Add(ContainAsync(
                    () => nodeFor(project, addresses, output, cancellationToken),
                    $"{project.Name}: this machine's node",
                    output));
            }
        }

        var codes = await Task.WhenAll(hosting).ConfigureAwait(false);

        return Array.Find(codes, code => code != 0);
    }

    /// <summary>Runs a hosted node and survives it failing.</summary>
    /// <param name="run">The node.</param>
    /// <param name="who">How to name it in the failure line.</param>
    /// <param name="output">Where to report a failure worth seeing.</param>
    /// <returns>Its exit code, or the unavailable code when it threw.</returns>
    private static async Task<int> ContainAsync(Func<Task<int>> run, string who, TextWriter output)
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
    /// file — each taking the write lock, each with its own idea of when the ghosting clock
    /// started, and none of them wrong in a way anybody could see.
    /// </para>
    /// <para>
    /// A failing pass is reported and the loop continues. The sweep enforces deadlines, so
    /// stopping on the first failure would leave every exchange in the deployment counting down
    /// against nothing — and the failures worth surviving are the transient ones a busy file
    /// produces.
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
