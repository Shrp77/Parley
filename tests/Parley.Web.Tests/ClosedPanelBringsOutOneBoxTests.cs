using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>That a closed conversation panel's icons each bring out one of its boxes.</summary>
/// <remarks>
/// A closed panel is a strip of icons, and a narrow window starts with it closed. Each icon names a part of the panel
/// (<c>data-pop</c>), and the stylesheet shows only that part in the box the icon brings out. An icon with no rule would
/// bring out the whole panel at once, which is what closing it was for avoiding; a rule with no icon is dead. The boxes
/// must stay inside the one wrapper the stylesheet turns into that box, or a box outside it would sit in the strip.
/// The script is not run here.
/// </remarks>
[Integration]
public sealed class ClosedPanelBringsOutOneBoxTests
{
    private static readonly Uri PageUri = new(
        $"/Conversations/Detail?project={TranscriptConsole.Project}&id=shared", UriKind.Relative);

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Every_icon_has_a_rule_and_every_rule_an_icon_and_the_boxes_are_in_the_wrapper()
    {
        using var console = new TranscriptConsole();

        console.Broker.Request(1, "orders@test", "billing@test", "Which rate applies?");

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, TestContext.Current.CancellationToken);
        var strip = Regex.Match(page, """<div class="panel-strip">[\s\S]*?</div>""", RegexOptions.None, Patience);

        Assert.True(strip.Success, "The page has no strip.");

        var icons = Regex.Matches(strip.Value, """data-pop="([a-z]+)" """.Trim(), RegexOptions.None, Patience)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var layout = ScriptSource.Page("src/Parley.Web/Pages/Shared/_Layout.cshtml");
        var rules = Regex.Matches(layout, """\.panel-closed\[data-pop="([a-z]+)"\] \.panel-body""", RegexOptions.None, Patience)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(icons);
        Assert.Equal(icons.Order(StringComparer.Ordinal), rules.Order(StringComparer.Ordinal));

        var body = page.IndexOf("id=\"panel-body\"", StringComparison.Ordinal);
        var end = page.IndexOf("class=\"convo-main\"", StringComparison.Ordinal);

        Assert.True(body > 0 && end > body, "The panel has no wrapper for its boxes.");
        foreach (var box in new[] { "class=\"panel-tools\"", "id=\"followed-side\"" })
        {
            var at = page.IndexOf(box, StringComparison.Ordinal);

            Assert.True(at > body && at < end, $"{box} is outside the wrapper.");
        }
    }
}
