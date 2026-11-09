using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Parley.Web.Notifications;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>Sending a Result from the Inbox: it goes, it is said to have gone, and the question leaves the list.</summary>
/// <remarks>
/// <para>
/// <b>The owner, 2026-11-06:</b> answered an item with "Send result and close" and asked why it stayed and what the
/// unlabelled "Done" meant. No test had ever sent a Result from this page, and the page read a send's success off its first
/// word: a Result that went says "Sent, and the exchange is closed.", which a match on "Sent." took as refused. The bell
/// said it failed, and the form kept the words as for a refusal.
/// </para>
/// </remarks>
[Integration]
public sealed partial class SendingAResultFromTheInboxTests
{
    private const string Orders = "orders@test";
    private const string Person = "human-user@" + RoadmapConsole.Project;
    private const long Exchange = 41;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("""name="__RequestVerificationToken"[^>]*value="([^"]+)""")]
    private static partial Regex Token();

    private static RoadmapConsole Asked()
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
        console.Broker.Conversations.Add(new Wire.ConversationSummary { ConversationId = "shared", HasOpenExchange = true });
        console.Broker.ExportMessages.Add(new Wire.Message
        {
            Id = 410,
            ExchangeId = Exchange,
            Type = Wire.MessageType.Request,
            From = Orders,
            To = Person,
            ResponderModel = "test",
            PayloadJson = "{\"ask\":" + JsonSerializer.Serialize("Turn on the nightly export.") + "}",
            ConversationId = "shared",
            CreatedUtc = "2026-11-06T10:00:00Z",
        });
        console.Broker.ExportExchanges.Add(new Wire.ExchangeSnapshot
        {
            ExchangeId = Exchange,
            State = Wire.ExchangeState.Open,
            OpenedUtc = "2026-11-06T10:00:00Z",
        });
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Orders, WaitedOn = Person, ExchangeId = Exchange });

        return console;
    }

    [Fact]
    public async Task A_result_that_went_is_said_to_have_gone_and_its_question_leaves_the_list()
    {
        using var console = Asked();
        using var client = console.CreateClient();

        var before = await client.GetStringAsync(new Uri("/Inbox", UriKind.Relative), Ct);

        Assert.Contains("Turn on the nightly export.", before, StringComparison.Ordinal);

        using var form = new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", Token().Match(before).Groups[1].Value),
            new("For", RoadmapConsole.Project),
            new("Exchange", Exchange.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("Ids", "request"),
            new("Texts", "Switched it on; it runs at two."),
            new("Verdicts", "done"),
        ]);

        using var response = await client.PostAsync(new Uri("/Inbox?handler=Result", UriKind.Relative), form, Ct);
        var after = await response.Content.ReadAsStringAsync(Ct);

        Assert.Single(console.Broker.Resulted, sent => sent.ExchangeId == Exchange);
        Assert.Contains("Sent, and the exchange is closed.", after, StringComparison.Ordinal);
        Assert.DoesNotContain("Turn on the nightly export.", after, StringComparison.Ordinal);
        Assert.DoesNotContain("Switched it on; it runs at two.", after, StringComparison.Ordinal);

        var told = console.Services.GetRequiredService<NotificationLog>().All();

        Assert.Contains(told, one => one.Text.StartsWith("Sent, and", StringComparison.Ordinal) && one.Good);
    }

    [Fact]
    public async Task A_result_sent_after_the_seats_went_idle_goes_on_the_first_press()
    {
        // The owner, 2026-11-09: a response sent from the Inbox brought the same request back, and only a second send
        // went. The book had let the seat go after twenty idle minutes; the post found no seat and reloaded the page,
        // whose render took the seat back for the second press.
        using var console = Asked();
        using var client = console.CreateClient();

        var before = await client.GetStringAsync(new Uri("/Inbox", UriKind.Relative), Ct);
        var book = console.Services.GetRequiredService<Parley.Client.Seating.SeatBook>();

        Assert.True(book.Sweep(DateTimeOffset.UtcNow + Parley.Client.Seating.SeatBook.IdleAfter + TimeSpan.FromMinutes(1)) > 0);

        using var form = new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", Token().Match(before).Groups[1].Value),
            new("For", RoadmapConsole.Project),
            new("Exchange", Exchange.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("Ids", "request"),
            new("Texts", "Switched it on; it runs at two."),
            new("Verdicts", "done"),
        ]);

        using var response = await client.PostAsync(new Uri("/Inbox?handler=Result", UriKind.Relative), form, Ct);

        Assert.Single(console.Broker.Resulted, sent => sent.ExchangeId == Exchange);
        Assert.Contains("Sent, and the exchange is closed.", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_result_with_no_seat_to_be_had_keeps_its_words_and_says_nothing_was_sent()
    {
        using var console = Asked();
        using var client = console.CreateClient();

        var before = await client.GetStringAsync(new Uri("/Inbox", UriKind.Relative), Ct);

        console.Services.GetRequiredService<Parley.Client.Seating.SeatBook>()
            .Sweep(DateTimeOffset.UtcNow + Parley.Client.Seating.SeatBook.IdleAfter + TimeSpan.FromMinutes(1));
        console.Broker.FailsOn = "/Attach";

        using var form = new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", Token().Match(before).Groups[1].Value),
            new("For", RoadmapConsole.Project),
            new("Exchange", Exchange.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("Ids", "request"),
            new("Texts", "Switched it on; it runs at two."),
            new("Verdicts", "done"),
        ]);

        using var response = await client.PostAsync(new Uri("/Inbox?handler=Result", UriKind.Relative), form, Ct);
        var after = await response.Content.ReadAsStringAsync(Ct);

        Assert.Empty(console.Broker.Resulted);
        Assert.Contains("Nothing was sent:", after, StringComparison.Ordinal);
        Assert.Contains("Switched it on; it runs at two.", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_outcome_of_each_item_is_labelled_on_screen()
    {
        using var console = Asked();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/Inbox", UriKind.Relative), Ct);

        Assert.Matches(
            $"""<label for="v-{Exchange}-request">Outcome for request</label>\s*<select name="Verdicts" id="v-{Exchange}-request">""",
            page);
    }
}
