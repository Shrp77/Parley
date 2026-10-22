using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// <c>--hidden</c>: the broker letting go of the console window a logon entry would otherwise put
/// on somebody's desktop.
/// </summary>
/// <remarks>
/// <para>
/// The entry registers with <c>InteractiveToken</c> so that the broker runs in the person's own
/// session, and Windows gives a console program a console. That session is not incidental: the
/// node's job is starting agents, and an agent is a CLI the person has already authenticated, so a
/// broker outside their session would start agents that cannot log in. **The window is the price of
/// being in the right session, and this is how it is not paid.**
/// </para>
/// <para>
/// Task Scheduler's own <c>Hidden</c> setting is not the answer and is easy to reach for: it
/// controls whether the task appears in the Task Scheduler list, not whether a window appears.
/// </para>
/// </remarks>
public sealed class HiddenEntryTests
{
    private const string Root = "/machine/parley";

    [Fact]
    public void An_entry_installed_without_it_says_nothing_about_a_window()
    {
        // The default is unchanged, which matters more than it sounds: every entry already installed
        // was written without this, and a flag that appeared in arguments nobody asked for would
        // make a reinstall differ from what the person last chose.
        Assert.DoesNotContain(
            "--hidden",
            AutostartPlan.DescribeManifest(Root).Arguments,
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_installed_with_it_carries_it()
    {
        Assert.Contains(
            "--hidden",
            AutostartPlan.DescribeManifest(Root, executable: null, consoleOn: null, hidden: true).Arguments,
            StringComparison.Ordinal);
    }

    [Fact]
    public void It_is_written_last_so_a_person_reads_it_at_the_end()
    {
        // `autostart status` prints these arguments whole. The flag says something about the entry
        // rather than about what it serves, so it belongs after the things that do — and a reader
        // scanning for "what does this run" should meet the project and the log first.
        var arguments = AutostartPlan
            .DescribeManifest(Root, executable: null, consoleOn: "http://127.0.0.1:5280", hidden: true)
            .Arguments;

        Assert.EndsWith("--hidden", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reader_tells_the_two_kinds_of_entry_apart()
    {
        // `autostart status` answers "why does a black window appear when I log in", and it can only
        // do that if it can see which kind of entry is installed. An entry reader that ignored the
        // flag would report every entry as the same and leave the question to be asked somewhere
        // this project cannot answer it.
        var plain = InstalledEntry.Parse($"serve --config {Root} --log {Root}/parley-broker.log");
        var quiet = InstalledEntry.Parse($"serve --config {Root} --log {Root}/parley-broker.log --hidden");

        Assert.False(plain!.Hidden);
        Assert.True(quiet!.Hidden);
    }

    [Fact]
    public void It_consumes_nothing_after_it()
    {
        // A flag, not an option. Reading it as though it took a value would swallow whatever came
        // next — and what comes next in a real entry is `--with-web-ui`, so the mistake would show
        // up as a web UI that silently stopped being started.
        var entry = InstalledEntry.Parse(
            $"serve --config {Root} --hidden --with-web-ui --web-ui http://127.0.0.1:5280");

        Assert.True(entry!.Hidden);
        Assert.Equal("http://127.0.0.1:5280", entry.Console);
    }

    [Fact]
    public void It_does_not_change_what_the_entry_serves()
    {
        // The flag is about the window and nothing else. An entry that quietly stopped serving every
        // project because somebody asked for no console window would be the worst kind of surprise:
        // silent, and only visible as agents that never answer.
        var quiet = InstalledEntry.Parse($"serve --config {Root} --hidden");

        Assert.True(quiet!.ServesManifest);
        Assert.Null(quiet.Project);
    }
}
