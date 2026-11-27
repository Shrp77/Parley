using Xunit;

namespace Parley.Web.Tests;

/// <summary>Where to go comes first in the rail, and what to do on the page comes after it, the same on every page.</summary>
/// <remarks>
/// <b>The person, 2026-11-25 (feature 042's navigation strategy):</b> navigation is Projects, All Agents and Inbox, then the
/// project's places; a page's commands follow under a heading, a conversation's tools among them; the menu's expand and fold
/// is a narrow handle on its edge; a folded menu folds the project's places into one item.
/// </remarks>
[Integration]
public sealed class NavigationAndCommandsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_pages_commands_come_after_the_navigation_and_before_the_foot()
    {
        var page = await PageAsync("/Inbox");

        var sections = page.IndexOf("<nav aria-label=\"Sections\">", StringComparison.Ordinal);
        var sectionsEnd = page.IndexOf("</nav>", sections, StringComparison.Ordinal);
        var commands = page.IndexOf("class=\"rail-actions\"", StringComparison.Ordinal);
        var foot = page.IndexOf("<nav class=\"rail-foot\"", StringComparison.Ordinal);

        Assert.True(sections >= 0 && commands >= 0 && foot >= 0, "the rail's sections, commands or foot were not drawn");
        Assert.True(sectionsEnd < commands && commands < foot, "the page's commands must follow the navigation and precede the foot");
    }

    [Fact]
    public async Task A_conversations_tools_are_the_rails_commands_drawn_once()
    {
        var page = await PageAsync("/Conversations/Detail?project=alpha&id=shared");

        var commands = page.IndexOf("class=\"rail-actions\"", StringComparison.Ordinal);
        var strip = page.IndexOf("class=\"panel-strip\"", StringComparison.Ordinal);
        var main = page.IndexOf("<main", StringComparison.Ordinal);

        Assert.True(commands >= 0 && commands < strip && strip < main, "the conversation's tools must be in the rail's commands");
        Assert.Equal(strip, page.LastIndexOf("class=\"panel-strip\"", StringComparison.Ordinal));
        Assert.Contains(">This conversation</p>", page[commands..strip], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_framed_conversation_draws_its_tools_in_the_page_and_no_commands_in_a_rail()
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/Conversations/Detail?project=alpha&id=shared&embed=1", UriKind.Relative));
        request.Headers.Add("Sec-Fetch-Dest", "iframe");
        request.Headers.Add("Sec-Fetch-Mode", "navigate");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");

        using var response = await client.SendAsync(request, Ct);
        var page = await response.Content.ReadAsStringAsync(Ct);

        var strip = page.IndexOf("class=\"panel-strip\"", StringComparison.Ordinal);

        Assert.DoesNotContain("class=\"rail-actions\"", page, StringComparison.Ordinal);
        Assert.True(strip > page.IndexOf("id=\"convo-page\"", StringComparison.Ordinal), "a framed conversation keeps its tools in the page");
        Assert.Equal(strip, page.LastIndexOf("class=\"panel-strip\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_handle_is_drawn_by_its_script_and_named_for_the_menu()
    {
        var page = await PageAsync("/Inbox");
        var at = page.IndexOf("id=\"rail-toggle\"", StringComparison.Ordinal);
        var handle = page[page.LastIndexOf("<button", at, StringComparison.Ordinal)..page.IndexOf("</button>", at, StringComparison.Ordinal)];

        Assert.Contains("aria-controls=\"rail\"", handle, StringComparison.Ordinal);
        Assert.Contains(" hidden", handle, StringComparison.Ordinal);
        Assert.Contains(">Menu<", handle, StringComparison.Ordinal);

        // What the handle's script and the stylesheets agree on: the attribute that unfolds the menu over the page, the
        // place the handle's position is kept, and the property it is drawn at.
        var layout = ScriptSource.Page("src/Parley.Web/Pages/Shared/_Layout.cshtml");
        Assert.Contains("data-rail-open", layout, StringComparison.Ordinal);
        Assert.Contains("parley.nav.handle", layout, StringComparison.Ordinal);
        Assert.Contains("button.hidden = false;", layout, StringComparison.Ordinal);
        Assert.Contains("--handle-top", layout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_folded_menu_offers_the_projects_places_as_one_item_holding_them_all()
    {
        var page = await PageAsync("/?tab=agents&project=alpha");
        var start = page.IndexOf("<details class=\"rail-project\">", StringComparison.Ordinal);

        Assert.True(start >= 0, "the project's places have no folded item");

        var fold = page[start..page.IndexOf("</details>", start, StringComparison.Ordinal)];

        foreach (var place in new[] { "/Projects/Agents?project=alpha", "/?tab=agents&amp;project=alpha", "/Roadmaps?project=alpha", "/Projects/Files?project=alpha", "/Projects/Health?project=alpha", "/Projects/Settings?project=alpha" })
        {
            Assert.Contains($"href=\"{place}\"", fold, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("<details class=\"rail-project\">", await PageAsync("/Inbox"), StringComparison.Ordinal);
    }

    private static async Task<string> PageAsync(string path)
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();

        return await client.GetStringAsync(new Uri(path, UriKind.Relative), Ct);
    }
}
