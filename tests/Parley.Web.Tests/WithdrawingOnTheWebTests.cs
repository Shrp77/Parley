using System.Net;
using System.Text.RegularExpressions;
using Parley.Setup;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// Retiring, reinstating and purging from the web console: the same door as the terminal's.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's rule: the two surfaces must behave the same.</b> They had drifted. The terminal asked for the agent's
/// name and withdrew the launch entry and the catalogue line; the web console took one click and did neither. Both now
/// call <see cref="AgentWithdrawal"/>, and these hold the web half of it.
/// </para>
/// <para>
/// <b>Reached with no broker listening, on purpose.</b> Every guard here must hold before anything is sent, so a console
/// that left one to the broker would answer "could not be reached" instead of the refusal asserted.
/// </para>
/// </remarks>
[Integration]
[Slow]
public sealed class WithdrawingOnTheWebTests
{
    private static string Agent => $"orders@{DeadBrokerConsole.Project}";

    private static string PathFor(string action, string subject) =>
        $"/Projects/Act?project={DeadBrokerConsole.Project}&action={action}&subject={subject}";

    [Theory]
    [InlineData("retire")]
    [InlineData("reinstate")]
    [InlineData("purge")]
    public async Task Each_asks_for_the_agents_name_as_the_terminal_does(string action)
    {
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        var body = await client.GetStringAsync(new Uri(PathFor(action, Agent), UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Contains("Type the agent", body, StringComparison.Ordinal);
        Assert.Contains("name=\"Typed\"", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("retire")]
    [InlineData("purge")]
    public async Task Retiring_and_purging_say_they_take_the_files_and_reach_this_project_only(string action)
    {
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        var body = WebUtility.HtmlDecode(
            await client.GetStringAsync(new Uri(PathFor(action, Agent), UriKind.Relative), TestContext.Current.CancellationToken));

        Assert.Contains(".mcp.json", body, StringComparison.Ordinal);
        Assert.Contains($"This happens in {DeadBrokerConsole.Project} only", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("retire", "")]
    [InlineData("retire", "orders")]
    [InlineData("reinstate", " orders@x")]
    [InlineData("purge", "ORDERS@X")]
    public async Task Anything_but_the_exact_name_changes_nothing(string action, string typed)
    {
        // Must fail against a console that acts on a click: with no broker, acting would say it could not be reached.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await PostAsync(client, PathFor(action, Agent), new() { ["Typed"] = typed.Replace("x", DeadBrokerConsole.Project, StringComparison.Ordinal) });

        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Contains(AgentWithdrawal.WrongName, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_persons_seat_is_refused_before_the_broker_is_asked()
    {
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        var seat = $"human-user@{DeadBrokerConsole.Project}";

        using var response = await PostAsync(client, PathFor("retire", seat), new() { ["Typed"] = seat });

        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Contains(AgentWithdrawal.NotAnAgent, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_purge_with_no_acceptance_waits_until_what_it_owes_can_be_read()
    {
        // The terminal asks for an acceptance when the agent still owes work. Here nothing can be read, so nothing is
        // known about what it owes, and the purge is kept rather than done blind.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await PostAsync(client, PathFor("purge", Agent), new() { ["Typed"] = Agent });

        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Contains(AgentWithdrawal.Kept, body, StringComparison.Ordinal);
        Assert.Contains("could not be read", body, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, Dictionary<string, string> fields)
    {
        var token = TestContext.Current.CancellationToken;

        using var opening = new HttpRequestMessage(HttpMethod.Get, path);

        using var page = await client.SendAsync(opening, token);

        var html = await page.Content.ReadAsStringAsync(token);

        var value = Regex.Match(
            html,
            """name="__RequestVerificationToken"[^>]*value="([^"]+)""",
            RegexOptions.None,
            TimeSpan.FromSeconds(2)).Groups[1].Value;

        Assert.NotEmpty(value);

        fields["__RequestVerificationToken"] = value;

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(fields),
        };

        var carried = page.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.Select(c => c.Split(';')[0]).ToList()
            : [];

        if (carried.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", carried));
        }

        return await client.SendAsync(request, token);
    }
}
