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
/// <b>Every one of these is reached with no broker listening, and that is the point rather than a
/// compromise.</b> Each asserts a guard that must be applied <em>before</em> anything is sent or
/// written — a seat that is not held, and an agent whose machine is not this one — so a console that
/// deferred either check to the broker fails here, where the broker cannot answer at all. The two that
/// describe rather than act assert the same thing from the other side: a GET says what it would cost
/// and only a POST spends it, so no link can spend a turn, least of all one per agent.
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
        Assert.Contains("No seat is needed", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Asking_needs_no_seat_because_Parley_asks()
    {
        // **022's correction.** 020 sent the ask as the person's own Request and refused without a seat, which is the
        // refusal the owner met. Parley asks now, so with no seat the console goes to the broker -- which is dead here, so
        // what comes back is that it could not be reached, never a sentence about a seat. Must fail against the seat
        // refusal coming back.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await PostAsync(
            client,
            $"/Projects/Act?project={DeadBrokerConsole.Project}"
            + $"&action=askownership&subject=orders@{DeadBrokerConsole.Project}",
            []);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("no seat is held", body, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task The_bulk_ask_is_described_before_it_is_offered()
    {
        // FR-006 and FR-007 on this surface. A GET describes and only a POST sends, so no link can spend
        // a turn on every agent in a project -- which is the version of this mistake that costs the most.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        var body = await client.GetStringAsync(
            new Uri(
                $"/Projects/Act?project={DeadBrokerConsole.Project}"
                + $"&action=askeveryownership&subject={DeadBrokerConsole.Project}",
                UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Contains("costs each", body, StringComparison.Ordinal);
        Assert.Contains("its own question", body, StringComparison.Ordinal);
        Assert.Contains("No seat is needed", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_bulk_ask_needs_no_seat_either()
    {
        // As the single ask: Parley asks, so no seat is needed and none is asked for. The broker is dead here, so what
        // comes back is that it could not be reached, never a sentence about a seat.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await PostAsync(
            client,
            $"/Projects/Act?project={DeadBrokerConsole.Project}"
            + $"&action=askeveryownership&subject={DeadBrokerConsole.Project}",
            []);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("no seat is held", body, StringComparison.OrdinalIgnoreCase);
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
