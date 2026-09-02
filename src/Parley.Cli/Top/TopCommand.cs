using Parley.Client;
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

        return await DriveAsync(AnsiConsole.Console, directory, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The navigation itself, over whatever console it is given.</summary>
    /// <param name="console">Where to draw, and where the keys come from.</param>
    /// <param name="directory">The projects, and the clients that reach them.</param>
    /// <param name="cancellationToken">Stops the surface.</param>
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
        CancellationToken cancellationToken = default)
    {
        console.Write(Banner());

        while (!cancellationToken.IsCancellationRequested)
        {
            // Re-read every time round rather than once at startup. BrokerDirectory re-stamps the
            // manifest on each access, so a project named in another window appears here without
            // restarting -- and holding a snapshot in a field is exactly the defect the console
            // shipped, where a project renamed on one page went on showing its old name on
            // another until the process was restarted.
            var chosen = await ProjectList.ChooseAsync(console, directory, cancellationToken)
                .ConfigureAwait(false);

            if (chosen is null)
            {
                return 0;
            }

            await ProjectMenu.ShowAsync(console, directory, chosen, cancellationToken)
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
