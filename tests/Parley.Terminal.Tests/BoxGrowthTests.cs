using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// How far the box grows, and what it never gives up to grow.
/// </summary>
/// <remarks>
/// <b>Every number here is read off the laid-out screen.</b> The box's rows are the one thing about this
/// region that the text in it cannot tell you: it was right about what was written while being nought rows
/// tall, twice, and both times the owner found it rather than the suite.
/// </remarks>
[Integration]
public sealed class BoxGrowthTests
{
    [Fact]
    public async Task It_grows_with_the_message_and_stops_at_its_bound()
    {
        await using var seated = await Seated.OpenAsync(TestContext.Current.CancellationToken);
        var shell = seated.Shell;

        seated.Lay();
        seated.Focus<LineRegion>();

        var rows = new List<int>();

        foreach (var many in new[] { 0, 200, 600, 4000 })
        {
            shell.Line.Written = new string('a', many);

            await shell.Settled;

            seated.Lay();

            rows.Add(shell.Line.BoxRows);
        }

        // Growing, and then held: four thousand characters is more rows than any terminal has.
        Assert.True(rows[1] > rows[0], $"it did not grow: {string.Join(", ", rows)}");
        Assert.True(rows[2] > rows[1], $"it stopped growing early: {string.Join(", ", rows)}");
        Assert.Equal(LineRegion.MostBoxRows, rows[3]);
    }

    [Fact]
    public async Task Typing_past_the_edge_grows_the_box_once_and_it_stays_grown()
    {
        // **The owner's report of 2026-10-03**: as the text began to wrap, the box flickered and bounced up and down until
        // enough was typed to settle it. The height was measured from the text's current width, the scrollbar appears and
        // takes a column whenever the text needs more rows than the box has, and the measure counted characters while the
        // toolkit wraps at words — so near the edge the box grew, lost its scrollbar, measured one row again, shrank,
        // gained the scrollbar, and grew. Every keystroke and every redraw went round again.
        //
        // Typed a key at a time, with prose rather than one long word, because word wrap is half of it; and laid out twice
        // after each key, because a size that changes between two layouts of the same text is the bounce itself.
        await using var seated = await Seated.OpenAsync(TestContext.Current.CancellationToken);
        var shell = seated.Shell;

        seated.Lay();
        seated.Focus<LineRegion>();

        var heights = new List<int>();

        foreach (var key in string.Concat(Enumerable.Repeat("the build is green on my side ", 12)))
        {
            seated.Session.Type(key.ToString());
            await shell.Settled;

            seated.Lay();
            var first = shell.Line.BoxRows;

            seated.Lay();
            var second = shell.Line.BoxRows;

            Assert.True(first == second, $"after {heights.Count + 1} keys the box was {first} rows and then {second}");

            heights.Add(second);
        }

        for (var at = 1; at < heights.Count; at++)
        {
            Assert.True(
                heights[at] >= heights[at - 1],
                $"the box shrank from {heights[at - 1]} to {heights[at]} rows at key {at + 1} while text was only added");
        }

        Assert.True(heights[^1] > heights[0], "the box never grew at all");
    }

    [Fact]
    public async Task And_keeps_its_rows_beside_a_preview_that_names_many_recipients()
    {
        // **The state the owner was in.** The preview names every recipient, so a conversation of sixteen
        // agents is a seventeen-row preview — and the box used to be given whatever that left, which was
        // nothing: it stopped growing at two lines and then lost the cursor altogether.
        await using var seated = await Seated.OpenAsync(TestContext.Current.CancellationToken);
        var shell = seated.Shell;

        seated.Lay();
        seated.Focus<LineRegion>();

        for (var at = 0; at < 14; at++)
        {
            shell.Drafting!.Widen(AgentName.Create($"agent{at}@test"));
        }

        shell.Line.Show(shell.Drafting, seated: true);
        shell.Line.Written = new string('a', 600);

        await shell.Settled;

        seated.Lay();

        Assert.True(shell.Line.BoxRows > 4, $"the box got {shell.Line.BoxRows} rows beside the preview");

        // And the preview is what gave way, bounded so that naming everybody cannot take the box's rows.
        Assert.True(
            shell.Line.Rows - shell.Line.BoxRows - 2 <= LineRegion.MostPreviewRows,
            $"the preview took {shell.Line.Rows - shell.Line.BoxRows - 2} rows");
        Assert.Contains(new string('a', 40), seated.Session.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_box_shows_where_you_are_once_there_is_more_than_fits()
    {
        // Asked for by name: a box that grows to a bound and then scrolls has hidden something, and a reader
        // with no scrollbar cannot tell that from having written less than they thought.
        await using var seated = await Seated.OpenAsync(TestContext.Current.CancellationToken);
        var shell = seated.Shell;

        seated.Lay();
        seated.Focus<LineRegion>();

        var before = shell.Line.BoxWidth;

        shell.Line.Written = new string('a', 4000);

        await shell.Settled;

        seated.Lay();

        // The scrollbar takes a column from the text, which is how a headless test sees one appear.
        Assert.True(
            shell.Line.BoxWidth < before,
            $"no scrollbar appeared: the box is {shell.Line.BoxWidth} columns and was {before}");
    }
}
