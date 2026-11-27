using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Parley.Web.Notifications;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>Lens 3 sweep on 3b752970: the conversation page's send path with files, as a reply and as a retry.</summary>
public sealed partial class AttachingToTheComposerTests
{
    private static string Hash => Parley.Core.ContentHash.Of(Png).Value;

    [Fact]
    public async Task A_reply_given_as_the_answer_names_the_files_placed_with_it()
    {
        // The file is placed, and the Result is the one message that can name it. Sent with the words alone, the agent
        // was answered without being told a file was there for it.
        using var console = Seated();

        await PostAsync(console, "Here it is.", [("ReplyTo", "12"), ("Outcome", "done")], ("shot.png", Png));

        var result = Assert.Single(console.Broker.Resulted);

        Assert.Contains("Here it is.", result.PayloadJson, StringComparison.Ordinal);
        Assert.Contains($"shot.png (sha256: {Hash})", result.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_given_as_a_clarification_names_the_files_placed_with_it()
    {
        using var console = Seated();

        await PostAsync(console, "Is this the one?", [("ReplyTo", "12")], ("shot.png", Png));

        var clarify = Assert.Single(console.Broker.Clarified);
        var said = JsonDocument.Parse(clarify.PayloadJson).RootElement.ToString();

        Assert.Contains("Is this the one?", said, StringComparison.Ordinal);
        Assert.Contains($"shot.png (sha256: {Hash})", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_the_broker_refused_is_in_the_bell_as_not_sent()
    {
        // The bell is told from the answer's flag. Marked good, a refused reply read in the bell as though it had gone.
        using var console = Seated();
        console.Broker.FailsOn = "/parley.v1.Agents/Result";

        await PostAsync(console, "Here it is.", [("ReplyTo", "12"), ("Outcome", "done")]);

        var told = Assert.Single(
            console.Services.GetRequiredService<NotificationLog>().All(), notice => notice.Source == "Reply");

        Assert.False(told.Good, "a refused reply was told as sent");
    }

    [Fact]
    public async Task Files_kept_from_a_refused_send_go_again_with_no_words_and_are_named()
    {
        // After a refusal the page keeps the placed file as an entry and the words may be cleared by the person. A send of
        // the kept file alone is a message: refused as empty, the file could only be named by typing something.
        using var console = Seated();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri(Page, UriKind.Relative), Ct);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(Token().Match(page).Groups[1].Value), "__RequestVerificationToken" },
            { new StringContent(string.Empty, Encoding.UTF8), "Says" },
            { new StringContent(Orders), "Targets" },
            { new StringContent($"shot.png|{Hash}"), "Attached" },
        };

        using var response = await client.PostAsync(new Uri(Page, UriKind.Relative), form, Ct);
        var after = await response.Content.ReadAsStringAsync(Ct);

        Assert.DoesNotContain("Nothing was written, so nothing was sent.", after, StringComparison.Ordinal);
        Assert.Contains($"shot.png (sha256: {Hash})", AskOf(Assert.Single(console.Broker.Requested)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_conversation_whose_message_naming_the_files_was_refused_says_so()
    {
        // In a conversation not started yet the words go first, then a second message names the files. That second message
        // refused, the page said nothing and the files were placed for an agent never told of them.
        using var console = Seated();
        var attempts = 0;

        console.Broker.Placing = (header, bytes) =>
        {
            if (++attempts == 1)
            {
                return new Wire.PlaceFileReply { Rejection = Wire.FileRejection.ConversationNotAssignable, Name = header.Name, ConversationId = header.ConversationId };
            }

            // The words have reached orders by now; the message naming the files is the next Request, and it is refused.
            console.Broker.RefusesRequestsTo.Add(Orders);

            return new Wire.PlaceFileReply
            {
                Outcome = Wire.PlaceOutcome.Placed,
                Rejection = Wire.FileRejection.None,
                ConversationId = header.ConversationId,
                Name = header.Name,
                SizeBytes = bytes.Length,
                Sha256 = Parley.Core.ContentHash.Of(bytes).Value,
            };
        };

        var page = await SendAsync(console, "Start here.", ("shot.png", Png));

        Assert.Equal(2, console.Broker.Requested.Count);
        Assert.Contains("the message naming the files did not reach everybody", page, StringComparison.Ordinal);
    }

    [Fact]
    public void A_kept_file_is_shown_by_its_name_and_never_with_its_hash()
    {
        // The page keeps a placed file as "name|sha256" and draws the name on its chip and tile.
        Assert.Equal("shot.png", Parley.Client.Rendering.Attaching.NameOf(Parley.Client.Rendering.Attaching.Entry("shot.png", Hash)));
        Assert.Equal("shot.png", Parley.Client.Rendering.Attaching.NameOf("shot.png"));
    }
}
