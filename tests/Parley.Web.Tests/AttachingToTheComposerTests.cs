using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Parley.Client.Rendering;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>Files pasted or dropped into the comment box are placed, then named in the message (SPEC.md §9.5).</summary>
[Integration]
public sealed partial class AttachingToTheComposerTests
{
    private const string Orders = "orders@test";
    private static readonly string Page = $"/Conversations/Detail?project={RoadmapConsole.Project}&id=shared";
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("""name="__RequestVerificationToken"[^>]*value="([^"]+)""")]
    private static partial Regex Token();

    private static RoadmapConsole Seated()
    {
        var console = new RoadmapConsole();

        console.Broker.Seats = true;
        console.Broker.Registry.Add(new Wire.RegistryRow
        {
            Name = "human-user@" + RoadmapConsole.Project,
            Adapter = "parley-console",
            Tier = Wire.AgentTier.Full,
            Status = Wire.AgentStatus.Active,
        });
        console.Broker.Registry.Add(new Wire.RegistryRow
        {
            Name = Orders,
            Adapter = "mcp",
            Tier = Wire.AgentTier.Full,
            Status = Wire.AgentStatus.Active,
        });

        return console;
    }

    private static async Task<string> SendAsync(RoadmapConsole console, string says, params (string Name, byte[] Bytes)[] files)
    {
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri(Page, UriKind.Relative), Ct);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(Token().Match(page).Groups[1].Value), "__RequestVerificationToken" },
            { new StringContent(says, Encoding.UTF8), "Says" },
            { new StringContent(Orders), "Targets" },
        };

        foreach (var (name, bytes) in files)
        {
            var part = new ByteArrayContent(bytes);

            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(part, "Attachments", name);
        }

        using var response = await client.PostAsync(new Uri(Page, UriKind.Relative), form, Ct);

        return await response.Content.ReadAsStringAsync(Ct);
    }

    private static string AskOf(Wire.OpenRequest sent) =>
        JsonDocument.Parse(sent.PayloadJson).RootElement.GetProperty("ask").GetString()!;

    [Fact]
    public async Task A_file_is_placed_as_sent_and_named_with_its_hash_under_the_persons_words()
    {
        using var console = Seated();

        await SendAsync(console, "Look at this.", ("shot.png", Png));

        var (header, bytes) = Assert.Single(console.Broker.Placed);

        Assert.Equal(("shared", "shot.png"), (header.ConversationId, header.Name));
        Assert.Equal(Png, bytes);

        var ask = AskOf(Assert.Single(console.Broker.Requested));

        Assert.StartsWith("Look at this.\n\nAttached", ask, StringComparison.Ordinal);
        Assert.Contains($"shot.png (sha256: {Parley.Core.ContentHash.Of(Png).Value})", ask, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_message_may_be_only_files()
    {
        using var console = Seated();

        await SendAsync(console, string.Empty, ("shot.png", Png));

        Assert.StartsWith("Attached", AskOf(Assert.Single(console.Broker.Requested)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_name_the_broker_placed_it_under_is_the_one_named()
    {
        using var console = Seated();
        console.Broker.Placing = (header, bytes) => new Wire.PlaceFileReply
        {
            Outcome = Wire.PlaceOutcome.Placed,
            Rejection = Wire.FileRejection.None,
            ConversationId = header.ConversationId,
            Name = "my_shot.png",
            SizeBytes = bytes.Length,
            Sha256 = Parley.Core.ContentHash.Of(bytes).Value,
        };

        await SendAsync(console, "Look.", ("my shot.png", Png));

        Assert.Contains("my_shot.png (sha256:", AskOf(Assert.Single(console.Broker.Requested)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_refused_sends_nothing_and_says_which()
    {
        using var console = Seated();
        console.Broker.Placing = (header, _) => new Wire.PlaceFileReply
        {
            Rejection = Wire.FileRejection.ExtensionNotAllowed,
            ConversationId = header.ConversationId,
            Name = header.Name,
        };

        var page = await SendAsync(console, "Look.", ("macro.exe", Png));

        Assert.Empty(console.Broker.Requested);
        Assert.Contains("Nothing was sent. macro.exe could not be attached", page, StringComparison.Ordinal);
        Assert.Contains(">Look.</textarea>", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_send_refused_after_placing_keeps_the_file_so_it_is_not_uploaded_again()
    {
        using var console = Seated();
        console.Broker.RefusesRequestsTo.Add(Orders);

        var page = await SendAsync(console, "Look.", ("shot.png", Png));

        Assert.Contains($"name=\"Attached\" value=\"shot.png|{Parley.Core.ContentHash.Of(Png).Value}\"", page, StringComparison.Ordinal);
        Assert.Contains("data-keep-open=\"true\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_conversation_not_started_gets_the_words_first_then_a_second_message_naming_the_files()
    {
        using var console = Seated();
        var attempts = 0;

        console.Broker.Placing = (header, bytes) => ++attempts == 1
            ? new Wire.PlaceFileReply { Rejection = Wire.FileRejection.ConversationNotAssignable, Name = header.Name, ConversationId = header.ConversationId }
            : new Wire.PlaceFileReply
            {
                Outcome = Wire.PlaceOutcome.Placed,
                Rejection = Wire.FileRejection.None,
                ConversationId = header.ConversationId,
                Name = header.Name,
                SizeBytes = bytes.Length,
                Sha256 = Parley.Core.ContentHash.Of(bytes).Value,
            };

        await SendAsync(console, "Start here.", ("shot.png", Png));

        Assert.Equal(2, console.Broker.Requested.Count);
        Assert.Equal("Start here.", AskOf(console.Broker.Requested[0]));
        Assert.Contains("shot.png (sha256:", AskOf(console.Broker.Requested[1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_conversation_whose_files_are_refused_after_the_words_went_says_the_words_went_and_the_files_did_not()
    {
        // In a conversation not started yet the files follow the words. The words reached orders, then the placement was
        // refused: the page must not read as though nothing was sent, nor name files nobody holds.
        using var console = Seated();
        var attempts = 0;

        console.Broker.Placing = (header, _) => ++attempts == 1
            ? new Wire.PlaceFileReply { Rejection = Wire.FileRejection.ConversationNotAssignable, Name = header.Name, ConversationId = header.ConversationId }
            : new Wire.PlaceFileReply { Rejection = Wire.FileRejection.ExtensionNotAllowed, Name = header.Name, ConversationId = header.ConversationId };

        var page = await SendAsync(console, "Start here.", ("shot.png", Png));

        Assert.Equal(2, attempts);
        Assert.Equal("Start here.", AskOf(Assert.Single(console.Broker.Requested)));
        Assert.Contains("Your words went, and the files did not. Nothing was sent. shot.png could not be attached", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_conversation_sent_to_one_agent_that_refused_keeps_its_files_for_the_retry()
    {
        // Words to two agents in a conversation not started yet, billing refused: the files went to orders alone and the
        // list was cleared, so the retry gave billing the words and no files, without a word (review, lens 1 round 21).
        const string Billing = "billing@" + RoadmapConsole.Project;
        using var console = Seated();
        var attempts = 0;

        console.Broker.Registry.Add(new Wire.RegistryRow
        {
            Name = Billing, Adapter = "mcp", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active,
        });
        console.Broker.RefusesRequestsTo.Add(Billing);
        console.Broker.Placing = (header, bytes) => ++attempts == 1
            ? new Wire.PlaceFileReply { Rejection = Wire.FileRejection.ConversationNotAssignable, Name = header.Name, ConversationId = header.ConversationId }
            : new Wire.PlaceFileReply
            {
                Outcome = Wire.PlaceOutcome.Placed,
                Rejection = Wire.FileRejection.None,
                ConversationId = header.ConversationId,
                Name = header.Name,
                SizeBytes = bytes.Length,
                Sha256 = Parley.Core.ContentHash.Of(bytes).Value,
            };

        using var client = console.CreateClient();

        var before = await client.GetStringAsync(new Uri(Page, UriKind.Relative), Ct);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(Token().Match(before).Groups[1].Value), "__RequestVerificationToken" },
            { new StringContent("Start here.", Encoding.UTF8), "Says" },
            { new StringContent(Orders), "Targets" },
            { new StringContent(Billing), "Targets" },
        };
        var part = new ByteArrayContent(Png);

        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "Attachments", "shot.png");

        using var response = await client.PostAsync(new Uri(Page, UriKind.Relative), form, Ct);
        var after = await response.Content.ReadAsStringAsync(Ct);

        Assert.Contains($"name=\"Attached\" value=\"shot.png|{Parley.Core.ContentHash.Of(Png).Value}\"", after, StringComparison.Ordinal);

        // Both stay ticked. Narrowing the retry to the refused was undone by the page's own remembered ticks in a browser
        // and, without script, dropped an agent that took the words but missed the files (review, lens 1 round 23); a
        // retry that repeats to some is the recorded cost.
        Assert.Contains($"name=\"Targets\" value=\"{Billing}\" checked=\"checked\"", after, StringComparison.Ordinal);
        Assert.Contains($"name=\"Targets\" value=\"{Orders}\" checked=\"checked\"", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_conversation_nobody_took_says_its_files_must_be_attached_again()
    {
        // Every recipient refused: the files follow the first words in a new conversation, so they were never placed, and
        // a browser cannot fill a file field again. The page said nothing and the retry went without them (review, lens 1
        // round 22).
        using var console = Seated();

        console.Broker.RefusesRequestsTo.Add(Orders);
        console.Broker.Placing = (header, _) =>
            new Wire.PlaceFileReply { Rejection = Wire.FileRejection.ConversationNotAssignable, Name = header.Name, ConversationId = header.ConversationId };

        var page = await SendAsync(console, "Start here.", ("shot.png", Png));

        Assert.Contains("Attach them again", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_files_the_message_is_exactly_what_was_written()
    {
        using var console = Seated();

        await SendAsync(console, "Just words.");

        Assert.Empty(console.Broker.Placed);
        Assert.Equal("Just words.", AskOf(Assert.Single(console.Broker.Requested)));
    }

    [Fact]
    public async Task The_page_offers_the_rule_and_sends_files_with_the_form()
    {
        using var console = Seated();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri(Page, UriKind.Relative), Ct);

        Assert.Contains("enctype=\"multipart/form-data\"", page, StringComparison.Ordinal);
        Assert.Contains("data-types=\".md .txt .png .jpg .jpeg .gif .webp\"", page, StringComparison.Ordinal);
        Assert.Contains($"data-paste-limit=\"{Parley.Web.Pages.Conversations.DetailModel.PasteBecomesAFileOver}\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain(".svg", page[page.IndexOf("id=\"attachments\"", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    [Fact]
    public void A_message_names_each_file_after_the_words()
    {
        Assert.Equal(
            "Hello\n\nAttached, to fetch with parley_file_fetch:\n- a.png (sha256: 11)\n- b.txt (sha256: 22)",
            Attaching.Message("Hello\n", [Attaching.Entry("a.png", "11"), Attaching.Entry("b.txt", "22")]));
    }
}
