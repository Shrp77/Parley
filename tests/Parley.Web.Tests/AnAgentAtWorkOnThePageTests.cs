using System.Text.RegularExpressions;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>While an agent works on an answer the transcript says so, and its answer takes that place.</summary>
[Integration]
public sealed partial class AnAgentAtWorkOnThePageTests
{
    private const string Orders = "orders@test";
    private const string Person = "human-user@" + TranscriptConsole.Project;

    private static readonly Uri PageUri = new(
        $"/Conversations/Detail?project={TranscriptConsole.Project}&id=shared", UriKind.Relative);

    private static readonly Uri FollowUri = new(
        $"/Conversations/Detail?project={TranscriptConsole.Project}&id=shared&handler=Follow", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("data-seen=\"([^\"]+)\"")]
    private static partial Regex SeenDigest();

    private static TranscriptConsole Asked(bool turnRunning = true, bool turnsReported = true)
    {
        var console = new TranscriptConsole();

        console.Broker.Request(1, Person, Orders, "Gross or net?");
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Person, WaitedOn = Orders, ExchangeId = 1 });
        console.Broker.Registry.Add(new Wire.RegistryRow
        {
            Name = Orders,
            Adapter = "claude-code",
            Status = Wire.AgentStatus.Active,
            TurnsReported = turnsReported,
            TurnRunningSinceUtc = turnRunning
                ? DateTimeOffset.UtcNow.AddMinutes(-3).AddSeconds(-5).ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty,
        });

        return console;
    }

    [Fact]
    public async Task An_agent_owing_an_answer_with_a_turn_running_is_shown_working_with_when_and_how_long()
    {
        using var console = Asked();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, Ct);
        var entry = page.IndexOf("class=\"turn working", StringComparison.Ordinal);

        Assert.True(entry > 0, "no working entry");
        Assert.True(entry > page.IndexOf("id=\"m1\"", StringComparison.Ordinal), "the working entry is not after the request");

        var drawn = page[entry..page.IndexOf("</li>", entry, StringComparison.Ordinal)];

        Assert.Contains(Orders, drawn, StringComparison.Ordinal);
        Assert.Contains(">working<", drawn, StringComparison.Ordinal);
        Assert.Contains("started ", drawn, StringComparison.Ordinal);
        Assert.Contains("running 3m", drawn, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Once_the_answer_is_in_the_entry_is_gone_and_the_answer_is_drawn()
    {
        using var console = Asked();
        using var client = console.CreateClient();

        Assert.Contains("class=\"turn working", await client.GetStringAsync(FollowUri, Ct), StringComparison.Ordinal);

        // The agent answers. Its turn may still be running, and it no longer owes anything here.
        console.Broker.Edges.Clear();
        console.Broker.Request(2, Orders, Person, "Net, **always**.");

        var after = await client.GetStringAsync(FollowUri, Ct);

        Assert.DoesNotContain("class=\"turn working", after, StringComparison.Ordinal);
        Assert.Contains("<strong>always</strong>", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_with_no_turn_running_is_not_shown_working()
    {
        using var console = Asked(turnRunning: false);
        using var client = console.CreateClient();

        Assert.DoesNotContain("class=\"turn working", await client.GetStringAsync(PageUri, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_whose_turns_nobody_reports_is_not_claimed_to_be_working()
    {
        using var console = Asked(turnRunning: false, turnsReported: false);
        using var client = console.CreateClient();

        Assert.DoesNotContain("class=\"turn working", await client.GetStringAsync(PageUri, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_working_that_owes_nothing_here_is_not_shown_working_here()
    {
        using var console = Asked();
        console.Broker.Edges.Clear();

        using var client = console.CreateClient();

        Assert.DoesNotContain("class=\"turn working", await client.GetStringAsync(PageUri, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Waiting_on_an_answer_elsewhere_is_not_owing_here()
    {
        using var console = Asked();
        console.Broker.Edges.Clear();
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Person, WaitedOn = Orders, ExchangeId = 99 });

        using var client = console.CreateClient();

        Assert.DoesNotContain("class=\"turn working", await client.GetStringAsync(PageUri, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_following_page_is_not_rebuilt_on_every_read_while_an_agent_works()
    {
        using var console = Asked();
        using var client = console.CreateClient();

        // Compared twice, because a minute can turn between two reads and move the digest honestly.
        for (var attempt = 0; ; attempt++)
        {
            var first = SeenDigest().Match(await client.GetStringAsync(FollowUri, Ct)).Groups[1].Value;
            var second = SeenDigest().Match(await client.GetStringAsync(FollowUri, Ct)).Groups[1].Value;

            if (first == second)
            {
                return;
            }

            Assert.True(attempt < 1, "the digest moved between reads moments apart");
        }
    }

    [Fact]
    public async Task A_message_is_formatted_on_the_page_and_cannot_carry_markup()
    {
        using var console = new TranscriptConsole();

        console.Broker.Request(
            1,
            Orders,
            Person,
            "**Bold** <script>alert(1)</script> [x](javascript:alert(2)) ![p](https://example.com/p.png)");

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(PageUri, Ct);

        Assert.Contains("<strong>Bold</strong>", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert(1)", page, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)", page, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:alert(2)\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<img src=\"https://example.com/p.png", page, StringComparison.Ordinal);
    }
}
