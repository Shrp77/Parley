using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// That <see cref="OwnershipStatement.Flatten"/> makes a model's paragraphs into a line and changes no word (SPEC.md
/// §10.2).
/// </summary>
/// <remarks>
/// <b>The second half is what makes <see cref="OwnershipSource.Automatic"/> a truthful claim.</b> An answer accepted
/// automatically is recorded as the agent's own words; a flatten that repaired anything beyond whitespace would make
/// that the machine's words wearing the agent's name.
/// </remarks>
[Unit]
public sealed class FlatteningChangesNoWordTests
{
    [Fact]
    public void A_multi_paragraph_answer_becomes_a_valid_line_with_the_same_words()
    {
        const string answer = """
            1. I own orders: taking them, pricing them, and their totals.

            2. I do not own tax,
               which billing@server-b owns.
            """;

        var line = OwnershipStatement.Flatten(answer);

        Assert.Equal(
            "1. I own orders: taking them, pricing them, and their totals. 2. I do not own tax, which " +
            "billing@server-b owns.",
            line);
        Assert.True(OwnershipStatement.IsValid(line, out _));
        Assert.Equal(Words(answer), Words(line));
    }

    [Theory]
    [InlineData("orders\u2028and totals", "orders and totals")]
    [InlineData("orders\u2029and totals", "orders and totals")]
    [InlineData("orders\tand\r\ntotals", "orders and totals")]
    [InlineData("  orders  ", "orders")]
    [InlineData("orders\u00A0totals", "orders totals")]
    public void Every_kind_of_break_and_run_of_space_is_one_space(string answer, string expected)
    {
        Assert.Equal(expected, OwnershipStatement.Flatten(answer));
    }

    [Theory]
    [InlineData("orders\u202Esdrawkcab")]
    [InlineData("orders\u200Bhidden")]
    [InlineData("orders\u0007bell")]
    public void A_character_that_is_not_whitespace_is_left_for_the_rule_to_refuse(string answer)
    {
        // Stripping it would admit the intent and repair the symptom, and it would be a change to what the agent
        // wrote. So it stays, the line is refused, and the answer waits for a person.
        var line = OwnershipStatement.Flatten(answer);

        Assert.Equal(answer, line);
        Assert.False(OwnershipStatement.IsValid(line, out _));
    }

    [Fact]
    public void An_answer_of_nothing_but_space_is_empty()
    {
        Assert.Equal(string.Empty, OwnershipStatement.Flatten(" \n\u2028\t "));
    }

    private static string[] Words(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}
