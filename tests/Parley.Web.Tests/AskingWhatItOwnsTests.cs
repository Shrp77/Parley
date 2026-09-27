using System.Net;
using System.Text.RegularExpressions;
using Parley.Web.Seating;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// Asking an agent what it owns, and refusing to write for an agent that is somewhere else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both of these are reached with no broker listening, and that is the point rather than a
/// compromise.</b> Each asserts a guard that must be applied <em>before</em> anything is sent or
/// written — a seat that is not held, and an agent whose machine is not this one — so a console that
/// deferred either check to the broker fails here, where the broker cannot answer at all.
/// </para>
/// <para>
/// <b>The machine rule is the one that carries real risk.</b> 005's review gate found a withdrawal
/// deleting a <em>different</em> agent's launch configuration, because the workspace came from the
/// registry and the registry is what the agent said about itself. A console writing for an agent on
/// another machine has no file to be careful with at all, which is worse: it would either write
/// somebody else's or report success having written nothing.
/// </para>
/// </remarks>
public sealed class AskingWhatItOwnsTests
{
    [Fact]
    public async Task The_ask_is_described_before_it_is_offered()
    {
        // A GET describes and only a POST does, so a link cannot spend anybody's turn -- including a
        // link followed by whatever crawls a page left open. Must fail against an ask reachable by
        // navigation.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await client.GetAsync(
            new Uri(
                $"/Projects/Act?project={DeadBrokerConsole.Project}"
                + $"&action=askownership&subject=orders@{DeadBrokerConsole.Project}",
                UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("costs it a turn", body, StringComparison.Ordinal);
        Assert.Contains("Nothing is written by", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Asking_with_no_seat_says_a_seat_is_needed_rather_than_instructing_instead()
    {
        // **The defect this forbids is the console quietly using the operator's voice.** §13 rests on
        // a receiver being able to tell who spoke: an instruction in place of a message would put the
        // question in front of the agent with nothing in the transcript and nothing owed back. Must
        // fail against falling through to `InstructAsync`, which against a dead broker would report a
        // transport failure instead of this sentence.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await PostAsync(
            client,
            $"/Projects/Act?project={DeadBrokerConsole.Project}"
            + $"&action=askownership&subject=orders@{DeadBrokerConsole.Project}",
            []);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("no seat", body, StringComparison.Ordinal);
        Assert.Contains("Nothing was asked", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_written_for_an_agent_whose_machine_is_not_this_one()
    {
        // **research.md R3, applied by this surface as well as the terminal.** The rule that replaced
        // one-writer is not "the web UI may not write" but "no surface writes for an agent whose
        // machine is not this one" -- a launch configuration is a file on that machine, and neither
        // surface is guaranteed to be on it.
        //
        // Must fail against the write being attempted: the refusal names the machine, which nothing
        // downstream of a `.mcp.json` write would.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await PostAsync(
            client,
            $"/Projects/Act?project={DeadBrokerConsole.Project}"
            + "&action=ownership&subject=orders@somebody-elses-laptop",
            new() { ["Text"] = "Orders and invoicing." });

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Not recorded", body, StringComparison.Ordinal);
        Assert.Contains("somebody-elses-laptop", body, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string path,
        Dictionary<string, string> fields,
        string? seat = null)
    {
        var token = TestContext.Current.CancellationToken;

        using var opening = new HttpRequestMessage(HttpMethod.Get, path);

        if (seat is not null)
        {
            opening.Headers.Add("Cookie", $"{SeatCookie.Key}={seat}");
        }

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

        if (seat is not null)
        {
            carried.Add($"{SeatCookie.Key}={seat}");
        }

        if (carried.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", carried));
        }

        return await client.SendAsync(request, token);
    }
}
