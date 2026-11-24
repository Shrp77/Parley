using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// That a search's words fold the capitals A to Z, both ends of the range included, and nothing else (SPEC.md §10.1).
/// </summary>
/// <remarks>
/// The broker folds a search's words with <see cref="HistoryText.AsciiLower"/> and the store compares them with SQLite's
/// <c>lower()</c>, which folds only A to Z. A capital left unfolded never matches, so a search for a word with a Z in it
/// finds nothing. No test held either end of the range: the fold stopping before Z passed every history test.
/// </remarks>
[Unit]
public sealed class TheAsciiFoldTakesEveryCapitalTests
{
    [Fact]
    public void Every_capital_from_A_to_Z_is_folded()
    {
        Assert.Equal("abcdefghijklmnopqrstuvwxyz", HistoryText.AsciiLower("ABCDEFGHIJKLMNOPQRSTUVWXYZ"));
    }

    [Fact]
    public void Nothing_beside_the_capitals_is_folded()
    {
        // '@' and '[' sit either side of A to Z, '`' and '{' either side of a to z; the rest are capitals SQLite's lower()
        // leaves alone, so folding them would make a word unfindable the other way.
        var kept = "@[`{" + char.ConvertFromUtf32(0xC4) + char.ConvertFromUtf32(0x212A) + char.ConvertFromUtf32(0x130);

        Assert.Equal(kept, HistoryText.AsciiLower(kept));
    }
}
