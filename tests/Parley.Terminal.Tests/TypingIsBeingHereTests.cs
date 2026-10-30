using Parley.Client.Seating;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That typing into the box, and every other key a view takes for itself, counts as being at the terminal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found probing the owner's report of 2026-10-03</b>, that focus left the box while they typed. That report is not
/// reproduced here; this is what the probe turned up beside it. The idle rule was fed only from the shell's own key
/// handler, and a focused view takes its keys before the shell is offered them — so every letter typed into the box, and
/// every arrow in a list, was invisible to it. Twenty minutes of writing an answer marked the person away while they
/// wrote it, and the next agent to ask was told they were not there.
/// </para>
/// <para>
/// The application's own keyboard event is what feeds it now: it is raised for every key before any view sees one.
/// </para>
/// </remarks>
[Integration]
public sealed class TypingIsBeingHereTests
{
    [Fact]
    public async Task Typing_into_the_box_after_the_idle_window_takes_the_connection_back()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var seated = await Seated.OpenAsync(ct);
        var shell = seated.Shell;

        seated.Focus<LineRegion>();

        shell.Tick(DateTimeOffset.UtcNow + SeatBook.IdleAfter + TimeSpan.FromMinutes(1));

        Assert.Null(shell.Held);

        // Only characters, which the box takes for itself. Nothing reaches the shell's own key handler.
        seated.Session.Type("PR9 yes");
        await shell.Settled;

        Assert.NotNull(shell.Held);
        Assert.Equal("PR9 yes", shell.Line.Written);
        Assert.True(shell.Line.Editing, "typing moved the cursor out of the box");
    }

    [Fact]
    public async Task Typing_keeps_the_person_here_through_the_idle_window()
    {
        // The other half: somebody typing steadily is never swept at all.
        var ct = TestContext.Current.CancellationToken;

        await using var seated = await Seated.OpenAsync(ct);
        var shell = seated.Shell;
        var held = shell.Held;

        seated.Focus<LineRegion>();

        // The last touch the shell knows of is long ago, so only the typing can keep the hold. Without this the shell's own
        // start-up time stands in for a touch and the test passes whatever typing does.
        shell.Touched(DateTimeOffset.UtcNow - SeatBook.IdleAfter - TimeSpan.FromMinutes(10));

        seated.Session.Type("a long answer");

        // The touch is dated now, so a sweep a little under the window from now must keep the hold.
        shell.Tick(DateTimeOffset.UtcNow + SeatBook.IdleAfter - TimeSpan.FromMinutes(1));

        Assert.Same(held, shell.Held);
    }
}
