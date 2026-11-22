using System.Net;
using System.Text.RegularExpressions;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// Stopping and restarting an agent from its page (SPEC.md §10.2 <c>stop</c>, <c>restart</c>; §11.1).
/// </summary>
/// <remarks>
/// Offered only for a running process Parley started, described before it is done, and done only on a POST.
/// </remarks>
[Integration]
public sealed class StoppingAnAgentTests
{
    private static string Agent => $"orders@{RoadmapConsole.Project}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> AgentPageAsync(RoadmapConsole console)
    {
        using var client = console.CreateClient();

        return WebUtility.HtmlDecode(await client.GetStringAsync(
            new Uri($"/Projects/Agent?project={RoadmapConsole.Project}&agent={Agent}", UriKind.Relative), Ct));
    }

    private static Wire.RegistryRow Row(Wire.AgentPresence presence, bool openOutside) => new()
    {
        Name = Agent,
        Adapter = "mcp",
        Tier = Wire.AgentTier.Full,
        Status = Wire.AgentStatus.Active,
        Presence = presence,
        OpenOutside = openOutside,
    };

    [Fact]
    public async Task A_running_agent_Parley_started_offers_stop_and_restart_and_not_start()
    {
        using var console = new RoadmapConsole();
        console.Broker.Registry.Add(Row(Wire.AgentPresence.Running, openOutside: false));

        var page = await AgentPageAsync(console);

        Assert.Contains("action=stop&subject=" + Agent, page, StringComparison.Ordinal);
        Assert.Contains("action=restart&subject=" + Agent, page, StringComparison.Ordinal);
        Assert.DoesNotContain("action=start&subject=" + Agent, page, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Wire.AgentPresence.Running, true)]
    [InlineData(Wire.AgentPresence.Startable, false)]
    [InlineData(Wire.AgentPresence.Stranded, false)]
    public async Task One_open_outside_Parley_or_not_running_offers_start_and_no_stop(
        Wire.AgentPresence presence, bool openOutside)
    {
        using var console = new RoadmapConsole();
        console.Broker.Registry.Add(Row(presence, openOutside));

        var page = await AgentPageAsync(console);

        Assert.Contains("action=start&subject=" + Agent, page, StringComparison.Ordinal);
        Assert.DoesNotContain("action=stop&", page, StringComparison.Ordinal);
        Assert.DoesNotContain("action=restart&", page, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("stop", "next work for it starts it again")]
    [InlineData("restart", "launch configuration afresh")]
    public async Task Each_is_described_and_nothing_is_asked_until_it_is_posted(string action, string said)
    {
        using var console = new RoadmapConsole();
        using var client = console.CreateClient();
        var path = $"/Projects/Act?project={RoadmapConsole.Project}&action={action}&subject={Agent}";

        using var page = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);
        var html = await page.Content.ReadAsStringAsync(Ct);

        Assert.Contains(said, WebUtility.HtmlDecode(html), StringComparison.Ordinal);
        Assert.Contains("What it owes stays owed", WebUtility.HtmlDecode(html), StringComparison.Ordinal);
        Assert.Empty(console.Broker.Stopped);
        Assert.Empty(console.Broker.Restarted);

        var token = Regex.Match(
            html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""", RegexOptions.None, TimeSpan.FromSeconds(2))
            .Groups[1].Value;

        using var posting = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }),
        };

        if (page.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            posting.Headers.Add("Cookie", string.Join("; ", cookies.Select(c => c.Split(';')[0])));
        }

        using var done = await client.SendAsync(posting, Ct);
        var outcome = WebUtility.HtmlDecode(await done.Content.ReadAsStringAsync(Ct));

        var asked = action == "stop" ? console.Broker.Stopped : console.Broker.Restarted;

        Assert.Equal(Agent, Assert.Single(asked).Agent);
        Assert.Contains($"node to {action} it once its current turn is over", outcome, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_a_node_that_is_gone_left_running_offers_kill_and_neither_start_nor_stop_and_says_nobody_opened_it()
    {
        using var console = new RoadmapConsole();
        var row = Row(Wire.AgentPresence.Running, openOutside: true);

        row.Unsupervised = true;
        console.Broker.Registry.Add(row);

        var page = await AgentPageAsync(console);

        Assert.Contains("action=kill&subject=" + Agent, page, StringComparison.Ordinal);
        Assert.DoesNotContain("action=start&", page, StringComparison.Ordinal);
        Assert.DoesNotContain("action=stop&", page, StringComparison.Ordinal);
        Assert.DoesNotContain("action=restart&", page, StringComparison.Ordinal);
        Assert.Contains("Nobody opened it in an editor.", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_somebody_opened_in_an_editor_is_not_offered_kill()
    {
        using var console = new RoadmapConsole();

        console.Broker.Registry.Add(Row(Wire.AgentPresence.Running, openOutside: true));

        var page = await AgentPageAsync(console);

        Assert.DoesNotContain("action=kill&", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Left running.", page, StringComparison.Ordinal);
    }

    private static Wire.RegistryRow LeftRow(bool besideASupervisedOne)
    {
        var row = Row(Wire.AgentPresence.Running, openOutside: !besideASupervisedOne);

        row.Unsupervised = true;

        return row;
    }

    private static async Task<(string Said, string Outcome)> KillAsync(RoadmapConsole console)
    {
        using var client = console.CreateClient();
        var path = $"/Projects/Act?project={RoadmapConsole.Project}&action=kill&subject={Agent}";

        using var page = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);
        var html = await page.Content.ReadAsStringAsync(Ct);

        var token = Regex.Match(
            html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""", RegexOptions.None, TimeSpan.FromSeconds(2))
            .Groups[1].Value;

        using var posting = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }),
        };

        if (page.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            posting.Headers.Add("Cookie", string.Join("; ", cookies.Select(c => c.Split(';')[0])));
        }

        using var done = await client.SendAsync(posting, Ct);

        return (WebUtility.HtmlDecode(html), WebUtility.HtmlDecode(await done.Content.ReadAsStringAsync(Ct)));
    }

    [Fact]
    public async Task Kill_on_an_agent_a_live_node_supervises_says_there_is_nothing_to_end_and_offers_no_confirm()
    {
        // Security run on 0.64.19: the page said the process "was started by a node that is gone" of one the node here
        // supervises, offered Confirm, and the broker's refusal came back as "refused the read".
        using var console = new RoadmapConsole();
        console.Broker.Registry.Add(Row(Wire.AgentPresence.Running, openOutside: false));

        using var client = console.CreateClient();
        var said = WebUtility.HtmlDecode(await client.GetStringAsync(
            new Uri($"/Projects/Act?project={RoadmapConsole.Project}&action=kill&subject={Agent}", UriKind.Relative), Ct));

        Assert.DoesNotContain("node that is gone, so no node holds it", said, StringComparison.Ordinal);
        Assert.Contains("there is nothing to end here", said, StringComparison.Ordinal);
        Assert.DoesNotContain("type=\"submit\"", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Kill_of_a_process_left_beside_one_the_node_supervises_is_offered_and_says_that_one_is_left_alone()
    {
        using var console = new RoadmapConsole();
        console.Broker.Registry.Add(LeftRow(besideASupervisedOne: true));

        var (said, outcome) = await KillAsync(console);

        Assert.Contains("was started by a node that is gone", said, StringComparison.Ordinal);
        Assert.Contains("that the node here started is left alone", said, StringComparison.Ordinal);
        Assert.Contains("type=\"submit\"", said, StringComparison.Ordinal);
        Assert.Contains("Asked the node to end", outcome, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_kill_the_broker_refuses_is_said_as_the_act_and_not_as_a_read()
    {
        using var console = new RoadmapConsole();
        console.Broker.Registry.Add(LeftRow(besideASupervisedOne: false));
        console.Broker.KillRefusal = "its adapter did not say which process hosts it";

        var (_, outcome) = await KillAsync(console);

        Assert.Contains($"the broker refused to end {Agent}'s process. its adapter did not say", outcome, StringComparison.Ordinal);
        Assert.DoesNotContain("refused the read", outcome, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Kill_is_described_first_and_asked_only_on_a_post()
    {
        using var console = new RoadmapConsole();
        console.Broker.Registry.Add(LeftRow(besideASupervisedOne: false));
        using var client = console.CreateClient();
        var path = $"/Projects/Act?project={RoadmapConsole.Project}&action=kill&subject={Agent}";

        using var page = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);
        var html = await page.Content.ReadAsStringAsync(Ct);
        var said = WebUtility.HtmlDecode(html);

        Assert.Contains("ends the process now", said, StringComparison.Ordinal);
        Assert.Contains("whatever it is in the middle of is lost", said, StringComparison.Ordinal);
        Assert.Empty(console.Broker.Killed);

        var token = Regex.Match(
            html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""", RegexOptions.None, TimeSpan.FromSeconds(2))
            .Groups[1].Value;

        using var posting = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }),
        };

        if (page.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            posting.Headers.Add("Cookie", string.Join("; ", cookies.Select(c => c.Split(';')[0])));
        }

        using var done = await client.SendAsync(posting, Ct);
        var outcome = WebUtility.HtmlDecode(await done.Content.ReadAsStringAsync(Ct));

        Assert.Equal(Agent, Assert.Single(console.Broker.Killed).Agent);
        Assert.Contains("Asked the node to end", outcome, StringComparison.Ordinal);
    }
}
