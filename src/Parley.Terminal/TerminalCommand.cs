using Parley.Client;
using Parley.Client.Seating;
using Parley.Setup;
using Terminal.Gui.App;
using Terminal.Gui.Time;

namespace Parley.Terminal;

/// <summary>
/// `parley terminal`: a deployment read from a terminal, without a browser and without an editor.
/// </summary>
/// <remarks>
/// <para>
/// A node hands turns to the agents it started;
/// an agent attached through somebody's own editor window was started by nobody, so nothing can
/// hand it one. The consequence is not that such an agent is inconvenient — it is that a person
/// has nowhere to take part from. Everything they can do otherwise either goes through a session
/// nothing can wake, or through single-shot commands that report and exit.
/// </para>
/// <para>
/// A client and only a client. It opens no database: §4 gives the store to one process, and every
/// fact about a broker on any screen here arrived over the transport from a broker that owns one.
/// It reads two files — the broker's log, to follow, and the selected project's catalogue, which
/// the broker never sees.
/// </para>
/// <para>
/// <b>The application is created here and passed down, never reached for.</b> Terminal.Gui's
/// static <c>Application</c> is process-global and, once used, makes <c>Application.Create</c>
/// throw; xUnit runs test classes in parallel. Passing it down is what lets a test drive the
/// whole surface with its own application.
/// </para>
/// </remarks>
public static class TerminalCommand
{
    /// <summary>Runs the terminal surface until the person leaves it.</summary>
    /// <param name="manifestPath">This machine's deployment root, where its projects and manifest live.</param>
    /// <param name="fallbackEndpoint">A broker to offer when the manifest names none.</param>
    /// <param name="cancellationToken">Stops the surface.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(
        string? manifestPath,
        string? fallbackEndpoint,
        CancellationToken cancellationToken = default)
    {
        if (!Interactive)
        {
            await Console.Error.WriteLineAsync(Redirected).ConfigureAwait(false);

            return 1;
        }

        using var directory = BrokerDirectory.FromManifest(
            manifestPath,
            string.IsNullOrWhiteSpace(fallbackEndpoint) ? null : new FallbackBroker(fallbackEndpoint));

        // The seats outlive every screen and are released when this is disposed, which is what
        // makes the idle rule hold on the paths nobody plans for. Disposing a SeatBook ends the
        // attach streams, and an attach stream ending is what tells the broker somebody left
        // (§4.0).
        using var seats = new SeatBook(directory.AgentsFor);

        // **Ctrl+C is deliberately not intercepted.** Every screen is blocked inside a synchronous prompt
        // waiting for a key, and a prompt does not observe a cancellation token. Letting it terminate is the
        // correct option: §4.0 makes an open connection the evidence an agent is reachable, and the process
        // dying closes every attach stream this book holds, which is how the broker learns somebody left.
        // The `using` above runs on every ordinary exit, and <see cref="Shell.QuitKey"/> (which `Shell` binds,
        // because the toolkit does not) leaves the surface properly.
        // The window's title names the build, which is the one place a tab strip shows. Set here and never in the shell:
        // a driven session writes nothing to standard output, and a title is written there on every platform but Windows.
        try
        {
            Console.Title = $"parley {BuildIdentity.Version}";
        }
        catch (Exception unsettable) when (unsettable is IOException or PlatformNotSupportedException)
        {
            // A console that has no title to set -- redirected, or a host that refuses -- loses a convenience, not the version:
            // the status line says it too.
        }

        // **Armed before the toolkit starts and disarmed after it stops**, which is why it is
        // declared above `app`: locals are disposed in reverse, so the driver's own shutdown runs
        // first and this is still watching until it has. What it covers is the paths that never
        // reach that shutdown — Ctrl+C, which the comment above deliberately lets kill this
        // process, and a crash, which ends it the same way. `ConsoleRestore` carries the argument.
        using var restore = ConsoleRestore.Arm();

        using var app = Application.Create(TimeProvider.System as ITimeProvider);

        app.Init();

        // Read once here rather than on every draw: the entry changes when somebody runs
        // `autostart install`, which is not something done from inside this surface, and reading a
        // scheduled task on every keystroke is a wait a person notices.
        // Resolved here, once, where the environment is the machine's own rather than a test host's.
        var catalogue = ProjectResolution.NodeConfig(
            null, Environment.GetEnvironmentVariable, manifestPath).Value;

        return await DriveAsync(
            app, directory, seats, manifestPath, InstalledEntry.Installed(), catalogue, AddressBook.In(manifestPath),
            cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The surface itself, over whatever application it is given.</summary>
    /// <param name="app">The application to build the surface in.</param>
    /// <param name="directory">The projects, and the clients that reach them.</param>
    /// <param name="seats">Where a person's seat is held, for as long as they hold one.</param>
    /// <param name="manifestPath">The deployment root, for the screens that write to it.</param>
    /// <param name="installed">
    /// What this machine's supervisor runs, or null for none or unknown. Passed rather than read
    /// here, for the reason the application is: a test drives this on a machine that has an entry,
    /// and a surface that offered a different action according to what the developer's own
    /// scheduler holds would be a test that behaves differently on somebody else's machine.
    /// </param>
    /// <param name="cataloguePath">
    /// This machine's node catalogue, or null to work it out from the manifest. Passed rather than read
    /// here, for the reason <paramref name="installed"/> is, and so a test never inherits the developer's own
    /// <c>PARLEY_NODE_CONFIG</c> and enrols its temporary repositories into a live deployment's catalogue.
    /// </param>
    /// <param name="addressingPath">
    /// Where who-each-line-goes-to is kept across restarts, or null to keep it for this run only. Null unless the
    /// program itself names it, for <paramref name="cataloguePath"/>'s reason: a driven test must not write into the
    /// Parley folder of the machine it runs on.
    /// </param>
    /// <param name="cancellationToken">Stops the surface.</param>
    /// <returns>The process exit code.</returns>
    internal static async Task<int> DriveAsync(
        IApplication app,
        BrokerDirectory directory,
        SeatBook seats,
        string? manifestPath = null,
        InstalledEntry? installed = null,
        string? cataloguePath = null,
        string? addressingPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        using var shell = Build(directory, seats, manifestPath, installed, cataloguePath, addressingPath);

        // Dialogs opened, and the box read or written, from the work queue go to the thread that draws
        // (`Prompting.Marshal` says why). Only here: a driven test never pumps the loop, so it keeps the inline default.
        Prompting.Marshal = app.Invoke;

        // **Started, not awaited, so the screen is on before the brokers have answered.** Waiting would draw
        // nothing until every project's broker had replied or given up, and giving up costs the
        // `ConnectTimeout` in `BrokerDirectory.Connect` per project; a down broker would look like a hung tool.
        //
        // The read happens the moment the surface exists; drawing does not wait on it. This is safe because
        // every later read already runs concurrently with the loop: `GuardedList` swaps rows and the fields
        // their colour derives from under the lock the draw takes. Every driven test calls `Show` and then
        // `RefreshAsync`, so a shell drawn before its first read is the ordering the suite exercises.
        var first = shell.RefreshAsync(cancellationToken);

        app.Run(shell);

        // Observed after the run so a fault is not left unwatched, and swallowed because by now
        // the surface has gone: `RefreshAsync` reports what it could not ask on the message line,
        // which is the screen this has just closed. Throwing here would turn "the broker was down
        // while you were reading" into a stack trace printed over somebody's shell prompt.
        try
        {
            await first.ConfigureAwait(false);
        }
        catch (Exception reported) when (reported is not OperationCanceledException)
        {
            // Said on the message line while the surface was up, and there is no surface now.
        }

        return 0;
    }

    /// <summary>Builds the surface without running it.</summary>
    /// <param name="directory">The projects, and the clients that reach them.</param>
    /// <param name="seats">Where a person's seat is held.</param>
    /// <param name="manifestPath">The deployment root, for the screens that write to it.</param>
    /// <param name="installed">What this machine's supervisor runs, or null.</param>
    /// <param name="cataloguePath">This machine's node catalogue, or null to take it from the manifest.</param>
    /// <param name="addressingPath">Where who-each-line-goes-to is kept, or null for this run only.</param>
    /// <returns>The surface.</returns>
    /// <remarks>
    /// Separated from <see cref="DriveAsync"/> because a test drives the surface without a run
    /// loop: <c>Run</c> pumps until something stops it, which is a test that hangs rather than one
    /// that fails.
    /// </remarks>
    internal static Shell Build(
        BrokerDirectory directory,
        SeatBook? seats = null,
        string? manifestPath = null,
        InstalledEntry? installed = null,
        string? cataloguePath = null,
        string? addressingPath = null) =>
        new(directory)
        {
            Seats = seats,
            ManifestPath = manifestPath,
            Installed = installed,
            CataloguePath = cataloguePath,
            Addressing = new AddressBook(addressingPath),
        };

    /// <summary>Whether there is a terminal to draw on at all.</summary>
    /// <remarks>
    /// The same test <see cref="ConsolePicker.Available"/> makes, and deliberately the same code
    /// rather than a second copy of the condition: output as well as input, because a full-screen
    /// surface redraws by rewriting lines and a redirected stdout turns that into thousands of
    /// frames in somebody's log file.
    /// </remarks>
    private static bool Interactive => ConsolePicker.Available;

    /// <summary>What to say to somebody who is not at a terminal.</summary>
    /// <remarks>
    /// Names what to run instead rather than only declining. A refusal that says "this needs a
    /// terminal" to a script leaves whoever wrote the script with a decision and no options; the
    /// commands below are what this surface is a nicer front for, and they all work in a pipe.
    /// </remarks>
    private const string Redirected =
        "parley draws a full screen, and this terminal is redirected. For the same facts in a pipe, "
        + "use `parley project list`, `parley registry` and `parley open`. "
        + "For anything else, use the web UI: `parley web-ui`.";
}
