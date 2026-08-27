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

    /// <summary>
    /// Opens the store and serves it until the process is stopped.
    /// </summary>
    /// <param name="databasePath">The store, or <see langword="null"/> if nothing named one.</param>
    /// <param name="listenOn">Where to listen, or <see langword="null"/> for the default.</param>
    /// <param name="logPath">A file to copy output into, or <see langword="null"/> for none.</param>
    /// <param name="output">Where to report what it is doing.</param>
    /// <param name="error">Where to report why it could not.</param>
    /// <param name="cancellationToken">Stops the broker.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static async Task<int> RunAsync(
        string? databasePath,
        string? listenOn,
        string? logPath,
        TextWriter output,
        TextWriter error,
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

            await output.WriteLineAsync(
                "Agents connect with PARLEY_AGENT set; machines join with `parley up`. Ctrl+C stops it.")
                .ConfigureAwait(false);

            await SweepAsync(runtime, output, cancellationToken).ConfigureAwait(false);
        }

        return 0;
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
