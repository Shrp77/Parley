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
