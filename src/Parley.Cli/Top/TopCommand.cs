using Parley.Client;
using Parley.Client.Seating;
using Terminal.Gui.App;
using Terminal.Gui.Time;

namespace Parley.Cli.Top;

/// <summary>
/// `parley top`: a deployment read from a terminal, without a browser and without an editor.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §17 question 15 records what this is for. A node hands turns to the agents it started;
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
/// throw; xUnit runs test classes in parallel. This is the same split the Spectre version made
/// around <c>AnsiConsole.Console</c> and for the same reason, and it is what lets a test drive the
/// whole surface with its own application.
/// </para>
/// </remarks>
public static class TopCommand
{
    /// <summary>Runs the terminal surface until the person leaves it.</summary>
    /// <param name="manifestPath">Where this machine's project manifest is.</param>
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

        // **Ctrl+C is deliberately not intercepted, and an early draft of this got it wrong.**
        //
        // That draft handled CancelKeyPress, set Cancel = true and signalled a token, on the
        // reasoning that a seat should be released politely rather than abandoned. It did not
        // work: every screen was blocked inside a synchronous prompt waiting for a key, and a
        // prompt does not observe a cancellation token.
        //
        // Letting it terminate is not the lesser option, it is the correct one. §4.0 makes an open
        // connection the evidence an agent is reachable, and the process dying closes every attach
        // stream this book holds — which is precisely how the broker learns somebody left. There is
        // no politer signal available; there is only the same signal sent sooner. The `using`
        // above still runs on every ordinary exit, and Ctrl+Q leaves the surface properly.
        using var app = Application.Create(TimeProvider.System as ITimeProvider);

        app.Init();

        // Read once here rather than on every draw: the entry changes when somebody runs
        // `autostart install`, which is not something done from inside this surface, and reading a
        // scheduled task on every keystroke is a wait a person notices.
        return await DriveAsync(
            app, directory, seats, manifestPath, InstalledEntry.Installed(), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The surface itself, over whatever application it is given.</summary>
    /// <param name="app">The application to build the surface in.</param>
    /// <param name="directory">The projects, and the clients that reach them.</param>
    /// <param name="seats">Where a person's seat is held, for as long as they hold one.</param>
    /// <param name="manifestPath">The manifest, for the screens that write to it.</param>
    /// <param name="installed">
    /// What this machine's supervisor runs, or null for none or unknown. Passed rather than read
    /// here, for the reason the application is: a test drives this on a machine that has an entry,
    /// and a surface that offered a different action according to what the developer's own
    /// scheduler holds would be a test that behaves differently on somebody else's machine.
    /// </param>
    /// <param name="cancellationToken">Stops the surface.</param>
    /// <returns>The process exit code.</returns>
    internal static async Task<int> DriveAsync(
        IApplication app,
        BrokerDirectory directory,
        SeatBook seats,
        string? manifestPath = null,
        InstalledEntry? installed = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        using var shell = Build(directory, seats, manifestPath, installed);

        await shell.RefreshAsync(cancellationToken).ConfigureAwait(false);

        app.Run(shell);

        return 0;
    }

    /// <summary>Builds the surface without running it.</summary>
    /// <param name="directory">The projects, and the clients that reach them.</param>
    /// <param name="seats">Where a person's seat is held.</param>
    /// <param name="manifestPath">The manifest, for the screens that write to it.</param>
    /// <param name="installed">What this machine's supervisor runs, or null.</param>
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
        InstalledEntry? installed = null) =>
        new(directory)
        {
            Seats = seats,
            ManifestPath = manifestPath,
            Installed = installed,
        };

    /// <summary>Whether there is a terminal to draw on at all.</summary>
    /// <remarks>
    /// The same test <see cref="ConsolePicker.Available"/> makes, and deliberately the same code
    /// rather than a second copy of the condition: output as well as input, because a full-screen
    /// surface redraws by rewriting lines and a redirected stdout turns that into thousands of
    /// frames in somebody's log file.
    /// </remarks>
    internal static bool Interactive => ConsolePicker.Available;

    /// <summary>What to say to somebody who is not at a terminal.</summary>
    /// <remarks>
    /// Names what to run instead rather than only declining. A refusal that says "this needs a
    /// terminal" to a script leaves whoever wrote the script with a decision and no options; the
    /// commands below are what this surface is a nicer front for, and they all work in a pipe.
    /// </remarks>
    internal const string Redirected =
        "parley top draws a full-screen terminal, and this one is redirected — nothing here "
        + "would be readable and the frames would fill the log. For the same facts in a pipe, "
        + "use `parley project list`, `parley registry` and `parley export <conversation>`. "
        + "For a browser instead, `parley console`.";
}
