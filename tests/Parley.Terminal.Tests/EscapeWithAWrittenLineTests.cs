using Parley.Client.Views;
using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That Escape does not throw away a line somebody was writing without asking first.
/// </summary>
/// <remarks>
/// The owner's report of 2026-10-08: a long reply was lost to one stray Escape. Each test presses the key, because the
/// property is what the key does and not what a method does.
/// </remarks>
public sealed class EscapeWithAWrittenLineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Escape_with_a_line_in_the_box_asks_first_and_keeps_the_line_if_they_keep_writing()
    {
        // Must fail against Escape leaving at once.
        await using var seated = await Seated.OpenAsync(Ct);
        var shell = seated.Shell;

        shell.Line.Written = "a long answer I have not sent";
        seated.Session.Press(Key.Esc);
        await shell.Settled;

        var dialog = (ChoiceDialog)seated.Session.Find(Shell.DiscardTitle);

        seated.Session.Row(dialog.Choices, Shell.KeepWriting);
        await seated.Session.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        Assert.Equal(Viewing.Conversation, shell.Place.Viewing);
        Assert.Equal("a long answer I have not sent", shell.Line.Written);
    }

    [Fact]
    public async Task Escape_with_a_line_in_the_box_leaves_when_they_say_to_discard_it()
    {
        await using var seated = await Seated.OpenAsync(Ct);
        var shell = seated.Shell;

        shell.Line.Written = "nothing I mind losing";
        seated.Session.Press(Key.Esc);
        await shell.Settled;

        var dialog = (ChoiceDialog)seated.Session.Find(Shell.DiscardTitle);

        seated.Session.Row(dialog.Choices, Shell.Discard);
        await seated.Session.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        Assert.Equal(Viewing.Project, shell.Place.Viewing);
    }

    [Fact]
    public async Task Escape_with_an_empty_box_leaves_at_once()
    {
        await using var seated = await Seated.OpenAsync(Ct);
        var shell = seated.Shell;

        seated.Session.Press(Key.Esc);
        await shell.Settled;

        Assert.Equal(Viewing.Project, shell.Place.Viewing);
    }
}
