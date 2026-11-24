using Parley.Client.Rendering;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>The one form a message names an exchange in (spec 036, FR-019).</summary>
/// <remarks>
/// <b>The owner, 2026-11-06:</b> "exchange #[XXX]" or "exchanges #[XXX], #[YYY] and #[ZZZ]", and agents are told so. Every
/// other way of writing a number stays plain text, so ordinary numbers never become links.
/// </remarks>
[Unit]
public sealed class ExchangeReferencesTests
{
    [Theory]
    [InlineData("Thanks for the answers on exchange #351.", new long[] { 351 })]
    [InlineData("Exchange #7 is done.", new long[] { 7 })]
    [InlineData("EXCHANGE #12", new long[] { 12 })]
    [InlineData("exchanges #349, #350 and #351", new long[] { 349, 350, 351 })]
    [InlineData("exchanges #349, #350, and #351", new long[] { 349, 350, 351 })]
    [InlineData("Exchanges #12 or #13", new long[] { 12, 13 })]
    [InlineData("exchanges #1,#2", new long[] { 1, 2 })]
    [InlineData("see exchange #4 and exchange #5", new long[] { 4, 5 })]
    [InlineData("(exchange #9)", new long[] { 9 })]
    public void The_form_is_a_reference(string text, long[] expected)
    {
        Assert.Equal(expected, ExchangeReferences.Find(text).Select(found => found.Exchange));
    }

    [Theory]
    [InlineData("Thanks for the answers on 351.")]
    [InlineData("#351")]
    [InlineData("exchange 351")]
    [InlineData("exchange #0351")]
    [InlineData("16,000 characters in 2026")]
    [InlineData("exchange #351abc")]
    [InlineData("subexchange #351")]
    [InlineData("exchange #1234567890123456789")]
    [InlineData("")]
    public void Nothing_else_is(string text)
    {
        Assert.Empty(ExchangeReferences.Find(text));
    }

    [Fact]
    public void Each_reference_covers_exactly_its_hash_and_number()
    {
        const string text = "exchanges #349 and #35";

        Assert.Equal(["#349", "#35"], ExchangeReferences.Find(text).Select(found => text.Substring(found.Start, found.Length)));
    }

    [Fact]
    public void A_list_takes_only_numbers_written_with_their_hash()
    {
        // "and 351" ends the list: a number without its # is not one of them.
        Assert.Equal([349L], ExchangeReferences.Find("exchanges #349 and 351").Select(found => found.Exchange));
    }
}
