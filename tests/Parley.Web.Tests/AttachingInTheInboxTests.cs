using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>A file pasted into a message box in the Inbox is placed and named, as on the conversation page (SPEC.md §5.6).</summary>
/// <remarks>
/// <b>The owner, 2026-11-13:</b> a screenshot pasted into the conversation page's box was attached, and the same paste into
/// the Inbox's reply did nothing. Every place a person writes a message to an agent behaves the same.
/// </remarks>
[Integration]
public sealed partial class AttachingInTheInboxTests
{
    private const string Orders = "orders@test";
    private const string Billing = "billing@test";
    private const string Person = "human-user@" + RoadmapConsole.Project;
    private const long Exchange = 41;
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("""name="__RequestVerificationToken"[^>]*value="([^"]+)""")]
    private static partial Regex Token();

    /// <summary>A console where orders has asked the person to do something, in the conversation <c>shared</c>.</summary>
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
        console.Broker.Conversations.Add(new Wire.ConversationSummary
        {
            ConversationId = "shared",
            HasOpenExchange = true,
            LastActivityUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        });
        console.Broker.ExportMessages.Add(new Wire.Message
        {
            Id = 410,
            ExchangeId = Exchange,
            Type = Wire.MessageType.Request,
            From = Orders,
            To = Person,
            ResponderModel = "test",
            PayloadJson = "{\"ask\":" + JsonSerializer.Serialize("Send me the screen you see.") + "}",
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

    private static MultipartFormDataContent Form(string page, (string Name, string Value)[] fields, params (string Name, byte[] Bytes)[] files)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(Token().Match(page).Groups[1].Value), "__RequestVerificationToken" },
        };

        foreach (var (name, value) in fields)
        {
            form.Add(new StringContent(value, Encoding.UTF8), name);
        }

        foreach (var (name, bytes) in files)
        {
            var part = new ByteArrayContent(bytes);

            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(part, "Attachments", name);
        }

        return form;
    }

    [Fact]
    public async Task Both_message_forms_on_a_row_take_files_and_the_close_form_does_not()
    {
        using var console = Asked();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/Inbox", UriKind.Relative), Ct);

        foreach (var handler in new[] { "Result", "Clarify" })
        {
            var form = Regex.Match(page, $@"<form[^>]*handler={handler}[^>]*>.*?</form>", RegexOptions.Singleline, TimeSpan.FromSeconds(2)).Value;

            Assert.Contains("enctype=\"multipart/form-data\"", form, StringComparison.Ordinal);
            Assert.Contains("data-attach", form, StringComparison.Ordinal);
            Assert.Contains("name=\"Attachments\"", form, StringComparison.Ordinal);
            Assert.Contains("name=\"Conversation\" value=\"shared\"", form, StringComparison.Ordinal);
        }

        var close = Regex.Match(page, @"<form[^>]*handler=Close[^>]*>.*?</form>", RegexOptions.Singleline, TimeSpan.FromSeconds(2)).Value;

        Assert.DoesNotContain("name=\"Attachments\"", close, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_result_with_a_pasted_image_places_it_in_the_rows_conversation_and_names_it()
    {
        using var console = Asked();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/Inbox", UriKind.Relative), Ct);

        using var form = Form(
            page,
            [
                ("For", RoadmapConsole.Project),
                ("Exchange", Exchange.ToString(CultureInfo.InvariantCulture)),
                ("Conversation", "shared"),
                ("Ids", "request"),
                ("Texts", "Here it is."),
                ("Verdicts", "done"),
            ],
            ("pasted-20261113-080000-abcd.png", Png));

        using var response = await client.PostAsync(new Uri("/Inbox?handler=Result", UriKind.Relative), form, Ct);
        var after = await response.Content.ReadAsStringAsync(Ct);

        var (header, bytes) = Assert.Single(console.Broker.Placed);

        Assert.Equal(("shared", "pasted-20261113-080000-abcd.png"), (header.ConversationId, header.Name));
        Assert.Equal(Png, bytes);

        var sent = Assert.Single(console.Broker.Resulted, result => result.ExchangeId == Exchange);

        Assert.Contains("Here it is.", sent.PayloadJson, StringComparison.Ordinal);
        Assert.Contains(
            $"pasted-20261113-080000-abcd.png (sha256: {Parley.Core.ContentHash.Of(Png).Value})",
            sent.PayloadJson,
            StringComparison.Ordinal);
        Assert.Contains("Sent, and the exchange is closed.", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_that_no_seat_could_send_says_to_attach_the_files_again_and_only_when_there_were_files()
    {
        // The seat could not be taken (here, a project this console does not have), so nothing was placed. A browser cannot
        // fill a file field again, so a page that said only why would send the retry without the files (SPEC.md §5.6).
        using var console = Asked();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/Inbox", UriKind.Relative), Ct);

        (string Name, string Value)[] fields =
        [
            ("For", "no-such-project"),
            ("Exchange", Exchange.ToString(CultureInfo.InvariantCulture)),
            ("Conversation", "shared"),
            ("Ids", "request"),
            ("Texts", "Here it is."),
            ("Verdicts", "done"),
        ];

        using var withAFile = Form(page, fields, ("shot.png", Png));
        using var refused = await client.PostAsync(new Uri("/Inbox?handler=Result", UriKind.Relative), withAFile, Ct);
        var said = await refused.Content.ReadAsStringAsync(Ct);

        Assert.Empty(console.Broker.Placed);
        Assert.Empty(console.Broker.Resulted);
        Assert.Contains("Nothing was sent. This console has no such project. Attach your files again.", said, StringComparison.Ordinal);

        using var withNone = Form(page, fields);
        using var plain = await client.PostAsync(new Uri("/Inbox?handler=Result", UriKind.Relative), withNone, Ct);
        var saidPlain = await plain.Content.ReadAsStringAsync(Ct);

        Assert.Contains("Nothing was sent. This console has no such project.", saidPlain, StringComparison.Ordinal);
        Assert.DoesNotContain("Attach your files again", saidPlain, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_follow_up_with_only_a_file_is_sent_naming_it()
    {
        using var console = Asked();

        // A closed exchange with billing, which the Closed view offers a follow-up on.
        console.Broker.ExportMessages.Add(new Wire.Message
        {
            Id = 420,
            ExchangeId = 42,
            Type = Wire.MessageType.Request,
            From = Billing,
            To = Person,
            ResponderModel = "test",
            PayloadJson = "{\"ask\":\"Monthly or yearly?\"}",
            ConversationId = "shared",
            CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-30).ToString("O", CultureInfo.InvariantCulture),
        });
        console.Broker.ExportMessages.Add(new Wire.Message
        {
            Id = 421,
            ExchangeId = 42,
            Type = Wire.MessageType.Result,
            From = Person,
            To = Billing,
            ResponderModel = "test",
            PayloadJson = """{"outcomes":[{"id":"request","outcome":"answered","text":"Yearly."}]}""",
            ConversationId = "shared",
            CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-20).ToString("O", CultureInfo.InvariantCulture),
        });
        console.Broker.ExportExchanges.Add(new Wire.ExchangeSnapshot
        {
            ExchangeId = 42,
            State = Wire.ExchangeState.Closed,
            OpenedUtc = DateTimeOffset.UtcNow.AddHours(-2).ToString("O", CultureInfo.InvariantCulture),
            ClosedUtc = DateTimeOffset.UtcNow.AddMinutes(-20).ToString("O", CultureInfo.InvariantCulture),
            Outcome = Wire.ExchangeOutcome.Complete,
        });

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/Inbox?view=closed", UriKind.Relative), Ct);
        var action = System.Net.WebUtility.HtmlDecode(
            Regex.Match(page, @"action=""(/Inbox\?[^""]*handler=FollowUp[^""]*)""", RegexOptions.None, TimeSpan.FromSeconds(2)).Groups[1].Value);

        Assert.NotEmpty(action);

        using var form = Form(
            page,
            [("For", RoadmapConsole.Project), ("Exchange", "42"), ("Agent", Billing), ("Conversation", "shared"), ("Words", "")],
            ("invoice.png", Png));

        using var response = await client.PostAsync(new Uri(action, UriKind.Relative), form, Ct);

        Assert.Single(console.Broker.Placed, placed => placed.Header.ConversationId == "shared" && placed.Header.Name == "invoice.png");

        var sent = Assert.Single(console.Broker.Requested);

        Assert.Equal(Billing, sent.To);
        Assert.Contains("invoice.png (sha256: ", sent.PayloadJson, StringComparison.Ordinal);
    }
}
