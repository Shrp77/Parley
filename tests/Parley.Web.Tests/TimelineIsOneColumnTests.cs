using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>That the conversation's minimap is one column of ticks however long the conversation is.</summary>
/// <remarks>
/// The rail is a <c>nav</c>, and the layout's rule for every <c>nav</c> wraps. A flex column with a capped height that
/// wraps starts a second column when it is full, so a long conversation drew its ticks in two columns. The rail's own
/// rule must say it does not wrap; the narrow layout's horizontal rail says it does, later and on purpose.
/// </remarks>
[Integration]
public sealed class TimelineIsOneColumnTests
{
    [Fact]
    public void The_rail_does_not_wrap_into_a_second_column()
    {
        var layout = ScriptSource.Page("src/Parley.Web/Pages/Shared/_Layout.cshtml");
        var rule = Regex.Match(layout, @"^\s*\.timeline\s*\{(?<body>[^}]*)\}", RegexOptions.Multiline, TimeSpan.FromSeconds(5));

        Assert.True(rule.Success, "The layout has no rule for .timeline.");
        Assert.Contains("flex-direction: column;", rule.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains("flex-wrap: nowrap;", rule.Groups["body"].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void The_rail_scrolls_with_no_scrollbar_and_the_page_keeps_the_message_being_read_marked()
    {
        var layout = ScriptSource.Page("src/Parley.Web/Pages/Shared/_Layout.cshtml");
        var rule = Regex.Match(layout, @"^\s*\.timeline\s*\{(?<body>[^}]*)\}", RegexOptions.Multiline, TimeSpan.FromSeconds(5));

        // Hidden both ways a browser draws one, and still a box that scrolls: a bar beside a column of ticks only drew the eye.
        Assert.Contains("scrollbar-width: none;", rule.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains(".timeline::-webkit-scrollbar { display: none; }", layout, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"\.timeline\s*\{[^}]*overflow-y: auto;", RegexOptions.None, TimeSpan.FromSeconds(5)), layout);

        // Following the reader is the page's script, which is not run here: what is held is that it marks a tick and that
        // the ring has a rule, so a marker nothing draws, or one nothing sets, fails.
        var page = ScriptSource.Page("src/Parley.Web/Pages/Conversations/Detail.cshtml");

        Assert.Contains("classList.add(\"here\")", page, StringComparison.Ordinal);
        Assert.Contains(".timeline .tick.here", layout, StringComparison.Ordinal);
    }
}
