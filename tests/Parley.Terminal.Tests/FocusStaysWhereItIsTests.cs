using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That nothing running in the background moves the cursor out of the pane somebody put it in.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's report of 2026-10-03</b>: typing "PR9 yes" into the box, at the <c>9</c> the cursor went and the
/// transcript had focus. The 9 was a coincidence of timing. The shell keeps its own record of which pane is focused,
/// and changed it only through its own <c>Focus</c> — so a pane reached by the mouse was focused on screen and not in
/// the record. Opening a conversation records the transcript; the mail poll re-arranges the screen every five seconds,
/// and re-arranging re-applies the record. Somebody who clicked into the box was moved out of it at the next poll,
/// mid-word.
/// </para>
/// </remarks>
public sealed class FocusStaysWhereItIsTests
{
    [Fact]
    public async Task Clicking_into_the_box_survives_the_mail_poll()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var seated = await Seated.OpenAsync(ct, mailEvery: TimeSpan.FromMilliseconds(20));
        var shell = seated.Shell;

        seated.Lay();

        // Arriving at a conversation focuses the transcript; the mouse is how the owner got to the box.
        Assert.IsType<TranscriptRegion>(shell.Panes[shell.FocusedRegion]);

        seated.Session.Click(shell.Line.Box);
        seated.Session.Type("PR");

        Assert.True(shell.Line.Editing, "the cursor left the box between the click and the typing: a poll re-applied a stale focus record");

        // Several polls, each of which re-arranges the screen.
        await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
        await shell.Settled;
        seated.Lay();

        seated.Session.Type("9 yes");

        Assert.True(shell.Line.Editing, "a poll moved the cursor out of the box");
        Assert.Equal("PR9 yes", shell.Line.Written);
        Assert.IsType<LineRegion>(shell.Panes[shell.FocusedRegion]);
    }
}
