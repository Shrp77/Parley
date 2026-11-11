using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>The Inbox's In flight and Closed views, its follow-up, and references that open an exchange (spec 036).</summary>
/// <remarks>
/// <para>
/// <b>The owner, 2026-11-06:</b> "if you answer a question, it's gone and you have no way of following up on it." Each
/// view is read from the broker's graph and each exchange's state, so these script the broker and read the page.
/// </para>
/// <para>
/// And: a numbered exchange is written "exchange #N" or "exchanges #N, #M and #K", and opens where it is mentioned.
/// </para>
/// </remarks>
[Integration]
public sealed partial class InboxFollowsExchangesTests
{
    private const string Orders = "orders@test";
    private const string Billing = "billing@test";
    private const string Person = "human-user@" + RoadmapConsole.Project;
    private const string Facilitator = "parley-facilitator@" + RoadmapConsole.Project;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("""name="__RequestVerificationToken"[^>]*value="([^"]+)""")]
    private static partial Regex Token();

    private static RoadmapConsole Console()
    {
        var console = new RoadmapConsole();

        console.Broker.Seats = true;
        console.Broker.Registry.Add(new Wire.RegistryRow
        {
            Name = Person,
            Adapter = "parley-console",
            Tier = Wire.AgentTier.Full,
            Status = Wire.AgentStatus.Active,
        });
        console.Broker.Conversations.Add(new Wire.ConversationSummary
        {
            ConversationId = "talk",
            HasOpenExchange = true,
            LastActivityUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        });

        return console;
    }

    private static void Said(RoadmapConsole console, long id, long exchange, Wire.MessageType type, string from, string to, string json)
    {
        console.Broker.ExportMessages.Add(new Wire.Message
        {
            Id = id,
            ExchangeId = exchange,
            Type = type,
            From = from,
            To = to,
            ResponderModel = "test",
            PayloadJson = json,
            ConversationId = "talk",
            CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-id).ToString("O", CultureInfo.InvariantCulture),
        });
    }

    private static void Exchange(RoadmapConsole console, long id, bool open, Wire.ExchangeOutcome outcome = Wire.ExchangeOutcome.Unspecified)
    {
        console.Broker.ExportExchanges.Add(new Wire.ExchangeSnapshot
        {
            ExchangeId = id,
            State = open ? Wire.ExchangeState.Open : Wire.ExchangeState.Closed,
            OpenedUtc = DateTimeOffset.UtcNow.AddHours(-2).ToString("O", CultureInfo.InvariantCulture),
            ClosedUtc = open ? string.Empty : DateTimeOffset.UtcNow.AddHours(-1).ToString("O", CultureInfo.InvariantCulture),
            Outcome = outcome,
        });
    }

    private static string Ask(string text) => "{\"ask\":" + JsonSerializer.Serialize(text) + "}";

    private static async Task<string> GetAsync(RoadmapConsole console, string url)
    {
        using var client = console.CreateClient();

        return await client.GetStringAsync(new Uri(url, UriKind.Relative), Ct);
    }

    [Fact]
    public async Task A_question_the_person_replied_to_is_in_flight_with_where_the_agent_stands()
    {
        using var console = Console();

        Said(console, 1, 10, Wire.MessageType.Request, Orders, Person, Ask("Which invoice schema?"));
        Said(console, 2, 10, Wire.MessageType.Clarify, Person, Orders, Ask("The new one."));
        Exchange(console, 10, open: true);
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Person, WaitedOn = Orders, ExchangeId = 10 });
        console.Broker.Registry.Add(new Wire.RegistryRow
        {
            Name = Orders,
            Adapter = "claude-code",
            Tier = Wire.AgentTier.Headless,
            Status = Wire.AgentStatus.Active,
            Presence = Wire.AgentPresence.Running,
            TurnsReported = true,
            TurnRunningSinceUtc = DateTimeOffset.UtcNow.AddMinutes(-3).ToString("O", CultureInfo.InvariantCulture),
        });

        var waiting = await GetAsync(console, "/Inbox");

        Assert.Contains("In flight <span class=\"count\">1</span>", waiting, StringComparison.Ordinal);
        Assert.DoesNotContain("Which invoice schema?", waiting, StringComparison.Ordinal);

        var flight = await GetAsync(console, "/Inbox?view=flight");

        Assert.Contains("aria-current=\"page\">In flight", flight, StringComparison.Ordinal);
        Assert.Contains("The new one.", flight, StringComparison.Ordinal);
        Assert.Contains("class=\"chip-state state-working\"", flight, StringComparison.Ordinal);
        Assert.Contains("/Conversations/Detail?project=alpha&amp;id=talk#m2", flight, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_closed_exchange_is_listed_with_how_it_ended()
    {
        using var console = Console();

        Said(console, 1, 20, Wire.MessageType.Request, Orders, Person, Ask("Turn on the nightly export."));
        Said(console, 2, 20, Wire.MessageType.Result, Person, Orders, """{"outcomes":[{"id":"request","outcome":"done","text":"Switched on."}]}""");
        Exchange(console, 20, open: false, Wire.ExchangeOutcome.Complete);

        var closed = await GetAsync(console, "/Inbox?view=closed");

        Assert.Contains("Closed in the last 7 days.", closed, StringComparison.Ordinal);
        Assert.Contains("<span class=\"chip-state\">Done</span>", closed, StringComparison.Ordinal);
        Assert.Contains("Switched on.", closed, StringComparison.Ordinal);
        Assert.Contains("Show older", closed, StringComparison.Ordinal);

        // The window before this one does not hold it.
        Assert.DoesNotContain("Switched on.", await GetAsync(console, "/Inbox?view=closed&older=1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_exchange_closed_this_week_in_a_conversation_quiet_for_longer_is_still_listed()
    {
        // A question asked ten days ago, settled two days ago by the person's Request elsewhere: its conversation's last
        // message is ten days old, and the Inbox read only conversations with a message in the window (review, lens 1
        // round 17).
        using var console = Console();

        console.Broker.Conversations[0].LastActivityUtc =
            DateTimeOffset.UtcNow.AddDays(-10).ToString("O", CultureInfo.InvariantCulture);
        console.Broker.Conversations[0].HasOpenExchange = false;
        console.Broker.Conversations[0].LastClosedUtc =
            DateTimeOffset.UtcNow.AddDays(-2).ToString("O", CultureInfo.InvariantCulture);

        Said(console, 14_400, 20, Wire.MessageType.Request, Orders, Person, Ask("Which rate for the nightly export?"));
        Exchange(console, 20, open: false, Wire.ExchangeOutcome.Settled);
        console.Broker.ExportExchanges[0].OpenedUtc = DateTimeOffset.UtcNow.AddDays(-10).ToString("O", CultureInfo.InvariantCulture);
        console.Broker.ExportExchanges[0].ClosedUtc = DateTimeOffset.UtcNow.AddDays(-2).ToString("O", CultureInfo.InvariantCulture);

        var closed = await GetAsync(console, "/Inbox?view=closed");

        Assert.Contains("Which rate for the nightly export?", closed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_still_in_flight_in_a_conversation_quiet_for_longer_is_still_listed()
    {
        // The person asked ten days ago and the agent has not answered: no message and no close in the window, only open
        // work keeps the conversation read for In flight (review, lens 3 round 17).
        using var console = Console();

        console.Broker.Conversations[0].LastActivityUtc =
            DateTimeOffset.UtcNow.AddDays(-10).ToString("O", CultureInfo.InvariantCulture);
        console.Broker.Conversations[0].HasOpenExchange = true;
        console.Broker.Conversations[0].LastClosedUtc = string.Empty;

        Said(console, 14_400, 20, Wire.MessageType.Request, Person, Billing, Ask("Which ledger should the export use?"));
        Exchange(console, 20, open: true);
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Person, WaitedOn = Billing, ExchangeId = 20 });

        Assert.Contains("Which ledger should the export use?", await GetAsync(console, "/Inbox?view=flight"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_follow_up_is_a_new_request_to_the_rows_agent_in_its_conversation()
    {
        using var console = Console();

        Said(console, 1, 20, Wire.MessageType.Request, Billing, Person, Ask("Monthly or yearly?"));
        Said(console, 2, 20, Wire.MessageType.Result, Person, Billing, """{"outcomes":[{"id":"request","outcome":"answered","text":"Yearly."}]}""");
        Exchange(console, 20, open: false, Wire.ExchangeOutcome.Complete);

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/Inbox?view=closed", UriKind.Relative), Ct);

        Assert.Contains("name=\"Words\"", page, StringComparison.Ordinal);

        using var form = new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", Token().Match(page).Groups[1].Value),
            new("For", RoadmapConsole.Project),
            new("Exchange", "20"),
            new("Agent", Billing),
            new("Conversation", "talk"),
            new("Words", "Did the yearly plan go through?"),
        ]);

        // Where the form itself posts, so a view the form leaves out cannot be supplied by the test.
        using var response = await client.PostAsync(FollowUpAction(page), form, Ct);

        var sent = Assert.Single(console.Broker.Requested);

        Assert.Equal(Billing, sent.To);
        Assert.Equal("talk", sent.ConversationId);
        Assert.Contains("Did the yearly plan go through?", sent.PayloadJson, StringComparison.Ordinal);

        // Nothing was said on the closed exchange itself.
        Assert.Empty(console.Broker.Resulted);
    }

    [Fact]
    public async Task A_follow_up_sent_after_the_seats_went_idle_is_still_sent()
    {
        // The book lets every seat go after twenty idle minutes, and the frame takes them back only when it draws, after
        // the handler. A follow-up written over that long found no seat and was dropped without a word.
        using var console = Console();

        Said(console, 1, 20, Wire.MessageType.Request, Billing, Person, Ask("Monthly or yearly?"));
        Said(console, 2, 20, Wire.MessageType.Result, Person, Billing, """{"outcomes":[{"id":"request","outcome":"answered","text":"Yearly."}]}""");
        Exchange(console, 20, open: false, Wire.ExchangeOutcome.Complete);

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/Inbox?view=closed", UriKind.Relative), Ct);
        var book = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Parley.Client.Seating.SeatBook>(console.Services);

        Assert.True(book.Sweep(DateTimeOffset.UtcNow + Parley.Client.Seating.SeatBook.IdleAfter + TimeSpan.FromMinutes(1)) > 0);
        Assert.Empty(book.Seats());

        using var form = new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", Token().Match(page).Groups[1].Value),
            new("For", RoadmapConsole.Project),
            new("Exchange", "20"),
            new("Agent", Billing),
            new("Conversation", "talk"),
            new("Words", "Did the yearly plan go through?"),
        ]);

        using var response = await client.PostAsync(FollowUpAction(page), form, Ct);

        Assert.Contains("Did the yearly plan go through?", Assert.Single(console.Broker.Requested).PayloadJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/Attach")]
    [InlineData("/DescribeRegistry")]
    public async Task A_follow_up_with_no_seat_to_be_had_keeps_its_words_and_says_nothing_was_sent(string failing)
    {
        // A broker that cannot seat the person: the post redirected, the words were gone and nothing said so.
        using var console = Console();

        Said(console, 1, 20, Wire.MessageType.Request, Billing, Person, Ask("Monthly or yearly?"));
        Said(console, 2, 20, Wire.MessageType.Result, Person, Billing, """{"outcomes":[{"id":"request","outcome":"answered","text":"Yearly."}]}""");
        Exchange(console, 20, open: false, Wire.ExchangeOutcome.Complete);

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/Inbox?view=closed", UriKind.Relative), Ct);
        var book = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Parley.Client.Seating.SeatBook>(console.Services);

        book.Sweep(DateTimeOffset.UtcNow + Parley.Client.Seating.SeatBook.IdleAfter + TimeSpan.FromMinutes(1));
        console.Broker.FailsOn = failing;

        using var form = new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", Token().Match(page).Groups[1].Value),
            new("For", RoadmapConsole.Project),
            new("Exchange", "20"),
            new("Agent", Billing),
            new("Conversation", "talk"),
            new("Words", "Did the yearly plan go through?"),
        ]);

        using var response = await client.PostAsync(FollowUpAction(page), form, Ct);
        var after = await response.Content.ReadAsStringAsync(Ct);

        Assert.Empty(console.Broker.Requested);
        Assert.Contains("Nothing was sent.", after, StringComparison.Ordinal);
        Assert.Contains("Did the yearly plan go through?", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_follow_up_comes_back_to_its_row_with_the_words_still_in_the_box()
    {
        // The box is only on In flight and Closed. A post that lost the view came back to Waiting on you, where the row
        // and its box are not drawn, and the words were gone.
        using var console = Console();

        Said(console, 1, 21, Wire.MessageType.Request, Billing, Person, Ask("Monthly or yearly?"));
        Said(console, 2, 21, Wire.MessageType.Result, Person, Billing, """{"outcomes":[{"id":"request","outcome":"answered","text":"Yearly."}]}""");
        Exchange(console, 21, open: false, Wire.ExchangeOutcome.Complete);
        console.Broker.RefusesRequestsTo.Add(Billing);

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/Inbox?view=closed", UriKind.Relative), Ct);

        using var form = new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", Token().Match(page).Groups[1].Value),
            new("For", RoadmapConsole.Project),
            new("Exchange", "21"),
            new("Agent", Billing),
            new("Conversation", "talk"),
            new("Words", "Words worth keeping"),
        ]);

        using var response = await client.PostAsync(FollowUpAction(page), form, Ct);
        var after = await response.Content.ReadAsStringAsync(Ct);

        Assert.Contains("Words worth keeping", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_old_window_far_beyond_any_record_is_an_empty_page_not_a_failure()
    {
        using var console = Console();
        using var client = console.CreateClient();

        using var response = await client.GetAsync(new Uri("/Inbox?view=closed&older=2000000000", UriKind.Relative), Ct);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/Inbox?view=flight")]
    [InlineData("/Inbox?view=closed")]
    public async Task In_flight_and_closed_read_what_waits_and_take_none_of_it(string address)
    {
        // Showing a message is delivering it, and only Waiting on you shows what waits (SPEC.md §5.6, owner 2026-11-09).
        // The count is read, so the seat is held and the view had mail it could have taken.
        using var console = Console();

        console.Broker.MessagesWaiting = 2;

        using var client = console.CreateClient();

        await client.GetStringAsync(new Uri(address, UriKind.Relative), Ct);

        Assert.Contains("/parley.v1.Agents/Waiting", console.Broker.Paths);
        Assert.DoesNotContain("/parley.v1.Agents/DrainInbox", console.Broker.Paths);
    }

    /// <summary>The address a page's follow-up form posts to, decoded.</summary>
    /// <param name="page">The page.</param>
    /// <returns>The form's action.</returns>
    private static Uri FollowUpAction(string page) => new(
        System.Net.WebUtility.HtmlDecode(Regex.Match(page, @"action=""(/Inbox\?[^""]*handler=FollowUp[^""]*)""", RegexOptions.None, TimeSpan.FromSeconds(2)).Groups[1].Value),
        UriKind.Relative);

    [Fact]
    public async Task No_follow_up_is_offered_to_the_facilitator_and_one_asked_for_is_refused()
    {
        using var console = Console();

        Said(console, 1, 30, Wire.MessageType.Request, Facilitator, Person, Ask("Is this conversation done?"));
        Said(console, 2, 30, Wire.MessageType.Result, Person, Facilitator, """{"outcomes":[{"id":"request","outcome":"answered","text":"Yes."}]}""");
        Exchange(console, 30, open: false, Wire.ExchangeOutcome.Complete);

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/Inbox?view=closed", UriKind.Relative), Ct);

        Assert.Contains("Yes.", page, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Words\"", page, StringComparison.Ordinal);

        // Without the handler's own refusal a forged form gave Parley work, which it is never given. The page above has
        // no post form, so its token was empty and antiforgery answered 400 before the handler ran: the token comes from
        // a page with a follow-up form on another row (review, lens 3 round 37).
        Said(console, 3, 31, Wire.MessageType.Request, Billing, Person, Ask("Monthly or yearly?"));
        Said(console, 4, 31, Wire.MessageType.Result, Person, Billing, """{"outcomes":[{"id":"request","outcome":"answered","text":"Yearly."}]}""");
        Exchange(console, 31, open: false, Wire.ExchangeOutcome.Complete);

        var withForm = await client.GetStringAsync(new Uri("/Inbox?view=closed", UriKind.Relative), Ct);
        var token = Token().Match(withForm).Groups[1].Value;

        Assert.NotEmpty(token);

        using var form = new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", token),
            new("For", RoadmapConsole.Project),
            new("Exchange", "30"),
            new("Agent", Facilitator),
            new("Conversation", "talk"),
            new("Words", "Do this for me."),
        ]);

        using var response = await client.PostAsync(FollowUpAction(withForm), form, Ct);
        var after = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("A follow-up goes to one agent, in the conversation its row is in.", after, StringComparison.Ordinal);
        Assert.Empty(console.Broker.Requested);
    }

    [Fact]
    public async Task A_reference_in_the_agreed_form_is_a_button_and_nothing_else_is()
    {
        using var console = Console();

        Said(console, 1, 40, Wire.MessageType.Request, Orders, Person,
            Ask("Thanks for the answers on exchange #351 and exchanges #12, #13 or #14. Not on 351, nor `exchange #77`."));
        Exchange(console, 40, open: true);
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Orders, WaitedOn = Person, ExchangeId = 40 });

        var page = await GetAsync(console, "/Inbox");
        var buttons = Regex.Matches(page, """<button type="button" class="exchange-ref" data-exchange="(\d+)" """)
            .Select(match => match.Groups[1].Value)
            .ToList();

        Assert.Equal(["351", "12", "13", "14"], buttons);
        Assert.Contains("<code>exchange #77</code>", page, StringComparison.Ordinal);
        Assert.Contains("data-project=\"alpha\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_panel_shows_the_exchange_and_reading_it_changes_nothing()
    {
        using var console = Console();

        Said(console, 1, 50, Wire.MessageType.Request, Orders, Billing, Ask("Which ledger?"));
        Said(console, 2, 50, Wire.MessageType.Result, Billing, Orders, """{"outcomes":[{"id":"request","outcome":"answered","text":"The **new** one."}]}""");
        Exchange(console, 50, open: false, Wire.ExchangeOutcome.Complete);

        var calls = console.Broker.Calls;
        var panel = await GetAsync(console, "/Exchanges/Panel?project=alpha&id=50");

        Assert.Contains("Exchange #50", panel, StringComparison.Ordinal);
        Assert.Contains("Which ledger?", panel, StringComparison.Ordinal);
        Assert.Contains("<strong>new</strong>", panel, StringComparison.Ordinal);
        Assert.Contains("Closed: Answered.", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("<html", panel, StringComparison.OrdinalIgnoreCase);
        Assert.True(console.Broker.Calls > calls);
        Assert.Empty(console.Broker.Requested);
        Assert.Empty(console.Broker.Resulted);
    }

    [Fact]
    public async Task The_panel_shows_only_the_exchanges_own_messages_not_the_rest_of_its_conversation()
    {
        using var console = Console();

        Said(console, 1, 50, Wire.MessageType.Request, Orders, Billing, Ask("Which ledger?"));
        Said(console, 2, 51, Wire.MessageType.Request, Billing, Orders, Ask("Is the nightly export on?"));
        Said(console, 3, 50, Wire.MessageType.Result, Billing, Orders, """{"outcomes":[{"id":"request","outcome":"answered","text":"The new one."}]}""");
        Exchange(console, 50, open: false, Wire.ExchangeOutcome.Complete);
        Exchange(console, 51, open: true);

        var panel = await GetAsync(console, "/Exchanges/Panel?project=alpha&id=50");

        Assert.Contains("Which ledger?", panel, StringComparison.Ordinal);
        Assert.Contains("The new one.", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("Is the nightly export on?", panel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_follow_up_with_no_words_sends_nothing_and_says_so()
    {
        using var console = Console();

        Said(console, 1, 20, Wire.MessageType.Request, Billing, Person, Ask("Monthly or yearly?"));
        Said(console, 2, 20, Wire.MessageType.Result, Person, Billing, """{"outcomes":[{"id":"request","outcome":"answered","text":"Yearly."}]}""");
        Exchange(console, 20, open: false, Wire.ExchangeOutcome.Complete);

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/Inbox?view=closed", UriKind.Relative), Ct);

        using var form = new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", Token().Match(page).Groups[1].Value),
            new("For", RoadmapConsole.Project),
            new("Exchange", "20"),
            new("Agent", Billing),
            new("Conversation", "talk"),
            new("Words", "   "),
        ]);

        using var response = await client.PostAsync(FollowUpAction(page), form, Ct);
        var after = await response.Content.ReadAsStringAsync(Ct);

        Assert.Empty(console.Broker.Requested);
        Assert.Contains("Nothing was written, so nothing was sent.", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_panels_way_to_its_conversation_leaves_the_frame_it_was_opened_in()
    {
        // The panel is also drawn inside the roadmap's framed conversation. Without target="_top" the link opened the
        // whole console inside that frame, a console nested in a console.
        using var console = Console();

        Said(console, 1, 50, Wire.MessageType.Request, Orders, Billing, Ask("Which ledger?"));
        Exchange(console, 50, open: true);

        var panel = await GetAsync(console, "/Exchanges/Panel?project=alpha&id=50");

        Assert.Matches("""<a href="/Conversations/Detail\?[^"]*" target="_top">Open it in its conversation</a>""", panel);
    }

    [Fact]
    public async Task A_number_that_is_no_exchange_says_so()
    {
        using var console = Console();

        var panel = await GetAsync(console, "/Exchanges/Panel?project=alpha&id=999");

        Assert.Contains("There is no exchange #999 in alpha.", panel, StringComparison.Ordinal);
    }
}
