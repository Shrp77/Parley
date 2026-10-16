using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That nothing the broker starts puts a window on somebody's desktop.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written because `--hidden` moved the window instead of removing it.</b> The broker lets go of
/// its console, and a child console program inherits its parent's console when there is one — which
/// is why the web UI had never needed <c>CreateNoWindow</c> and why nothing noticed. Once there is
/// no console to inherit, Windows gives the child a fresh one, so the window somebody had just asked
/// not to see would come back reading "Parley web UI on http://127.0.0.1:5280".
/// </para>
/// <para>
/// <b>Read off the source, because the thing being asserted is a window.</b> A test cannot watch for
/// one appearing on the desktop of the machine running the suite — and a test that started a hidden
/// broker to look would be a test that needs a desktop. What can be checked is that every starter
/// carries the setting, which is the whole of the rule.
/// </para>
/// <para>
/// <b>The list is explicit and short, so a fourth starter is a decision somebody makes here.</b> Not
/// every <c>ProcessStartInfo</c> in this repository should be windowless: `parley terminal` forwards
/// to a full-screen application that needs a console, and opening a file or a browser for somebody
/// is meant to be visible. What this covers is the three things that run *under a broker* — which
/// may have no console at all.
/// </para>
/// </remarks>
public sealed class NoChildWindowTests
{
    /// <summary>Each source that starts a process the broker is the parent of.</summary>
    public static TheoryData<string, string> Starters => new()
    {
        { "src/Parley.Cli/Program.cs", "the web UI the broker starts beside itself" },
        { "src/Parley.Cli/AutostartCommand.cs", "the supervisor the install command drives" },
        { "src/Parley.Headless/ResidentAgent.cs", "an agent the node starts for a peer's request" },
    };

    [Theory]
    [MemberData(nameof(Starters))]
    public void Everything_the_broker_starts_asks_for_no_window(string source, string what)
    {
        var text = RepositoryFile.Read(source);

        // One `ProcessStartInfo` per file today, so the count is the assertion: if a second starter
        // is added here, this fails and whoever added it decides whether it belongs on the list
        // above rather than inheriting an answer nobody meant to give.
        var starters = Occurrences(text, "new ProcessStartInfo");
        var windowless = Occurrences(text, "CreateNoWindow = true");

        Assert.True(
            windowless >= starters,
            $"{source} starts {starters} process(es) and only {windowless} ask for no window — "
            + $"{what} would open one on a machine where the broker has let go of its console");
    }

    [Fact]
    public void The_check_is_not_vacuous()
    {
        // The guard above passes trivially against a file that starts nothing, so this pins that
        // each named source really does start something. A starter renamed or moved out would
        // otherwise leave the rule quietly covering nothing — the shape this repository records as
        // "a regex that silently matches nothing makes every other check pass by vacuity".
        foreach (var source in Starters.Select(row => (string)row.Data.Item1))
        {
            Assert.True(
                Occurrences(RepositoryFile.Read(source), "new ProcessStartInfo") > 0,
                $"{source} no longer starts a process, so it is covering nothing");
        }
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        var at = text.IndexOf(needle, StringComparison.Ordinal);

        while (at >= 0)
        {
            count++;
            at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
