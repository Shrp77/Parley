using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>That the conversation's minimap is one column of ticks however long the conversation is.</summary>
/// <remarks>
/// The rail is a <c>nav</c>, and the layout's rule for every <c>nav</c> wraps. A flex column with a capped height that
/// wraps starts a second column when it is full, so a long conversation drew its ticks in two columns. The rail's own
/// rule must say it does not wrap; the narrow layout's horizontal rail says it does, later and on purpose.
/// </remarks>
[Unit]
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
}
