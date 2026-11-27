using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That Parley as a whole has a settings page, reached from every page, with the browser's appearance choices and what is
/// on this machine, and that it changes nothing in the deployment.
/// </summary>
[Integration]
public sealed class ParleySettingsTests
{
    [Fact]
    public async Task The_page_offers_the_theme_and_density_and_says_what_is_on_this_machine()
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/Settings", UriKind.Relative), TestContext.Current.CancellationToken);

        foreach (var choice in new[] { "name=\"theme\" value=\"system\"", "name=\"theme\" value=\"light\"", "name=\"theme\" value=\"dark\"", "name=\"density\" value=\"compact\"" })
        {
            Assert.Contains(choice, page, StringComparison.Ordinal);
        }

        Assert.Contains($"parley-web-ui {WebBuild.Version}", page, StringComparison.Ordinal);

        // The appearance choices are kept by script in this browser and never sent. The one form that posts is the Inbox's
        // closed window, which the terminal reads too (spec 036).
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "method=\"post\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        Assert.Contains("action=\"/Settings?handler=Inbox\"", page, StringComparison.Ordinal);
        Assert.Contains("localStorage.setItem(key, value)", ScriptSource.Code(page, "function write(key, value)"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_closed_window_is_offered_with_its_default_and_its_range()
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/Settings", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Matches("""<input id="closed-days" name="Days" type="number"[^>]*min="1" max="365"[^>]*value="7" />""", page);
    }

    [Fact]
    public async Task Each_project_is_listed_with_what_its_broker_said()
    {
        // This console's broker answers Implements with the protocol and no build, so the row says both halves.
        using var console = new RoadmapConsole();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/Settings", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Contains($"/Projects/Agents?project={RoadmapConsole.Project}", page, StringComparison.Ordinal);
        Assert.Contains($"Build not said, protocol {Parley.Core.ProtocolVersion.Current}", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Not answering", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_broker_that_does_not_answer_is_said_to_be_not_answering()
    {
        using var console = new DeadBrokerConsole();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/Settings", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Contains("Not answering", page, StringComparison.Ordinal);
        Assert.Contains($">{DeadBrokerConsole.Project}</a>", page, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Inbox")]
    public async Task Every_page_links_to_parley_settings_from_the_navigation(string path)
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Contains("<a href=\"/Settings\" title=\"Settings\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_chosen_theme_is_applied_before_the_stylesheet_is_read()
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/Settings", UriKind.Relative), TestContext.Current.CancellationToken);

        var applied = page.IndexOf("localStorage.getItem(\"parley.theme\")", StringComparison.Ordinal);

        Assert.True(applied > 0);

        // And it is code, not a comment, in the script that sets the theme.
        Assert.Contains(
            "if (theme === \"light\" || theme === \"dark\") { document.documentElement.setAttribute(\"data-theme\", theme); }",
            ScriptSource.Code(page, "var theme = localStorage.getItem(\"parley.theme\")"),
            StringComparison.Ordinal);
        // Before the first stylesheet the page links, so the page is never drawn in the other theme first.
        var stylesheet = page.IndexOf("<link rel=\"stylesheet\"", StringComparison.Ordinal);

        Assert.True(stylesheet > 0, "the page links no stylesheet");
        Assert.True(applied < stylesheet);
    }
}
