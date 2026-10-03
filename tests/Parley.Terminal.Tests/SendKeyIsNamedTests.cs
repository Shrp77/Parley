using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the status line names the key that sends, and draws all of what it names.
/// </summary>
/// <remarks>
/// The combined review gate's finding (lens 3, run 3): the send action was keyed on a carriage return, so its stroke was
/// "Alt+" and that character. The character drew as nothing and the row ended there — "Send what you wrote" and the
/// Shift+Enter line after it never reached the screen — and the half that did said Alt, which sends nothing.
/// </remarks>
public sealed class SendKeyIsNamedTests
{
    [Fact]
    public async Task The_message_box_says_Enter_sends_and_says_it_in_full()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var seated = await Seated.OpenAsync(ct);
        seated.Focus<LineRegion>();
        seated.Lay();

        var status = seated.Shell.Status;

        Assert.DoesNotContain(status, c => char.IsControl(c));
        Assert.Contains("Enter: " + Shell.SendTitle, status, StringComparison.Ordinal);
        Assert.DoesNotContain("Alt+Enter", status, StringComparison.Ordinal);
    }
}
