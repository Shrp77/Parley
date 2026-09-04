using Parley.Cli.Top;
using Parley.Core;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// A transcript on a terminal that is not wide enough for it.
/// </summary>
/// <remarks>
/// <para>
/// The spec's edge case: <i>the terminal is too narrow, or is resized while running — the
/// transcript reflows; nothing that carries meaning is truncated away silently.</i> The last word
/// is the requirement. Wrapping is fine and expected; a value that disappears behind an ellipsis is
/// not, because the reader has no way to tell it was ever there.
/// </para>
/// <para>
/// A panel's <i>body</i> wraps, which is the easy half and is where the message text and the
/// visibility sentence live. Its <b>header</b> is the half worth probing: Spectre truncates a
/// header that does not fit, and this header carries who sent the message, who it was sent to,
/// what kind it is, and which exchange it belongs to — every one of which is meaning.
/// </para>
/// </remarks>
public sealed class NarrowTerminalTests
{
    /// <summary>Narrow enough that the header cannot fit, and plausible: a split pane.</summary>
    private const int Narrow = 40;

    [Theory]
    [InlineData(Narrow)]
    [InlineData(20)]
    public void Who_a_message_was_to_survives_a_terminal_too_narrow_to_hold_the_header(int width)
    {
        var console = Rendered(width, Panel(width));

        // The recipient is not decoration. A transcript that says who spoke and not who they
        // spoke to has lost the half that makes it a conversation.
        Assert.Contains("billing", console.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Narrow)]
    [InlineData(20)]
    public void What_kind_of_message_it_was_survives_too(int width)
    {
        var console = Rendered(width, Panel(width));

        Assert.Contains("question", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_visibility_sentence_wraps_rather_than_vanishing()
    {
        // Carried in words and never by colour (FR-005), so it has to survive a narrow terminal
        // for the same reason it has to survive a colourless one: it is the only statement that
        // somebody in the conversation did not see this message.
        var console = Rendered(Narrow, Panel(Narrow));

        Assert.Contains("did not see", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_check_is_looking_at_something()
    {
        // Non-vacuity: at a comfortable width everything asserted above is plainly present, so a
        // failure at 40 or 20 is the width and not a fixture that never said these things.
        var console = Rendered(200, Panel(200));

        Assert.Contains("billing", console.Output, StringComparison.Ordinal);
        Assert.Contains("question", console.Output, StringComparison.Ordinal);
        Assert.Contains("did not see", console.Output, StringComparison.Ordinal);
    }

    /// <summary>Renders one panel at a given width and hands back what was drawn.</summary>
    /// <param name="width">The terminal width.</param>
    /// <param name="panel">What to draw.</param>
    /// <returns>The console it was drawn on.</returns>
    private static TestConsole Rendered(int width, Panel panel)
    {
        var console = new TestConsole();

        console.Profile.Width = width;
        console.Write(panel);

        return console;
    }

    /// <summary>One message's panel, with a third party who did not see it.</summary>
    /// <param name="width">The width it is going to be drawn at.</param>
    /// <returns>The panel.</returns>
    private static Panel Panel(int width)
    {
        var message = new Message(
            1,
            7,
            MessageType.Question,
            AgentName.Create("orders@alpha"),
            AgentName.Create("billing@alpha"),
            "test",
            "{\"intent\":\"why\",\"sub_questions\":[{\"id\":\"q1\",\"text\":\"which one\"}],"
            + "\"consequences\":\"the answer decides which of two paths is taken here\"}",
            "conv",
            DateTimeOffset.UnixEpoch,
            null);

        // A third participant, so Visibility.Describe has somebody to name as absent.
        return TranscriptView.Panel(
            message,
            [
                AgentName.Create("orders@alpha"),
                AgentName.Create("billing@alpha"),
                AgentName.Create("inventory@alpha")
            ],
            new Dictionary<long, ExchangeSnapshot>(),
            width);
    }
}
