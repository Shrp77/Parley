using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// The transcript folded to one line per message, so the shape of a conversation can be read.
/// </summary>
/// <remarks>
/// The owner asked for it on the first morning in the room: a broadcast to nineteen agents is nineteen
/// pairwise messages (SPEC.md §5.0) carrying one sentence, so who spoke to whom and in what order is buried
/// under its own content on exactly the conversations where that shape matters most.
/// </remarks>
public sealed class FoldingTests
{
    [Fact]
    public async Task Folding_leaves_one_line_per_message()
    {
        await using var seated = await Seated.OpenAsync(TestContext.Current.CancellationToken);
        var transcript = seated.Shell.Transcript;

        seated.Lay();

        Assert.Contains(transcript.Drawn, row => row.Contains("is the build green?", StringComparison.Ordinal));

        transcript.Fold();

        Assert.True(transcript.Folded);
        Assert.DoesNotContain(transcript.Drawn, row => row.Contains("is the build green?", StringComparison.Ordinal));

        // The header stays, which is what makes a fold a fold rather than an empty screen.
        Assert.Contains(transcript.Drawn, row => row.Contains(Seated.Orders.Value, StringComparison.Ordinal));

        // **And what is written is the whole conversation still**, which is what copying takes: a fold is a
        // way of looking at a transcript rather than a way of shortening it.
        Assert.Contains("is the build green?", transcript.Written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task And_the_frame_says_so_because_short_messages_look_the_same()
    {
        // A reader who does not know a fold is on reads missing words as missing messages.
        await using var seated = await Seated.OpenAsync(TestContext.Current.CancellationToken);
        var transcript = seated.Shell.Transcript;

        Assert.Equal(TranscriptRegion.RegionTitle, transcript.Title);

        transcript.Fold();

        Assert.Equal(TranscriptRegion.FoldedTitle, transcript.Title);

        transcript.Fold();

        Assert.Equal(TranscriptRegion.RegionTitle, transcript.Title);
    }

    [Fact]
    public async Task Enter_opens_the_message_under_the_cursor_and_closes_it_again()
    {
        await using var seated = await Seated.OpenAsync(TestContext.Current.CancellationToken);
        var shell = seated.Shell;

        seated.Lay();
        shell.Transcript.Fold();
        seated.Focus<TranscriptRegion>();

        Assert.NotNull(shell.Transcript.Under);

        shell.NewKeyDownEvent(Key.Enter);

        Assert.Contains(
            shell.Transcript.Drawn,
            row => row.Contains("is the build green?", StringComparison.Ordinal));

        shell.NewKeyDownEvent(Key.Enter);

        Assert.DoesNotContain(
            shell.Transcript.Drawn,
            row => row.Contains("is the build green?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unfolded_Enter_is_left_alone_rather_than_swallowed()
    {
        // An action offered where it would do nothing is the fault this surface keeps a rule about, and a key
        // taken and dropped is the same fault with nothing on screen to show for it.
        await using var seated = await Seated.OpenAsync(TestContext.Current.CancellationToken);
        var transcript = seated.Shell.Transcript;

        Assert.False(transcript.Folded);
        Assert.False(transcript.Expand(1), "an unfolded transcript took the key anyway");
    }

    [Fact]
    public async Task What_is_opened_survives_folding_and_unfolding()
    {
        // Somebody who opened a message to compare it with another has said something about that message
        // which closing the fold should not throw away.
        await using var seated = await Seated.OpenAsync(TestContext.Current.CancellationToken);
        var transcript = seated.Shell.Transcript;

        seated.Lay();
        transcript.Fold();

        var message = transcript.Under;

        Assert.NotNull(message);
        Assert.True(transcript.Expand(message!.Value));
        Assert.Contains(transcript.Drawn, row => row.Contains("is the build green?", StringComparison.Ordinal));

        transcript.Fold();
        transcript.Fold();

        Assert.Contains(transcript.Drawn, row => row.Contains("is the build green?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_fold_is_named_on_the_status_line_both_ways()
    {
        await using var seated = await Seated.OpenAsync(TestContext.Current.CancellationToken);
        var shell = seated.Shell;

        seated.Focus<TranscriptRegion>();

        var offered = Assert.Single(shell.Actions(), a => a.Label == Shell.FoldTitle);

        Assert.Equal(Shell.FoldKey.ToString(), offered.Stroke);

        // Pressed rather than called, because a key named on a status line and bound to nothing is the fault
        // this surface has met twice -- and a letter is only free in this pane because it is a list.
        shell.NewKeyDownEvent(new Key(Shell.FoldKey));

        await shell.Settled;

        Assert.True(shell.Transcript.Folded);
        Assert.Contains(shell.Actions(), a => a.Label == Shell.UnfoldTitle);
    }

    [Fact]
    public async Task Folding_with_the_cursor_near_the_end_does_not_throw()
    {
        // **The toolkit refuses a selection past the end outright rather than clamping it.** Folding is the
        // first thing on this surface that makes a transcript shorter on purpose, so this is the first route
        // to a selection that no longer exists.
        await using var seated = await Seated.OpenAsync(TestContext.Current.CancellationToken);
        var transcript = seated.Shell.Transcript;

        seated.Lay();

        var rows = transcript.List.Source?.Count ?? 0;

        Assert.True(rows > 1, $"the fixture drew {rows} rows, so there is nothing to fold away");

        transcript.List.SelectedItem = rows - 1;
        transcript.Fold();

        Assert.True(transcript.List.SelectedItem < (transcript.List.Source?.Count ?? 0));
    }
}
