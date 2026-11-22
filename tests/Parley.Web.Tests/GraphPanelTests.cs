using System.Net;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// The graph panel every page offers, and the acts it leads to (spec 041).
/// </summary>
/// <remarks>
/// <para>
/// <b>Collapsed, it asks for nothing.</b> The tab is in every page and the fragment is fetched by script only while the panel is
/// open, so a page's own load never reads the graph.
/// </para>
/// <para>
/// <b>It is never inside the roadmap's frame.</b> That frame hides the project's name, and a panel listing every project
/// would put the name back.
/// </para>
/// <para>
/// Run against a console whose broker cannot be dialled, so what is frozen is how the pages behave when nothing answers: the
/// fragment still draws, saying each project could not be read, rather than failing.
/// </para>
/// </remarks>
[Integration]
[Slow]
public sealed class GraphPanelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_page_offers_a_collapsed_tab_and_no_page_reads_the_graph_on_its_own()
    {
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative), Ct);
        var page = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<button type=\"button\" class=\"pg-tab\" id=\"pg-tab\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"pg-panel\"", page, StringComparison.Ordinal);
        Assert.Matches("<aside[^>]*id=\"pg-panel\"[^>]* hidden", page);

        // Nothing in the page's own markup is the graph; the script fetches it.
        Assert.DoesNotContain("<svg class=\"pg-svg\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_panel_remembers_being_pinned_in_the_browser_only()
    {
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        var page = await client.GetStringAsync(new Uri("/", UriKind.Relative), Ct);

        Assert.Contains("localStorage", page, StringComparison.Ordinal);
        Assert.Contains("parley.graph", page, StringComparison.Ordinal);

        // Every access to storage is guarded: it can be absent or refuse, and the panel must still work.
        Assert.Matches(@"try \{ var s = JSON\.parse\(localStorage", page);
    }

    [Fact]
    public async Task A_page_inside_the_roadmaps_frame_has_no_panel()
    {
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"/Conversations/Detail?project={DeadBrokerConsole.Project}&id=rm1-1&embed=1", UriKind.Relative));
        request.Headers.Add("Sec-Fetch-Dest", "iframe");
        request.Headers.Add("Sec-Fetch-Mode", "navigate");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");

        using var response = await client.SendAsync(request, Ct);
        var page = await response.Content.ReadAsStringAsync(Ct);

        Assert.DoesNotContain("id=\"pg-tab\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"pg-panel\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_fragment_draws_when_no_broker_answers_and_the_panel_page_cannot_be_framed()
    {
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await client.GetAsync(new Uri("/Graph", UriKind.Relative), Ct);
        var fragment = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("class=\"pg\"", fragment, StringComparison.Ordinal);
        Assert.Contains("unreachable", fragment, StringComparison.Ordinal);
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.DoesNotContain("<html", fragment, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Freezing_an_agent_is_described_before_anything_is_done()
    {
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        var page = await client.GetStringAsync(
            new Uri($"/Projects/Act?project={DeadBrokerConsole.Project}&action=freeze&subject=orders%40test", UriKind.Relative), Ct);

        Assert.Contains("will be stopped once its current turn ends", page, StringComparison.Ordinal);
        Assert.Contains("What it owes stays open", page, StringComparison.Ordinal);
        Assert.DoesNotContain("This is not something the console does", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Freezing_offers_the_choice_to_keep_it_frozen_after_a_logon_and_unfreezing_does_not()
    {
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        var one = await client.GetStringAsync(
            new Uri($"/Projects/Act?project={DeadBrokerConsole.Project}&action=freeze&subject=orders%40test", UriKind.Relative), Ct);
        var all = await client.GetStringAsync(
            new Uri($"/Projects/Act?project={DeadBrokerConsole.Project}&action=freezeall&subject={DeadBrokerConsole.Project}", UriKind.Relative), Ct);
        var unfreeze = await client.GetStringAsync(
            new Uri($"/Projects/Act?project={DeadBrokerConsole.Project}&action=unfreeze&subject=orders%40test", UriKind.Relative), Ct);

        Assert.Contains("name=\"KeepFrozen\"", one, StringComparison.Ordinal);
        Assert.Contains("Do not unfreeze on logon", one, StringComparison.Ordinal);
        Assert.Contains("name=\"KeepFrozen\"", all, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"KeepFrozen\"", unfreeze, StringComparison.Ordinal);

        // Unchecked by default: the person chooses to keep a freeze, never the other way round.
        Assert.DoesNotMatch("<input[^>]*name=\"KeepFrozen\"[^>]*checked", one);
        Assert.Contains("unfreezes it", one, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unfreezing_an_agent_and_freezing_a_whole_project_are_each_described_too()
    {
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        var unfreeze = await client.GetStringAsync(
            new Uri($"/Projects/Act?project={DeadBrokerConsole.Project}&action=unfreeze&subject=orders%40test", UriKind.Relative), Ct);
        var freezeAll = await client.GetStringAsync(
            new Uri($"/Projects/Act?project={DeadBrokerConsole.Project}&action=freezeall&subject={DeadBrokerConsole.Project}", UriKind.Relative), Ct);
        var unfreezeAll = await client.GetStringAsync(
            new Uri($"/Projects/Act?project={DeadBrokerConsole.Project}&action=unfreezeall&subject={DeadBrokerConsole.Project}", UriKind.Relative), Ct);

        Assert.Contains("can be started again", unfreeze, StringComparison.Ordinal);
        Assert.Contains("may stop work for other projects too", freezeAll, StringComparison.Ordinal);
        Assert.Contains("frozen agent", unfreezeAll,StringComparison.Ordinal);
    }
}
