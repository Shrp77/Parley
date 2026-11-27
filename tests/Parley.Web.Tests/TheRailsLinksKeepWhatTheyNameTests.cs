using System.Net;
using System.Text.RegularExpressions;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// The commands feature 042 moved into the rail still carry what they name: the conversation, the project, the roadmap
/// and the filter they lead to, and a new conversation's project.
/// </summary>
/// <remarks>
/// <b>Found by review.</b> Each of these links and fields could lose a part of its address, and every test passed, before
/// the move and after it: Media's "Its files" leading to every file in the project, the file problems page's two views
/// leading to the same one, Health's prune with no subject, the roadmap editor's way back with no roadmap, and either
/// new-conversation form posting no project.
/// </remarks>
[Integration]
public sealed partial class TheRailsLinksKeepWhatTheyNameTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Medias_links_keep_the_conversation_they_are_for()
    {
        using var console = new FilesConsole();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri($"/Conversations/Media?project={FilesConsole.Project}&id=pricing", UriKind.Relative), Ct);

        Assert.Equal("/Projects/Files?project=alpha&conversation=pricing", RailLink(page, "Files, to place or remove"));
        Assert.Equal("/Conversations/Detail?project=alpha&id=pricing", RailLink(page, "Back to the conversation"));
    }

    [Fact]
    public async Task The_file_problems_pages_two_views_lead_to_each_other()
    {
        using var console = new FilesConsole();
        using var client = console.CreateClient();
        const string Page = "/Projects/FileProblems?project=" + FilesConsole.Project;

        var waiting = await client.GetStringAsync(new Uri(Page, UriKind.Relative), Ct);
        var all = await client.GetStringAsync(new Uri(Page + "&all=true", UriKind.Relative), Ct);

        Assert.Equal("/Projects/FileProblems?project=alpha&all=true", RailLink(waiting, "Show the dismissed ones too"));
        Assert.Null(RailLinkOrNull(waiting, "Only the waiting ones"));
        Assert.Equal("/Projects/FileProblems?project=alpha", RailLink(all, "Only the waiting ones"));
        Assert.Null(RailLinkOrNull(all, "Show the dismissed ones too"));
    }

    [Fact]
    public async Task Healths_prune_names_the_project_it_prunes()
    {
        var page = await TranscriptPageAsync($"/Projects/Health?project={TranscriptConsole.Project}");

        Assert.Equal("/Projects/Act?project=alpha&action=prune&subject=alpha", RailLink(page, "Apply the retention policy now"));
    }

    [Fact]
    public async Task The_roadmap_editors_way_back_names_the_roadmap()
    {
        using var console = new RoadmapConsole();
        var detail = new Wire.RoadmapDetail { Id = 4, Number = 4, Title = "Billing export", Revision = 1 };

        detail.Items.Add(new Wire.RoadmapItemDetail
        {
            Id = 1, Number = 1, Position = 1, Text = "Add CSV export", IntendedAgent = "billing@test", ConversationName = "rm4-1",
            Facts = new Wire.ItemFacts { ConversationExists = false },
        });
        console.Broker.Details[4] = detail;
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri($"/Roadmaps/Edit?project={RoadmapConsole.Project}&id=4", UriKind.Relative), Ct);

        Assert.Equal("/Roadmaps/Roadmap?project=alpha&id=4", RailLink(page, "Back to the roadmap"));
    }

    [Fact]
    public async Task The_conversation_lists_new_conversation_form_posts_its_project()
    {
        var form = NewConversationForm(await TranscriptPageAsync($"/Projects/Agents?project={TranscriptConsole.Project}"));

        Assert.Contains("<input type=\"hidden\" name=\"project\" value=\"alpha\" />", form, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_inboxs_new_conversation_form_posts_the_project_chosen()
    {
        // Only a project the person is seated in, or one with work waiting on them, is offered, so the seat is held.
        using var console = new RoadmapConsole();

        console.Broker.Seats = true;
        console.Broker.Registry.Add(new Wire.RegistryRow
        {
            Name = "human-user@" + RoadmapConsole.Project,
            Adapter = "parley-console",
            Tier = Wire.AgentTier.Full,
            Status = Wire.AgentStatus.Active,
        });
        using var client = console.CreateClient();

        var form = NewConversationForm(await client.GetStringAsync(new Uri("/Inbox", UriKind.Relative), Ct));
        var select = Regex.Match(form, "<select id=\"new-conversation-project\" name=\"project\" required>(?<options>.*?)</select>", RegexOptions.Singleline, TimeSpan.FromSeconds(2));

        Assert.True(select.Success, "the form has no project field named project");
        Assert.Contains("<option value=\"alpha\">alpha</option>", select.Groups["options"].Value, StringComparison.Ordinal);
    }

    [GeneratedRegex("""<a class="rail-link" href="(?<href>[^"]*)" title="(?<title>[^"]*)">""")]
    private static partial Regex RailLinks();

    private static async Task<string> TranscriptPageAsync(string path)
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();

        return await client.GetStringAsync(new Uri(path, UriKind.Relative), Ct);
    }

    /// <summary>The address of the rail link with the given title, decoded, or null when the page draws none.</summary>
    private static string? RailLinkOrNull(string page, string title)
    {
        var links = RailLinks().Matches(page).Where(m => WebUtility.HtmlDecode(m.Groups["title"].Value) == title).ToList();

        Assert.True(links.Count <= 1, $"the rail has {links.Count} links titled \"{title}\"");
        return links.Count == 0 ? null : WebUtility.HtmlDecode(links[0].Groups["href"].Value);
    }

    private static string RailLink(string page, string title) =>
        RailLinkOrNull(page, title) ?? throw new Xunit.Sdk.XunitException($"the rail has no link titled \"{title}\"");

    /// <summary>The drawn form that starts a conversation: the one with the box for its name.</summary>
    private static string NewConversationForm(string page)
    {
        var box = page.IndexOf("<input id=\"new-conversation\" name=\"id\"", StringComparison.Ordinal);

        Assert.True(box >= 0, "the page draws no box to name a new conversation");

        var start = page.LastIndexOf("<form method=\"get\" action=\"/Conversations/Detail\">", box, StringComparison.Ordinal);

        Assert.True(start >= 0, "the box to name a new conversation is in no form that opens one");
        Assert.True(page.IndexOf("</form>", start, StringComparison.Ordinal) > box, "the box to name a new conversation is outside its form");

        return page[start..page.IndexOf("</form>", start, StringComparison.Ordinal)];
    }
}
