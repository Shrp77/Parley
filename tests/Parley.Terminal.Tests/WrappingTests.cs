using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// The edges of the one piece of text layout this surface does itself.
/// </summary>
/// <remarks>
/// <para>
/// Lens 1 of the review gate, run 1.6. Everything else on screen is a row in a list; the transcript
/// is prose, and prose has to be broken somewhere. A wrap that loses a character loses it from a
/// conversation somebody is reading to find out what was agreed, and a wrap that loops does it on a
/// message an agent chose the contents of.
/// </para>
/// <para>
/// The inputs below are the ones a real transcript produces: a line exactly the width of the
/// region, a line of nothing, an identifier longer than the region, and a width of one from a
/// terminal being dragged shut.
/// </para>
/// </remarks>
[Unit]
public sealed class WrappingTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("    ")]
    public void Nothing_much_wraps_to_one_line(string line)
    {
        Assert.Equal([line], TranscriptRegion.Wrap(line, 20));
    }

    [Fact]
    public void A_line_exactly_the_width_is_left_alone()
    {
        // The off-by-one that would otherwise appear as an empty row after every full line.
        var line = new string('a', 20);

        Assert.Equal([line], TranscriptRegion.Wrap(line, 20));
    }

    [Fact]
    public void A_line_one_longer_than_the_width_becomes_two()
    {
        var wrapped = TranscriptRegion.Wrap(new string('a', 21), 20);

        Assert.Equal(2, wrapped.Count);
        Assert.Equal(new string('a', 21), string.Concat(wrapped));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_terminal_dragged_almost_shut_still_terminates_and_loses_nothing(int width)
    {
        // A width of one is what a person dragging a pane produces on the way past. The loop has to
        // make progress at every width, and a break that could not fit a single word is the case
        // that stops it.
        var wrapped = TranscriptRegion.Wrap("orders@test asked billing@test", width);

        Assert.All(wrapped, piece => Assert.True(piece.Length <= width, $"'{piece}' is too wide"));
        Assert.Contains("orders", string.Concat(wrapped), StringComparison.Ordinal);
    }

    [Fact]
    public void A_width_of_zero_or_less_gives_the_line_back_rather_than_looping()
    {
        // What a region that has not been laid out yet reports. Dividing the work by it would
        // never finish.
        Assert.Equal(["something"], TranscriptRegion.Wrap("something", 0));
        Assert.Equal(["something"], TranscriptRegion.Wrap("something", -5));
    }

    [Fact]
    public void Words_are_kept_whole_where_they_fit()
    {
        var wrapped = TranscriptRegion.Wrap("the quick brown fox jumps", 10);

        Assert.All(wrapped, piece => Assert.True(piece.Length <= 10));
        Assert.Equal("the quick brown fox jumps", string.Join(" ", wrapped));
    }

    [Fact]
    public void A_word_longer_than_the_region_is_broken_rather_than_lost()
    {
        // Identifiers and paths, which are what somebody opened the transcript to check.
        var wrapped = TranscriptRegion.Wrap(new string('x', 45), 10);

        Assert.Equal(5, wrapped.Count);
        Assert.Equal(new string('x', 45), string.Concat(wrapped));
    }

    [Fact]
    public void The_check_is_looking_at_something()
    {
        // Non-vacuity: a Wrap that returned its input unchanged would pass several cases above.
        Assert.True(TranscriptRegion.Wrap(new string('a', 100), 10).Count > 1);
    }
}
