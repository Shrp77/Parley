using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// What a search is charged, and what is made of text another participant wrote before it is shown (SPEC.md §10.1, §13).
/// </summary>
/// <remarks>
/// The cost is a pure function so it can be held without waiting on a clock: the floor, the scale by time, the clamp and what is
/// returned. Characters are built from numbers because a raw invisible character in a source file is what is being refused.
/// </remarks>
[Unit]
public sealed class HistoryLimitsAndTextTests
{
    private static string Chars(params int[] codePoints) => string.Concat(codePoints.Select(char.ConvertFromUtf32));

    [Fact]
    public void A_quick_search_is_charged_the_floor_and_what_it_returned()
    {
        Assert.Equal(HistoryLimits.SearchCharge, HistoryLimits.SearchCost(0, 0));
        Assert.Equal(HistoryLimits.SearchCharge, HistoryLimits.SearchCost(5, 0));
        Assert.Equal(HistoryLimits.SearchCharge + 3_000, HistoryLimits.SearchCost(5, 3_000));
    }

    [Fact]
    public void A_slow_search_is_charged_by_its_time()
    {
        Assert.Equal(1_000 * HistoryLimits.CharactersPerMillisecond, HistoryLimits.SearchCost(1_000, 0));
        Assert.Equal((1_000 * HistoryLimits.CharactersPerMillisecond) + 50, HistoryLimits.SearchCost(1_000, 50));
    }

    [Fact]
    public void A_clock_running_backwards_or_a_very_long_search_cannot_overflow_or_go_negative()
    {
        Assert.Equal(HistoryLimits.SearchCharge, HistoryLimits.SearchCost(-5_000, 0));
        Assert.InRange(HistoryLimits.SearchCost(long.MaxValue, 0), HistoryLimits.CharactersPerMinute, int.MaxValue / 2);
        Assert.InRange(HistoryLimits.SearchCost(10_000_000_000, 100), 1, int.MaxValue);
    }

    [Fact]
    public void Odd_spaces_are_refused_in_words_and_become_an_ordinary_space_in_a_snippet()
    {
        foreach (var space in new[] { 0x00A0, 0x3000, 0x2009 })
        {
            Assert.True(HistoryText.Unprintable("a" + Chars(space) + "b"));
            Assert.Equal("a b", HistoryText.Neutralised("a" + Chars(space) + "b"));
        }

        Assert.False(HistoryText.Unprintable("a b"));
    }

    [Fact]
    public void Unassigned_code_points_are_refused_in_words_and_made_a_space_in_a_snippet()
    {
        foreach (var unassigned in new[] { 0x0378, 0xE0080, 0x2FFFE })
        {
            Assert.True(HistoryText.Unprintable("a" + Chars(unassigned) + "b"));
            Assert.Equal("a b", HistoryText.Neutralised("a" + Chars(unassigned) + "b"));
        }
    }

    [Fact]
    public void Plain_text_in_many_scripts_passes_untouched()
    {
        string[] plain = ["naive", "na" + Chars(0x00EF) + "ve", Chars(0x65E5, 0x672C, 0x8A9E), Chars(0x0645, 0x0631, 0x062D, 0x0628, 0x0627), "100%", "a_b", Chars(0x1F680)];

        Assert.All(plain, text => Assert.False(HistoryText.Unprintable(text)));
        Assert.All(plain, text => Assert.Equal(text, HistoryText.Neutralised(text)));
    }

    [Fact]
    public void Cutting_never_splits_a_pair_and_refuses_a_bound_that_cannot_hold_one()
    {
        var text = "a" + Chars(0x1F680) + "b";

        Assert.Equal("a", HistoryText.Cut(text, 2));
        Assert.Equal("a" + Chars(0x1F680), HistoryText.Cut(text, 3));
        Assert.Equal(text, HistoryText.Cut(text, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => HistoryText.Cut("x", 1));
    }
}
