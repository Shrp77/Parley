using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Parley.Client;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>The Inbox's server-side guards on its digest, its rows and its follow-up, each held through the page.</summary>
/// <remarks>
/// Found by review, lens 3 round 37: each guard below could be removed with every Inbox test still passing. Two of them
/// differ only across projects, so those use a console with two projects, each with its own scripted broker.
/// </remarks>
[Integration]
public sealed partial class TheInboxKeepsItsServerGuardsTests
{
    private const string Orders = "orders@test";
    private const string Billing = "billing@test";
    private const string Inventory = "inventory@test";

    private static readonly Uri DigestUri = new("/Inbox?handler=Digest", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("""name="__RequestVerificationToken"[^>]*value="([^"]+)""")]
    private static partial Regex Token();

    private static string PersonOf(string project) => "human-user@" + project;

    /// <summary>Seats the person in a broker, with one conversation the Inbox reads.</summary>
    private static void Seat(RoadmapBroker broker, string project, string conversation = "talk")
    {
        broker.Seats = true;
        broker.Registry.Add(new Wire.RegistryRow
        {
            Name = PersonOf(project),
            Adapter = "parley-console",
            Tier = Wire.AgentTier.Full,
            Status = Wire.AgentStatus.Active,
        });
        broker.Conversations.Add(new Wire.ConversationSummary
        {
            ConversationId = conversation,
            HasOpenExchange = true,
            LastActivityUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        });
    }

    /// <summary>A closed exchange: the agent asked, the person answered with these words, some minutes ago.</summary>
    private static void Closed(
        RoadmapBroker broker, string project, long exchange, string agent, string answer, int minutesAgo, string conversation = "talk")
    {
        Said(broker, exchange * 10, exchange, Wire.MessageType.Request, agent, PersonOf(project), "{\"ask\":\"A question\"}", minutesAgo + 1, conversation);
        Said(
            broker, (exchange * 10) + 1, exchange, Wire.MessageType.Result, PersonOf(project), agent,
            "{\"outcomes\":[{\"id\":\"request\",\"outcome\":\"answered\",\"text\":" + JsonSerializer.Serialize(answer) + "}]}",
            minutesAgo, conversation);
        broker.ExportExchanges.Add(new Wire.ExchangeSnapshot
        {
            ExchangeId = exchange,
            State = Wire.ExchangeState.Closed,
            OpenedUtc = DateTimeOffset.UtcNow.AddHours(-3).ToString("O", CultureInfo.InvariantCulture),
            ClosedUtc = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo).ToString("O", CultureInfo.InvariantCulture),
            Outcome = Wire.ExchangeOutcome.Complete,
        });
    }

    private static void Said(
        RoadmapBroker broker, long id, long exchange, Wire.MessageType type, string from, string to, string json, int minutesAgo, string conversation)
    {
        broker.ExportMessages.Add(new Wire.Message
        {
            Id = id,
            ExchangeId = exchange,
            Type = type,
            From = from,
            To = to,
            ResponderModel = "test",
            PayloadJson = json,
            ConversationId = conversation,
            CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo).ToString("O", CultureInfo.InvariantCulture),
        });
    }

    private static RoadmapConsole OneProject()
    {
        var console = new RoadmapConsole();

        Seat(console.Broker, RoadmapConsole.Project);

        return console;
    }

    private static async Task<string> DigestAsync(HttpClient client) =>
        JsonDocument.Parse(await client.GetStringAsync(DigestUri, Ct)).RootElement.GetProperty("digest").GetString()!;

    [Fact]
    public async Task Exchanges_between_other_agents_moving_do_not_change_the_digest()
    {
        // Without the filter every exchange in the project was in the digest, so two agents talking to each other reloaded
        // the person's Inbox every few seconds, and a reload discards nothing typed only because the page waits for quiet
        // (review, lens 3 round 37).
        using var console = OneProject();

        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Orders, WaitedOn = PersonOf(RoadmapConsole.Project), ExchangeId = 1 });

        using var client = console.CreateClient();

        var before = await DigestAsync(client);

        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Orders, WaitedOn = Billing, ExchangeId = 2 });
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Billing, WaitedOn = Inventory, ExchangeId = 3 });

        Assert.Equal(before, await DigestAsync(client));
    }

    [Fact]
    public async Task A_turn_starting_or_ending_for_an_agent_the_person_waits_on_changes_the_digest()
    {
        // Without the standing part the chip beside a row (working, waiting) went stale: the digest held only the graph,
        // so a turn starting or ending never reloaded the page (review, lens 3 round 37).
        using var console = OneProject();

        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = PersonOf(RoadmapConsole.Project), WaitedOn = Orders, ExchangeId = 1 });

        var orders = new Wire.RegistryRow
        {
            Name = Orders,
            Adapter = "claude-code",
            Tier = Wire.AgentTier.Headless,
            Status = Wire.AgentStatus.Active,
            Presence = Wire.AgentPresence.Running,
            TurnsReported = true,
        };

        console.Broker.Registry.Add(orders);

        using var client = console.CreateClient();

        var idle = await DigestAsync(client);

        orders.TurnRunningSinceUtc = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O", CultureInfo.InvariantCulture);
        var working = await DigestAsync(client);

        Assert.NotEqual(idle, working);

        orders.TurnRunningSinceUtc = string.Empty;
        Assert.NotEqual(working, await DigestAsync(client));
    }

    [Fact]
    public async Task Search_finds_a_row_by_its_conversation()
    {
        // Without the conversation in the match, the filter's own hint ("who, conversation or words") was untrue for In
        // flight and Closed: a row was found by who and words only (review, lens 3 round 37).
        using var console = new RoadmapConsole();

        Seat(console.Broker, RoadmapConsole.Project, "ledger-rework");
        Closed(console.Broker, RoadmapConsole.Project, 20, Billing, "Use the yearly plan.", 30, "ledger-rework");

        using var client = console.CreateClient();

        var found = await client.GetStringAsync(new Uri("/Inbox?view=closed&q=rework", UriKind.Relative), Ct);
        var missed = await client.GetStringAsync(new Uri("/Inbox?view=closed&q=nowhere", UriKind.Relative), Ct);

        Assert.Contains("Use the yearly plan.", found, StringComparison.Ordinal);
        Assert.DoesNotContain("Use the yearly plan.", missed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_latest_closed_window_offers_no_newer_one()
    {
        // Without the check the latest window offered "Show newer", which led back to itself (review, lens 3 round 37).
        using var console = OneProject();
        using var client = console.CreateClient();

        var latest = await client.GetStringAsync(new Uri("/Inbox?view=closed", UriKind.Relative), Ct);
        var older = await client.GetStringAsync(new Uri("/Inbox?view=closed&older=1", UriKind.Relative), Ct);

        Assert.Contains("Show older", latest, StringComparison.Ordinal);
        Assert.DoesNotContain("Show newer", latest, StringComparison.Ordinal);
        Assert.Contains("Show newer", older, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rows_from_every_project_are_drawn_newest_first()
    {
        // Without the sort across projects, rows were drawn a project at a time, so an answer from a minute ago in beta sat
        // below everything older in alpha (review, lens 3 round 37).
        using var console = new TwoProjects();

        Closed(console.Alpha, "alpha", 20, Billing, "Alpha answered an hour ago.", 60);
        Closed(console.Beta, "beta", 21, Billing, "Beta answered a minute ago.", 1);

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/Inbox?view=closed", UriKind.Relative), Ct);
        var alpha = page.IndexOf("Alpha answered an hour ago.", StringComparison.Ordinal);
        var beta = page.IndexOf("Beta answered a minute ago.", StringComparison.Ordinal);

        Assert.True(alpha >= 0 && beta >= 0, "Both rows are drawn.");
        Assert.True(beta < alpha, "The newer row, in beta, comes first.");
    }

    [Fact]
    public async Task A_refused_follow_up_goes_back_only_into_its_own_projects_row()
    {
        // Exchange numbers are unique only within a project. Without the project in the match, a refused follow-up's words
        // went back into every row with that exchange number, in each project, and opened them all (review, lens 3 round 37).
        using var console = new TwoProjects();

        Closed(console.Alpha, "alpha", 20, Billing, "Alpha's answer.", 30);
        Closed(console.Beta, "beta", 20, Billing, "Beta's answer.", 30);
        console.Alpha.RefusesRequestsTo.Add(Billing);

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/Inbox?view=closed", UriKind.Relative), Ct);

        using var form = new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", Token().Match(page).Groups[1].Value),
            new("For", "alpha"),
            new("Exchange", "20"),
            new("Agent", Billing),
            new("Conversation", "talk"),
            new("Words", "Words for alpha alone"),
        ]);

        using var response = await client.PostAsync(new Uri("/Inbox?handler=FollowUp&view=closed", UriKind.Relative), form, Ct);
        var after = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Single(console.Alpha.Requested);
        Assert.Equal(1, Regex.Count(after, "Words for alpha alone", RegexOptions.None, TimeSpan.FromSeconds(2)));
        Assert.Equal(1, Regex.Count(after, "<details class=\"dgrid-row\" role=\"row\" open=\"open\"", RegexOptions.None, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task A_project_filter_keeps_only_that_projects_rows_and_counts()
    {
        // Without the project in Keep, choosing a project still listed every project's In flight and Closed rows, and the
        // tab counts said so too (review, lens 3 round 38).
        using var console = new TwoProjects();

        Closed(console.Alpha, "alpha", 20, Billing, "Alpha's answer.", 30);
        Closed(console.Beta, "beta", 21, Billing, "Beta's answer.", 30);
        InFlight(console.Alpha, "alpha", 30, Orders, "Alpha's open request.");
        InFlight(console.Beta, "beta", 31, Orders, "Beta's open request.");

        using var client = console.CreateClient();

        var everyClosed = await client.GetStringAsync(new Uri("/Inbox?view=closed", UriKind.Relative), Ct);
        var closed = await client.GetStringAsync(new Uri("/Inbox?view=closed&scope=alpha", UriKind.Relative), Ct);
        var flight = await client.GetStringAsync(new Uri("/Inbox?view=flight&scope=alpha", UriKind.Relative), Ct);

        // Unfiltered, both projects' rows are there.
        Assert.Contains("Beta's answer.", everyClosed, StringComparison.Ordinal);
        Assert.Contains("In flight <span class=\"count\">2</span>", everyClosed, StringComparison.Ordinal);
        Assert.Contains("Closed <span class=\"count\">2</span>", everyClosed, StringComparison.Ordinal);

        Assert.Contains("Alpha's answer.", closed, StringComparison.Ordinal);
        Assert.DoesNotContain("Beta's answer.", closed, StringComparison.Ordinal);
        Assert.Contains("Alpha's open request.", flight, StringComparison.Ordinal);
        Assert.DoesNotContain("Beta's open request.", flight, StringComparison.Ordinal);

        foreach (var page in new[] { closed, flight })
        {
            Assert.Contains("In flight <span class=\"count\">1</span>", page, StringComparison.Ordinal);
            Assert.Contains("Closed <span class=\"count\">1</span>", page, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Result")]
    [InlineData("Clarify")]
    public async Task A_refused_answer_goes_back_only_into_its_own_projects_row(string handler)
    {
        // Exchange numbers are unique only within a project. Without the project in the match, a Result or a Clarify
        // refused in alpha filled the row of the same number in beta with alpha's words, verdict and attached file names,
        // and opened it: one press from being sent to a different agent about a different question.
        using var console = new TwoProjects();

        Asked(console.Alpha, "alpha", 20, Orders, "What alpha was asked.");
        Asked(console.Beta, "beta", 20, Billing, "What beta was asked.");
        console.Alpha.FailsOn = "/parley.v1.Agents/" + handler;

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/Inbox", UriKind.Relative), Ct);

        using var form = new MultipartFormDataContent
        {
            { new StringContent(Token().Match(page).Groups[1].Value), "__RequestVerificationToken" },
            { new StringContent("alpha"), "For" },
            { new StringContent("20"), "Exchange" },
            { new StringContent("talk"), "Conversation" },
            { new StringContent("request"), "Ids" },
            { new StringContent("Words for alpha alone"), "Texts" },
            { new StringContent("blocked:conflict"), "Verdicts" },
            { new StringContent("A question for alpha alone"), "Clarification" },
            { new StringContent("alphas-screen.png"), "Attached" },
        };

        using var response = await client.PostAsync(new Uri("/Inbox?handler=" + handler, UriKind.Relative), form, Ct);
        var after = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(console.Alpha.Resulted);
        Assert.Empty(console.Beta.Resulted);

        var rows = after.Split("<details class=\"dgrid-row\" role=\"row\"");
        var alpha = Assert.Single(rows, row => row.Contains("What alpha was asked.", StringComparison.Ordinal));
        var beta = Assert.Single(rows, row => row.Contains("What beta was asked.", StringComparison.Ordinal));
        var words = handler == "Result" ? "Words for alpha alone" : "A question for alpha alone";

        // Alpha's row comes back open with everything the person had put in it.
        Assert.StartsWith(" open=\"open\"", alpha, StringComparison.Ordinal);
        Assert.Contains(words, alpha, StringComparison.Ordinal);
        Assert.Contains("alphas-screen.png", alpha, StringComparison.Ordinal);

        // Beta's row has the same exchange number and none of it.
        Assert.False(beta.StartsWith(" open=", StringComparison.Ordinal), "the row of the same number in another project was opened");
        Assert.DoesNotContain("for alpha alone", beta, StringComparison.Ordinal);
        Assert.DoesNotContain("alphas-screen.png", beta, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"blocked:conflict\" selected", beta, StringComparison.Ordinal);

        if (handler == "Result")
        {
            Assert.Contains("value=\"blocked:conflict\" selected", alpha, StringComparison.Ordinal);
        }
    }

    /// <summary>An open exchange an agent asked the person, which the person has not answered yet.</summary>
    private static void Asked(RoadmapBroker broker, string project, long exchange, string agent, string ask)
    {
        Said(
            broker, exchange * 10, exchange, Wire.MessageType.Request, agent, PersonOf(project),
            "{\"ask\":" + JsonSerializer.Serialize(ask) + "}", 5, "talk");
        broker.ExportExchanges.Add(new Wire.ExchangeSnapshot
        {
            ExchangeId = exchange,
            State = Wire.ExchangeState.Open,
            OpenedUtc = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O", CultureInfo.InvariantCulture),
        });
        broker.Edges.Add(new Wire.WaitingEdge { Waiter = agent, WaitedOn = PersonOf(project), ExchangeId = exchange });
    }

    /// <summary>An open exchange the person asked an agent, which the agent has not answered yet.</summary>
    private static void InFlight(RoadmapBroker broker, string project, long exchange, string agent, string ask)
    {
        Said(
            broker, exchange * 10, exchange, Wire.MessageType.Request, PersonOf(project), agent,
            "{\"ask\":" + JsonSerializer.Serialize(ask) + "}", 5, "talk");
        broker.ExportExchanges.Add(new Wire.ExchangeSnapshot
        {
            ExchangeId = exchange,
            State = Wire.ExchangeState.Open,
            OpenedUtc = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O", CultureInfo.InvariantCulture),
        });
        broker.Edges.Add(new Wire.WaitingEdge { Waiter = PersonOf(project), WaitedOn = agent, ExchangeId = exchange });
    }

    /// <summary>A console with two projects, alpha and beta, each answered by its own scripted broker.</summary>
    private sealed class TwoProjects : WebApplicationFactory<Program>
    {
        private const string AlphaAddress = "http://127.0.0.1:5407";
        private const string BetaAddress = "http://127.0.0.1:5408";

        public TwoProjects()
        {
            Seat(Alpha, "alpha");
            Seat(Beta, "beta");
        }

        /// <summary>Alpha's broker.</summary>
        public RoadmapBroker Alpha { get; } = new();

        /// <summary>Beta's broker.</summary>
        public RoadmapBroker Beta { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<BrokerDirectory>();

                GrpcChannel Connect(string address) => GrpcChannel.ForAddress(address, new GrpcChannelOptions
                {
                    HttpHandler = address.StartsWith(BetaAddress, StringComparison.Ordinal) ? Beta : Alpha,
                    DisposeHttpClient = false,
                });

                services.AddSingleton(_ => new BrokerDirectory(
                    [
                        new ProjectSettings(ProjectName.Create("alpha"), "alpha.db", AlphaAddress, null),
                        new ProjectSettings(ProjectName.Create("beta"), "beta.db", BetaAddress, null),
                    ],
                    Connect));
            });
        }
    }
}
