using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That one message can be put on the clipboard, and only the one the cursor is on.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner asked on 2026-10-02</b> whether a single response could be copied or only the whole conversation; it was
/// only the whole. Alt+M, their choice of key, and in the View menu beside the whole-conversation copy.
/// </para>
/// <para>
/// Every test presses the key, through a <c>Seated</c> fixture whose clipboard is the driven session's list rather than the
/// machine's — a suite run in a loop would otherwise replace whatever the person at the keyboard had copied.
/// </para>
/// </remarks>
public sealed class CopyingOneMessageTests
{
    [Fact]
    public async Task Alt_m_copies_the_message_under_the_cursor_and_nothing_else()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var seated = await Seated.OpenAsync(ct);

        await seated.AskAsync(Seated.Billing, Seated.Conversation, ct);

        seated.Focus<TranscriptRegion>();
        seated.Session.Row(seated.Shell.Transcript.List, "is the build green?");
        seated.Session.Press(new Key(Shell.CopyMessageKey).WithAlt);

        var copied = Assert.Single(seated.Session.Copied);

        Assert.Contains("is the build green?", copied, StringComparison.Ordinal);
        Assert.Contains(Seated.Orders.Value, copied, StringComparison.Ordinal);

        // The other message in the conversation is not in it.
        Assert.DoesNotContain("which of you should I talk to", copied, StringComparison.Ordinal);

        // And the screen's indent is not either: it is this surface's structure, not anybody's words.
        Assert.All(
            copied.ReplaceLineEndings("\n").Split('\n'),
            line => Assert.False(line.StartsWith(new string(' ', 4), StringComparison.Ordinal), $"indented: '{line}'"));

        Assert.Equal(Shell.CopiedMessage(copied.Length), seated.Shell.Said);
    }

    [Fact]
    public async Task On_a_row_that_is_no_message_it_says_so_and_copies_nothing()
    {
        // The blank line between two messages, or the conversation's preamble. Copying the nearest message instead would put
        // words on the clipboard the person did not point at.
        var ct = TestContext.Current.CancellationToken;

        await using var seated = await Seated.OpenAsync(ct);

        seated.Focus<TranscriptRegion>();

        var list = seated.Shell.Transcript.List;

        list.SelectedItem = 0;

        Assert.Null(seated.Shell.Transcript.Under);

        seated.Session.Press(new Key(Shell.CopyMessageKey).WithAlt);

        Assert.Empty(seated.Session.Copied);
        Assert.Equal(Shell.NoMessageHere, seated.Shell.Said);
    }

    [Fact]
    public async Task It_is_in_the_View_menu_beside_copying_the_conversation()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var seated = await Seated.OpenAsync(ct);

        seated.Focus<TranscriptRegion>();

        var offered = Assert.Single(seated.Shell.Actions(), action => action.Label == Shell.CopyMessageTitle);

        Assert.Equal(ActionCategory.View, offered.Category);
        Assert.Equal(Shell.Alt(Shell.CopyMessageKey), offered.Stroke);
    }
}
