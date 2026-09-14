using Parley.Core;
using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// A transcript on a terminal that is not wide enough for it.
/// </summary>
/// <remarks>
/// <para>
/// The specification's edge case: the terminal is too narrow, or is resized while running, and
/// nothing that carries meaning is truncated away silently. The last word is the requirement.
/// Wrapping is fine and expected; a value that disappears behind an ellipsis is not, because the
/// reader has no way to tell it was ever there.
/// </para>
/// <para>
/// <b>The hazard moved with the toolkit and did not go away.</b> The Spectre version drew each
/// message as a panel whose header held who sent it, who it was sent to, what kind it was and
/// which exchange it belonged to — and a header that did not fit was truncated. There are no
/// panels now: the region wraps <c>Transcript.Write</c>'s lines. So what is asserted is the same
/// list of facts, at the same widths, against the thing that draws them today.
/// </para>
/// </remarks>
public sealed class NarrowTerminalTests
{
    /// <summary>Narrow enough that a header could not have fitted, and plausible: a split pane.</summary>
    private const int Narrow = 40;

    [Theory]
    [InlineData(Narrow)]
    [InlineData(20)]
    public void Who_a_message_was_to_survives_a_terminal_too_narrow_to_hold_it(int width)
    {
        // The recipient is not decoration. A transcript that says who spoke and not who they
        // spoke to has lost the half that makes it a conversation.
        Assert.Contains("billing", Wrapped(width), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Narrow)]
    [InlineData(20)]
    public void What_kind_of_message_it_was_survives_too(int width)
    {
        Assert.Contains("question", Wrapped(width), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_visibility_sentence_wraps_rather_than_vanishing()
    {
        // Carried in words and never by colour, so it has to survive a narrow terminal for the
        // same reason it has to survive a colourless one: it is the only statement that somebody
        // in the conversation did not see this message.
        Assert.Contains("did not see", Wrapped(Narrow), StringComparison.Ordinal);
    }

    [Fact]
    public void The_check_is_looking_at_something()
    {
        // Non-vacuity: at a comfortable width everything asserted above is plainly present, so a
        // failure at 40 or 20 is the width and not a fixture that never said these things.
        var wide = Wrapped(200);

        Assert.Contains("billing", wide, StringComparison.Ordinal);
        Assert.Contains("question", wide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("did not see", wide, StringComparison.Ordinal);
    }

    [Fact]
    public void No_line_is_wider_than_the_terminal_it_is_drawn_on()
    {
        // The other half of the same claim. Text that survives by running off the right edge has
        // not survived; it has simply failed somewhere the assertion was not looking.
        foreach (var line in Written().ReplaceLineEndings("\n").Split('\n')
                     .SelectMany(line => TranscriptRegion.Wrap(line, Narrow)))
        {
            Assert.True(line.Length <= Narrow, $"'{line}' is wider than {Narrow} columns");
        }
    }

    /// <summary>The transcript, wrapped to a width, as one string.</summary>
    /// <param name="width">The terminal width.</param>
    /// <returns>Everything a reader would see.</returns>
    private static string Wrapped(int width) =>
        string.Join(
            Environment.NewLine,
            Written().ReplaceLineEndings("\n").Split('\n')
                .SelectMany(line => TranscriptRegion.Wrap(line, width)));

    /// <summary>One message, with a third party who did not see it.</summary>
    /// <returns>The transcript the region would hold.</returns>
    private static string Written()
    {
        var region = new TranscriptRegion();

        var message = new Message(
            1,
            7,
            MessageType.Request,
            AgentName.Create("orders@alpha"),
            AgentName.Create("billing@alpha"),
            "test",
            "{\"intent\":\"why\",\"items\":[{\"id\":\"q1\",\"kind\":\"question\",\"text\":\"which one\"}],"
            + "\"done_when\":\"the answer decides which of two paths is taken here\"}",
            "conv",
            DateTimeOffset.UnixEpoch,
            null);

        // A third participant, so the visibility derivation has somebody to name as absent. It is
        // given a message of its own because participants come from the messages themselves.
        var other = message with
        {
            Id = 2,
            To = AgentName.Create("inventory@alpha"),
        };

        region.Show("conv", "alpha", new ConversationExport([message, other], []), null);

        return region.Written;
    }
}
