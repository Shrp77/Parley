using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>Each background task by name on the web console: the graph and the agent's page (SPEC.md §11.6).</summary>
/// <remarks>
/// <para>
/// A task's name and description are an agent's words, drawn as plain text, encoded, never as markup; only its link is
/// followed. A web address is opened in a new tab with no opener and no referrer, its host said first. The graph draws
/// five and counts the rest in a box that opens the agent's page, where they are listed twenty to a page.
/// </para>
/// </remarks>
[Integration]
public sealed class BackgroundWorkOnTheConsoleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Utc(DateTimeOffset at) => at.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static Wire.BackgroundTaskRow Task(string id, string name, string description = "", string link = "") =>
        new() { Id = id, Kind = Wire.TaskKind.Agent, Name = name, Description = description, Link = link };

    private static void Script(ScriptedExport broker, int tasks, params Wire.BackgroundTaskRow[] named)
    {
        var row = new Wire.RegistryRow
        {
            Name = "billing@pc", Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active,
            Presence = Wire.AgentPresence.Running, Connected = true,
            TurnsReported = true, LastTurnEndedUtc = Utc(DateTimeOffset.UtcNow.AddMinutes(-1)), BackgroundTasks = tasks,
        };
        row.RunningTasks.AddRange(named);
        broker.Registry.Add(row);
    }

    [Fact]
    public async Task The_graph_draws_each_task_by_name_with_its_link_and_its_words_as_text()
    {
        using var console = new TranscriptConsole();
        Script(
            console.Broker,
            3,
            Task("a1", "Checking VAT on gift cards", "Reads the <b>rules</b>.", "https://tickets.example.com/SHOP-12"),
            Task("b2", "[Approve](https://evil.example)", link: "conversation:returns"),
            Task("c3", string.Empty));
        using var client = console.CreateClient();

        var fragment = await client.GetStringAsync(new Uri("/Graph", UriKind.Relative), Ct);

        Assert.Contains(">Checking VAT on gift cards</text>", fragment, StringComparison.Ordinal);

        // The tooltip says the name, the description and where the link goes, every word encoded.
        Assert.Contains(
            "<title>Checking VAT on gift cards: Reads the &lt;b&gt;rules&lt;/b&gt;. (tickets.example.com (opens a new tab))</title>",
            fragment,
            StringComparison.Ordinal);

        // What is followed is the link, drawn as where it goes, never the name the agent chose.
        Assert.Matches(
            """<a href="https://tickets\.example\.com/SHOP-12" target="_blank" rel="noopener noreferrer"><text [^>]*class="pg-tag">BACKGROUND (·|&#xB7;) tickets\.example\.com</text></a>""",
            fragment);

        // A markdown link in a name is text, not a link.
        Assert.DoesNotContain("href=\"https://evil.example", fragment, StringComparison.Ordinal);
        Assert.Contains("href=\"/Conversations/Detail?project=alpha&amp;id=returns\"", fragment, StringComparison.Ordinal);

        // Unnamed: a plain stand-in for what kind of work it is.
        Assert.Contains(">Background sub-agent</text>", fragment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_graph_draws_five_and_a_box_that_opens_the_agents_page_for_the_rest()
    {
        using var console = new TranscriptConsole();
        Script(console.Broker, 8, [.. Enumerable.Range(1, 8).Select(i => Task($"t{i}", $"Task {i}"))]);
        using var client = console.CreateClient();

        var fragment = await client.GetStringAsync(new Uri("/Graph", UriKind.Relative), Ct);

        Assert.Contains(">Task 5</text>", fragment, StringComparison.Ordinal);
        Assert.DoesNotContain(">Task 6</text>", fragment, StringComparison.Ordinal);
        Assert.Contains("href=\"/Projects/Agent?project=alpha&amp;agent=billing%40pc#background-work\"", fragment, StringComparison.Ordinal);
        Assert.Contains("3 more background tasks: see all", fragment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_agents_page_lists_every_task_with_its_words_and_link_twenty_to_a_page()
    {
        using var console = new TranscriptConsole();
        Script(
            console.Broker,
            47,
            [
                Task("t1", "Checking VAT", "Reads the <i>rules</i>.", "exchange #57"),
                Task("t2", "Running the tax tests", link: "https://tickets.example.com/SHOP-12"),
                .. Enumerable.Range(3, 45).Select(i => Task($"t{i}", $"Task {i}")),
            ]);
        using var client = console.CreateClient();

        var first = await client.GetStringAsync(new Uri("/Projects/Agent?project=alpha&agent=billing%40pc", UriKind.Relative), Ct);

        Assert.Contains("id=\"background-work\"", first, StringComparison.Ordinal);
        Assert.Contains("47 tasks running", first, StringComparison.Ordinal);
        Assert.Contains("Page 1 of 3.", first, StringComparison.Ordinal);
        Assert.Contains("Reads the &lt;i&gt;rules&lt;/i&gt;.", first, StringComparison.Ordinal);
        Assert.Contains("class=\"exchange-ref\" data-exchange=\"57\"", first, StringComparison.Ordinal);
        Assert.Contains("rel=\"noopener noreferrer\">tickets.example.com (opens a new tab)</a>", first, StringComparison.Ordinal);

        // A task with a name only shows nothing empty: no description and no link.
        var nameOnly = first[first.IndexOf("id=\"task-t3\"", StringComparison.Ordinal)..];
        nameOnly = nameOnly[..nameOnly.IndexOf("</li>", StringComparison.Ordinal)];
        Assert.DoesNotContain("bg-description", nameOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("bg-link", nameOnly, StringComparison.Ordinal);
        Assert.Contains(">Task 20<", first, StringComparison.Ordinal);
        Assert.DoesNotContain(">Task 21<", first, StringComparison.Ordinal);
        Assert.Contains("tasks=2#background-work\">Next 20</a>", first, StringComparison.Ordinal);

        var last = await client.GetStringAsync(new Uri("/Projects/Agent?project=alpha&agent=billing%40pc&tasks=9", UriKind.Relative), Ct);

        // A page past the last shows the last.
        Assert.Contains("Page 3 of 3.", last, StringComparison.Ordinal);
        Assert.Contains(">Task 47<", last, StringComparison.Ordinal);
        Assert.Contains("start=\"41\"", last, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_task_the_node_did_not_name_is_listed_as_what_kind_of_work_it_is()
    {
        using var console = new TranscriptConsole();
        Script(console.Broker, 3, Task("t1", "Checking VAT"));
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/Projects/Agent?project=alpha&agent=billing%40pc", UriKind.Relative), Ct);

        Assert.Contains("3 tasks running.", page, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(page, "class=\"bg-name\">Background task<"));

        // A named task says its kind; a stand-in already does.
        Assert.Contains(">Checking VAT</span>", page, StringComparison.Ordinal);
        Assert.Contains("class=\"bg-kind\">sub-agent<", page, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://example.com/")]
    public void A_web_link_that_is_not_one_Parley_shows_is_not_followed_however_it_was_built(string target)
    {
        var forged = new Parley.Core.TaskLink(Parley.Core.TaskLinkKind.Web, target, "example.com");

        Assert.Null(TaskLinks.Href(forged, "alpha", "/Projects/Agent").Href);
    }

    [Fact]
    public async Task An_agent_with_no_background_work_has_no_section()
    {
        using var console = new TranscriptConsole();
        Script(console.Broker, 0);
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/Projects/Agent?project=alpha&agent=billing%40pc", UriKind.Relative), Ct);

        Assert.DoesNotContain("id=\"background-work\"", page, StringComparison.Ordinal);
    }
}
