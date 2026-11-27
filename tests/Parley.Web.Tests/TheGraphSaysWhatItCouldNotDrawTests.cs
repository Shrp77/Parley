using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>What the graph panel says about the read behind it: a narrow panel's one column, a reply waiting on an agent's own
/// question, and every part of the read it bounded or could not make.</summary>
/// <remarks>
/// <para>
/// Feature 042. The picture is read for each refresh of every open panel, so what an agent owes is read only so far, and
/// whatever was left out is said, never dropped in silence. Each test here drives the page over a scripted broker and reads
/// the fragment as drawn.
/// </para>
/// </remarks>
[Integration]
public sealed class TheGraphSaysWhatItCouldNotDrawTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Utc(DateTimeOffset at) => at.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Billing at work, owing <paramref name="exchanges"/> requests from orders, each in the conversation
    /// <paramref name="conversation"/> names for it.</summary>
    private static void Owing(ScriptedExport broker, int exchanges, Func<int, string> conversation)
    {
        var now = DateTimeOffset.UtcNow;

        broker.Registry.Add(new Wire.RegistryRow
        {
            Name = "billing@pc", Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active,
            Presence = Wire.AgentPresence.Running, Connected = true,
            TurnsReported = true, TurnRunningSinceUtc = Utc(now.AddMinutes(-4)),
        });

        for (var n = 1; n <= exchanges; n++)
        {
            broker.Messages.Add(new Wire.Message
            {
                Id = n, ExchangeId = n, Type = Wire.MessageType.Request, From = "orders@pc", To = "billing@pc",
                PayloadJson = """{"ask":"Price the order"}""", ConversationId = conversation(n),
                CreatedUtc = Utc(now.AddMinutes(-60).AddSeconds(n)),
            });
            broker.Edges.Add(new Wire.WaitingEdge { Waiter = "orders@pc", WaitedOn = "billing@pc", ExchangeId = n });
        }
    }

    private static async Task<string> GraphAsync(TranscriptConsole console, string query = "")
    {
        using var client = console.CreateClient();

        return await client.GetStringAsync(new Uri("/Graph" + query, UriKind.Relative), Ct);
    }

    [Fact]
    public async Task A_narrow_panel_is_drawn_in_one_column()
    {
        using var console = new TranscriptConsole();
        Owing(console.Broker, 1, _ => "returns");

        var wide = await GraphAsync(console);
        var narrow = await GraphAsync(console, "?narrow=true");

        Assert.Contains($"data-w=\"{Parley.Client.Views.WorkNow.Wide}\"", wide, StringComparison.Ordinal);
        Assert.Contains($"data-w=\"{Parley.Client.Views.WorkNow.Narrow}\"", narrow, StringComparison.Ordinal);
        Assert.Contains($"viewBox=\"0 0 {Parley.Client.Views.WorkNow.Narrow} ", narrow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_to_the_agents_own_question_that_it_has_not_collected_is_drawn_as_a_reply_waiting()
    {
        using var console = new TranscriptConsole();
        var now = DateTimeOffset.UtcNow;
        Owing(console.Broker, 0, _ => "");

        // Billing collected the Request and asked a question on it; the answer to its question waits, not collected.
        console.Broker.Messages.Add(new Wire.Message
        {
            Id = 1, ExchangeId = 7, Type = Wire.MessageType.Request, From = "orders@pc", To = "billing@pc",
            PayloadJson = """{"ask":"Refund the returned order"}""", ConversationId = "returns",
            CreatedUtc = Utc(now.AddMinutes(-10)), DeliveredUtc = Utc(now.AddMinutes(-9)),
        });
        console.Broker.Messages.Add(new Wire.Message
        {
            Id = 2, ExchangeId = 7, Type = Wire.MessageType.Clarify, From = "billing@pc", To = "orders@pc",
            PayloadJson = """{"question":"Which order?"}""", ConversationId = "returns",
            CreatedUtc = Utc(now.AddMinutes(-8)), DeliveredUtc = Utc(now.AddMinutes(-7)),
        });
        console.Broker.Messages.Add(new Wire.Message
        {
            Id = 3, ExchangeId = 7, Type = Wire.MessageType.Clarify, From = "orders@pc", To = "billing@pc",
            PayloadJson = """{"answer":"Order 12"}""", ConversationId = "returns",
            CreatedUtc = Utc(now.AddMinutes(-2)),
        });
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = "orders@pc", WaitedOn = "billing@pc", ExchangeId = 7 });

        var fragment = await GraphAsync(console);

        Assert.Contains("REPLY WAITS 1 · alpha", fragment, StringComparison.Ordinal);
        Assert.Contains("Queued 1, a reply waits, not collected yet", fragment, StringComparison.Ordinal);
        Assert.Contains("Refund the returned order", fragment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Past_the_most_exchanges_read_the_panel_says_how_many_were_left_out()
    {
        using var console = new TranscriptConsole();
        Owing(console.Broker, 101, _ => "returns");

        var fragment = await GraphAsync(console);

        Assert.Contains("Only the oldest 100 of 101 exchanges its working agents owe are shown.", fragment, StringComparison.Ordinal);
        Assert.Contains("could not be read in full.", fragment, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(20, 20, null, null)]
    [InlineData(21, 20, "1 more it holds, not listed.", null)]
    [InlineData(20, 21, null, "1 more queued, not listed.")]
    public async Task The_list_in_words_counts_what_it_held_back_only_past_its_bound(
        int held, int queued, string? moreHeld, string? moreQueued)
    {
        using var console = new TranscriptConsole();
        var now = DateTimeOffset.UtcNow;

        // Billing owes `queued` requests it has not collected, and holds `held` it collected and has not answered.
        Owing(console.Broker, queued, _ => "returns");

        for (var n = queued + 1; n <= queued + held; n++)
        {
            console.Broker.Messages.Add(new Wire.Message
            {
                Id = n, ExchangeId = n, Type = Wire.MessageType.Request, From = "orders@pc", To = "billing@pc",
                PayloadJson = """{"ask":"Price the order"}""", ConversationId = "gift-cards",
                CreatedUtc = Utc(now.AddMinutes(-30).AddSeconds(n)), DeliveredUtc = Utc(now.AddMinutes(-29).AddSeconds(n)),
            });
            console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = "orders@pc", WaitedOn = "billing@pc", ExchangeId = n });
        }

        var fragment = await GraphAsync(console);
        var list = fragment[fragment.IndexOf("<section class=\"pg-list\"", StringComparison.Ordinal)..];

        // Every item it does list is listed, so the bound is the one under test.
        Assert.Equal(20, System.Text.RegularExpressions.Regex.Count(list, "<li>Holds: "));
        Assert.Equal(20, System.Text.RegularExpressions.Regex.Count(list, "<li>Queued \\d+, "));

        if (moreHeld is null)
        {
            Assert.DoesNotContain("more it holds, not listed", list, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(moreHeld, list, StringComparison.Ordinal);
        }

        if (moreQueued is null)
        {
            Assert.DoesNotContain("more queued, not listed", list, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(moreQueued, list, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Past_the_most_conversations_read_the_panel_says_so()
    {
        using var console = new TranscriptConsole();
        Owing(console.Broker, 31, n => $"c{n}");

        var fragment = await GraphAsync(console);

        Assert.Contains("Only the first 30 conversations its working agents owe work in are shown.", fragment, StringComparison.Ordinal);
        Assert.Equal(30, console.Broker.Exports);
    }

    [Theory]
    [InlineData("/LocateExchange")]
    [InlineData("/Export")]
    [InlineData("/DescribeWaiting")]
    public async Task An_exchange_whose_conversation_could_not_be_read_is_said_not_dropped(string failing)
    {
        using var console = new TranscriptConsole();
        Owing(console.Broker, 1, _ => "returns");
        console.Broker.FailsOn = failing;

        var fragment = await GraphAsync(console);

        // Billing is still drawn at work, and the panel says its project was not read in full, and why.
        Assert.Contains(">billing@pc</text>", fragment, StringComparison.Ordinal);
        Assert.Contains("<p class=\"pg-note\" role=\"status\"><strong>alpha</strong> project, could not be read in full. This project", fragment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_nobody_hidden_and_one_project_the_agents_words_count_neither()
    {
        using var console = new TranscriptConsole();
        Owing(console.Broker, 1, _ => "returns");

        var fragment = await GraphAsync(console);

        Assert.Contains(">1 agent working</text>", fragment, StringComparison.Ordinal);
        Assert.DoesNotContain("0 hidden", fragment, StringComparison.Ordinal);
        Assert.DoesNotContain("1 projects", fragment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_box_whose_ask_could_not_be_read_says_so_held_or_queued()
    {
        using var console = new TranscriptConsole();
        var now = DateTimeOffset.UtcNow;
        Owing(console.Broker, 0, _ => "");

        // One collected and one not, neither with an ask that can be read.
        console.Broker.Messages.Add(new Wire.Message
        {
            Id = 1, ExchangeId = 1, Type = Wire.MessageType.Request, From = "orders@pc", To = "billing@pc",
            PayloadJson = "not json", ConversationId = "returns",
            CreatedUtc = Utc(now.AddMinutes(-10)), DeliveredUtc = Utc(now.AddMinutes(-9)),
        });
        console.Broker.Messages.Add(new Wire.Message
        {
            Id = 2, ExchangeId = 2, Type = Wire.MessageType.Request, From = "orders@pc", To = "billing@pc",
            PayloadJson = "not json", ConversationId = "returns",
            CreatedUtc = Utc(now.AddMinutes(-8)),
        });
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = "orders@pc", WaitedOn = "billing@pc", ExchangeId = 1 });
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = "orders@pc", WaitedOn = "billing@pc", ExchangeId = 2 });

        var fragment = await GraphAsync(console);

        Assert.Matches("""class="pg-node pg-now">(?:(?!</g>).)*class="pg-sub">What was asked could not be read</text>""", fragment.ReplaceLineEndings(" "));
        Assert.Matches("""class="pg-node pg-queued">(?:(?!</g>).)*class="pg-sub">What was asked could not be read</text>""", fragment.ReplaceLineEndings(" "));
    }

    [Fact]
    public async Task One_held_item_is_listed_and_not_also_said_to_be_nothing()
    {
        using var console = new TranscriptConsole();
        var now = DateTimeOffset.UtcNow;
        Owing(console.Broker, 0, _ => "");

        console.Broker.Messages.Add(new Wire.Message
        {
            Id = 1, ExchangeId = 1, Type = Wire.MessageType.Request, From = "orders@pc", To = "billing@pc",
            PayloadJson = """{"ask":"Price the order"}""", ConversationId = "returns",
            CreatedUtc = Utc(now.AddMinutes(-10)), DeliveredUtc = Utc(now.AddMinutes(-9)),
        });
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = "orders@pc", WaitedOn = "billing@pc", ExchangeId = 1 });

        var fragment = await GraphAsync(console);
        var list = fragment[fragment.IndexOf("<section class=\"pg-list\"", StringComparison.Ordinal)..];

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(list, "<li>Holds: "));
        Assert.DoesNotContain("Holds nothing it has not answered.", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_background_task_is_one_task_in_the_list()
    {
        using var console = new TranscriptConsole();
        Owing(console.Broker, 0, _ => "");
        console.Broker.Registry[0].BackgroundTasks = 1;

        var fragment = await GraphAsync(console);

        Assert.Contains("<li>1 background task running:</li>", fragment, StringComparison.Ordinal);
    }
}
