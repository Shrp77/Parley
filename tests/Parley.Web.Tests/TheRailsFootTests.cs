using Xunit;

namespace Parley.Web.Tests;

/// <summary>The navigation rail ends with Parley's settings and then the collapse button, always at the bottom.</summary>
/// <remarks>
/// <b>The owner, 2026-11-13:</b> collapsed, the expand button sat just under the last item instead of at the bottom where
/// the collapse button is when expanded, and Parley's Settings sat between the console's items and a project's, which
/// looked wrong inside a project. Put the button at the bottom always, with Settings just above it.
/// </remarks>
[Integration]
public sealed class TheRailsFootTests
{
    [Fact]
    public async Task Inside_a_project_settings_and_the_toggle_come_after_the_projects_items()
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/?tab=agents&project=alpha", UriKind.Relative), TestContext.Current.CancellationToken);

        var lastProjectItem = page.IndexOf("href=\"/Projects/Settings?project=alpha\"", StringComparison.Ordinal);
        var foot = page.IndexOf("<nav class=\"rail-foot\"", StringComparison.Ordinal);
        var settings = page.IndexOf("href=\"/Settings\"", StringComparison.Ordinal);
        var toggle = page.IndexOf("id=\"rail-toggle\"", StringComparison.Ordinal);

        Assert.True(lastProjectItem >= 0 && foot >= 0 && settings >= 0 && toggle >= 0, "the rail's items, foot, settings or toggle were not drawn");
        Assert.True(lastProjectItem < foot, "the foot no longer comes after the project's items");
        Assert.True(foot < settings && settings < toggle, "Settings is no longer in the foot, just above the toggle");
        Assert.Equal(settings, page.LastIndexOf("href=\"/Settings\"", StringComparison.Ordinal));
    }

    [Fact]
    public void The_foot_is_pushed_to_the_bottom_and_nothing_pulls_the_toggle_up()
    {
        // Collapsed, the items' rule gave the toggle a margin of its own, which undid the margin that pushed it down.
        // The foot holds the push now, on a rule the collapsed one does not touch.
        var layout = ScriptSource.Page("src/Parley.Web/Pages/Shared/_Layout.cshtml");

        Assert.Contains(".rail nav.rail-foot { margin-top: auto;", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("rail-foot {", layout.Replace(".rail nav.rail-foot { margin-top: auto;", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("<nav class=\"rail-foot\"", layout, StringComparison.Ordinal);
        Assert.True(
            layout.IndexOf("<nav class=\"rail-foot\"", StringComparison.Ordinal) < layout.IndexOf("id=\"rail-toggle\"", StringComparison.Ordinal),
            "the toggle no longer comes after the foot");
    }
}
