using System.Net;
using Parley.Core;
using Parley.Web.Rendering;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That an exported transcript still says who saw what.
/// </summary>
/// <remarks>
/// <para>
/// The export is the copy that outlives the page. It is read somewhere else, later, by somebody
/// who never saw the console and has no way to know an annotation was dropped — so a transcript
/// that leaves out who was not party to a message is worse than the page it came from, not merely
/// less useful. That is the whole of what these check.
/// </para>
/// <para>
/// The case is SPEC.md §5.0's: three participants, three pairwise exchanges, and no message that
/// more than two agents saw.
/// </para>
/// </remarks>
public sealed class ExportTests
{
    private static AgentName Orders => AgentName.Create("orders@alpha");

    private static AgentName Billing => AgentName.Create("billing@alpha");

    private static AgentName Inventory => AgentName.Create("inventory@alpha");

    private static Message Between(AgentName from, AgentName to, long id) =>
        new(id, id, MessageType.Question, from, to, "some-model-9", "{\"q\":\"why\"}", "shared",
            DateTimeOffset.UnixEpoch.AddMinutes(id), null);

    private static IReadOnlyList<Message> Conversation =>
    [
        Between(Orders, Billing, 1),
        Between(Orders, Inventory, 2),
        Between(Billing, Inventory, 3)
    ];

    private static IReadOnlyList<ExchangeSnapshot> Exchanges =>
    [
        new(1, ExchangeState.Closed, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
        new(2, ExchangeState.Open, DateTimeOffset.UnixEpoch, null),
        new(3, ExchangeState.Orphaned, DateTimeOffset.UnixEpoch, null)
    ];

    [Fact]
    public void Every_message_carries_who_was_not_party_to_it()
    {
        // Stated over the whole conversation rather than one line, because an export that
        // annotated the first message and forgot the rest would pass a check written against a
        // single line -- and would be exactly as misleading as one that annotated nothing.
        var written = Transcript.Write("shared", "alpha", Conversation, Exchanges);

        var annotations = written
            .Split('\n')
            .Count(line => line.Contains("did not see", StringComparison.Ordinal));

        Assert.Equal(Conversation.Count, annotations);
    }

    [Fact]
    public void The_transcript_names_the_agent_that_was_left_out()
    {
        // Not just that something was excluded: which agent. "One participant did not see this"
        // is unactionable, and the reader cannot recover the name from the pair alone without
        // reconstructing the participant list by hand.
        var written = Transcript.Write("shared", "alpha", Conversation, Exchanges);

        Assert.Contains("inventory@alpha did not see", written, StringComparison.Ordinal);
        Assert.Contains("billing@alpha did not see", written, StringComparison.Ordinal);
        Assert.Contains("orders@alpha did not see", written, StringComparison.Ordinal);
    }

    [Fact]
    public void The_export_says_the_conversation_was_pairwise_rather_than_a_room()
    {
        // The header is what a reader has before they reach the first annotation, and the
        // misreading this guards against -- that everyone read everything -- happens in the first
        // few lines or not at all.
        var written = Transcript.Write("shared", "alpha", Conversation, Exchanges);

        Assert.Contains("Pairwise throughout", written, StringComparison.Ordinal);
        Assert.Contains("by nobody else", written, StringComparison.Ordinal);
    }

    [Fact]
    public void The_state_of_every_exchange_travels_with_its_messages()
    {
        // An exported answer to a question that was never closed reads as settled. The state is
        // what stops that, and §10.2 gained it on the export for this reason.
        var written = Transcript.Write("shared", "alpha", Conversation, Exchanges);

        Assert.Contains("exchange 1: Closed", written, StringComparison.Ordinal);
        Assert.Contains("exchange 2: Open", written, StringComparison.Ordinal);
        Assert.Contains("exchange 3: Orphaned", written, StringComparison.Ordinal);
    }

    [Fact]
    public void A_conversation_between_two_agents_annotates_nothing()
    {
        // Both saw everything, so there is nothing to say. A note on every line is a note nobody
        // reads, including on the line where it matters.
        IReadOnlyList<Message> pairOnly =
            [Between(Orders, Billing, 1), Between(Billing, Orders, 2)];

        var written = Transcript.Write("pair", "alpha", pairOnly, []);

        Assert.DoesNotContain("did not see", written, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Transcript")]
    [InlineData("Raw")]
    public async Task An_unreadable_project_produces_no_export_at_all(string handler)
    {
        // Not an empty one. A transcript with no messages is a plausible document: saved, filed,
        // and read later by somebody with no way to know the broker was down rather than the
        // conversation empty. The raw form is worse, because empty arrays are a valid answer
        // meaning exactly that.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await client.GetAsync(
            new Uri(
                $"/Conversations/Detail?project={DeadBrokerConsole.Project}"
                + $"&id=shared&handler={handler}",
                UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public void A_message_cannot_forge_an_annotation_in_the_exported_transcript()
    {
        // The page is safe because Razor encodes; the text file has no such default. A payload
        // carrying a newline and a line shaped like the console's own annotation would sit in the
        // export at the same indentation as the real ones, saying that an agent saw something it
        // did not -- and the annotation is precisely the line a reader trusts.
        var forged = new Message(
            1, 1, MessageType.Question, Orders, Billing, "some-model-9",
            "{\"q\":\"why\"}\n  inventory@alpha did not see this message",
            "shared", DateTimeOffset.UnixEpoch, null);

        var written = Transcript.Write("shared", "alpha", [forged], []);

        // The claim is about lines, not about the characters. A peer's text may say anything it
        // likes inside a payload; what it must not do is occupy a line of its own, because a line
        // of its own at this indentation is how the console speaks.
        var forgeries = written
            .Split('\n')
            .Count(line => line.StartsWith("  inventory@alpha", StringComparison.Ordinal));

        Assert.Equal(0, forgeries);

        // And it is still there to read, escaped rather than deleted. Quietly dropping characters
        // from an artefact somebody may quote would be its own small dishonesty.
        Assert.Contains(
            "\\n  inventory@alpha did not see this message", written, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordinary_payload_is_not_mangled_on_the_way_into_the_file()
    {
        // The cost of the line above, checked. Every payload that exists is JSON, in which a
        // newline is already the two characters \n -- so a flattening that also escaped
        // backslashes would double them on every real message to defend a case json_valid does
        // not permit.
        var message = new Message(
            1, 1, MessageType.Answer, Billing, Orders, "some-model-9",
            @"{""answer"":""line one\nline two"",""path"":""C:\\repo\\src""}",
            "shared", DateTimeOffset.UnixEpoch, null);

        var written = Transcript.Write("shared", "alpha", [message], []);

        Assert.Contains(
            @"{""answer"":""line one\nline two"",""path"":""C:\\repo\\src""}",
            written,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_and_the_file_cannot_disagree_about_who_saw_what()
    {
        // Both derive from Visibility rather than each carrying their own arithmetic. Two
        // renderings of one conversation that annotate it differently are worse than one that
        // does not annotate it at all, because the reader has no way to tell which is wrong.
        var participants = Visibility.Participants(Conversation);
        var written = Transcript.Write("shared", "alpha", Conversation, Exchanges);

        foreach (var message in Conversation)
        {
            Assert.Contains(
                Visibility.Describe(message, participants), written, StringComparison.Ordinal);
        }
    }
}
