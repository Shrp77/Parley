using Parley.Client.Rendering;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>A spin-off's first message says what it is and where it came from (SPEC.md §5.0).</summary>
[Unit]
public sealed class QuotingOpenedTests
{
    private const string Entry =
        "Side conversation disk-cleanup has been stood up to discuss the following conversation entry from shared.";

    [Theory]
    [InlineData("orders@test said in shared:\n> The disk is full.\n")]
    [InlineData("orders@test said in shared:\r\n> The disk is full.\r\n")]
    [InlineData("Please look.\n  > The disk is full.")]
    public void A_box_holding_a_quote_is_about_that_entry(string text)
    {
        var opened = Quoting.Opened("disk-cleanup", "shared", text);

        Assert.StartsWith(Entry + "\n\n", opened, StringComparison.Ordinal);
        Assert.EndsWith(text, opened, StringComparison.Ordinal);
    }

    [Fact]
    public void A_box_with_no_quote_does_not_speak_of_an_entry()
    {
        Assert.Equal(
            "Side conversation disk-cleanup has been stood up from shared.\n\nClear the old logs.",
            Quoting.Opened("disk-cleanup", "shared", "Clear the old logs."));
    }

    [Fact]
    public void The_line_break_a_terminal_quote_starts_with_leaves_no_gap_beyond_the_one_after_the_line()
    {
        Assert.Equal(
            Entry + "\n\norders@test said in shared:\n> full",
            Quoting.Opened("disk-cleanup", "shared", "\norders@test said in shared:\n> full"));
    }

    [Fact]
    public void A_box_that_starts_with_a_carriage_return_and_line_feed_leaves_no_gap_either()
    {
        // Review, quality lens (0.62.9): a mutation trimming only the line feed survived, though a box written with CRLF
        // endings is the case the trim of the carriage return exists for.
        Assert.Equal(
            Entry + "\n\norders@test said in shared:\r\n> full",
            Quoting.Opened("disk-cleanup", "shared", "\r\norders@test said in shared:\r\n> full"));
    }
}
