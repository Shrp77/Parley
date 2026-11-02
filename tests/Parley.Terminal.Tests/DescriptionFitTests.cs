using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That a description written on several lines is drawn on several lines.
/// </summary>
/// <remarks>
/// <b>Found by the review gate, probing the dialog the picker fix had just changed.</b>
/// <c>Prompting.Rows</c> measured a label by counting its characters and dividing, which is right
/// for a sentence and wrong for anything written with line breaks in it — eight short lines came
/// out as two rows, and the other six were drawn off the bottom of the label. The add
/// confirmation names two file paths on lines of their own, so it is exactly that shape.
/// </remarks>
[Integration]
public sealed class DescriptionFitTests
{
    [Fact]
    public void A_row_is_counted_for_every_line_the_text_already_has()
    {
        Assert.Equal(4, Prompting.Rows("one\ntwo\nthree\nfour"));
        Assert.Equal(4, Prompting.Rows("one\r\ntwo\r\nthree\r\nfour"));
    }

    [Fact]
    public void A_long_line_still_counts_the_rows_it_will_wrap_to()
    {
        // Both rules at once, which is the case the add confirmation actually is: lines of its
        // own, one of which is a paragraph.
        Assert.Equal(1, Prompting.Rows("short"));
        Assert.True(Prompting.Rows(new string('x', 200)) >= 3);
        Assert.True(Prompting.Rows("short\n" + new string('x', 200)) > Prompting.Rows(new string('x', 200)));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(40)]
    public void Every_part_of_the_add_confirmation_reaches_the_screen(int height)
    {
        using var driving = Driving.Open(width: 100, height: height);

        var described = Adding.Describe(
            "C:/work/orders", "C:/store/alpha-node.json", "alpha");

        var dialog = new ChoiceDialog(Adding.ConfirmTitle, described, [Adding.NotNow, Adding.Write]);

        driving.Show(dialog);

        var screen = driving.Text();

        // Short fragments, one per part, chosen so no wrap can split them. Both files and the
        // process it is about to start — which is the whole of what somebody is agreeing to.
        foreach (var fragment in new[] { ".mcp.json", "alpha-node.json", "registers itself", "own server" })
        {
            Assert.Contains(fragment, screen, StringComparison.Ordinal);
        }
    }
}
