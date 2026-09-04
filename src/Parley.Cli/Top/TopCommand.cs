using Parley.Client;
using Parley.Client.Seating;
using Spectre.Console;

namespace Parley.Cli.Top;

/// <summary>
/// `parley top`: a deployment read from a terminal, without a browser and without an editor.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §17 question 15 records what this is for. A node hands turns to the agents it started;
/// an agent attached through somebody's own editor window was started by nobody, so nothing can
/// hand it one. The consequence is not that such an agent is inconvenient — it is that a person
/// has nowhere to take part from. Everything they can do today either goes through a session
/// nothing can wake, or through single-shot commands that report and exit.
/// </para>
/// <para>
/// A client and only a client. It opens no database: §4 gives the store to one process, and every
/// fact on any screen here arrived over the transport from a broker that owns one. The one
/// exception is the broker's log, which is a file and is read to follow rather than to own.
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
        // makes FR-011 hold on the paths nobody plans for. Disposing a SeatBook ends the attach
        // streams, and an attach stream ending is what tells the broker somebody left (§4.0).
        using var seats = new SeatBook(directory.AgentsFor);

        // **Ctrl+C is deliberately not intercepted, and the first draft of this got it wrong.**
        //
        // That draft handled CancelKeyPress, set Cancel = true and signalled a token, on the
        // reasoning that a seat should be released politely rather than abandoned. It did not
        // work, and could not: every screen here is blocked inside a synchronous prompt waiting
        // for a key, and a prompt does not observe a cancellation token. So Ctrl+C suppressed the
        // exit, nothing else happened, and the surface sat there -- with a banner promising that
        // Ctrl+C leaves. Pressing it again did nothing either.
        //
        // Letting it terminate is not the lesser option, it is the correct one. SPEC.md §4.0
        // makes an open connection the evidence an agent is reachable, and the process dying
        // closes every attach stream this book holds -- which is precisely how the broker learns
        // somebody left. There is no politer signal available; there is only the same signal sent
        // sooner. The `using` above still runs on every ordinary exit.
        // Read once here rather than on every visit to the list: the entry changes when somebody
        // runs `autostart install`, which is not something done from inside this surface, and
        // reading a scheduled task on every keystroke is a wait a person notices.
        return await DriveAsync(
            AnsiConsole.Console, directory, seats, cancellationToken, InstalledEntry.Installed())
            .ConfigureAwait(false);
    }

    /// <summary>The navigation itself, over whatever console it is given.</summary>
    /// <param name="console">Where to draw, and where the keys come from.</param>
    /// <param name="directory">The projects, and the clients that reach them.</param>
    /// <param name="seats">Where a person's seat is held, for as long as they hold one.</param>
    /// <param name="cancellationToken">Stops the surface.</param>
    /// <param name="installed">
    /// What this machine's supervisor runs, or null for none or unknown. Passed rather than read
    /// here, for the reason the console is: a test drives this on a machine that has an entry,
    /// and a first screen that grew an option according to what the developer's own scheduler
    /// holds would be a test that navigates somewhere else on somebody else's machine.
    /// </param>
    /// <returns>The process exit code.</returns>
    /// <remarks>
    /// <para>
    /// Split from <see cref="RunAsync"/> so the journey can be driven by a test, and the console
    /// is <b>passed</b> rather than set on <see cref="AnsiConsole.Console"/>. That static is
    /// process-global, xUnit runs test classes in parallel, and a test that redirected it would
    /// be redirecting it for every other test running at that moment — which is the shape of the
    /// bug <c>BannedApiAnalyzers</c> exists in this repository to prevent, arrived at from a
    /// different direction.
    /// </para>
    /// <para>
    /// The parameter is worth having on its own merits. Every view below now says where it draws
    /// instead of reaching for an ambient one, which is the difference between a surface that can
    /// be checked and a surface somebody has to look at.
    /// </para>
    /// </remarks>
    internal static async Task<int> DriveAsync(
        IAnsiConsole console,
        BrokerDirectory directory,
        SeatBook seats,
        CancellationToken cancellationToken = default,
        InstalledEntry? installed = null)
    {
        console.Write(Banner());

        // The seat lives here rather than inside a menu visit: a person is one person for the
        // session, and walking back out to the project list is not leaving their seat.
        Seat? me = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            // Re-read every time round rather than once at startup. BrokerDirectory re-stamps the
            // manifest on each access, so a project named in another window appears here without
            // restarting -- and holding a snapshot in a field is exactly the defect the console
            // shipped, where a project renamed on one page went on showing its old name on
            // another until the process was restarted.
            var chosen = await ProjectList
                .ChooseAsync(console, directory, installed, cancellationToken)
                .ConfigureAwait(false);

            if (chosen is null)
            {
                return 0;
            }

            me = await ProjectMenu
                .ShowAsync(console, directory, seats, chosen, me, cancellationToken, installed)
                .ConfigureAwait(false);
        }

        return 0;
    }

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

    /// <summary>The one line of chrome the surface opens with.</summary>
    private static Markup Banner() =>
        new("[bold]parley top[/] — a deployment, from here. [dim]Ctrl+C to leave.[/]\n");
}
