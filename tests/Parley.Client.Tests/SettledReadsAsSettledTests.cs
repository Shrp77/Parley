using Parley.Client.Rendering;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// That a question an agent settled reads as settled by that agent, and that the reply which settled it carries the list
/// beneath it whatever its own words say (SPEC.md §5.6, FR-017, FR-021).
/// </summary>
/// <remarks>
/// <b>The list is the record, not the reply.</b> The owner's condition for letting the agent decide was that the person
/// can see what it decided, beside its reply, without trusting the model to mention it — so every case here has a reply
/// whose words say nothing about settling.
/// </remarks>
public sealed class SettledReadsAsSettledTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Person = AgentName.Create("human-user@test");
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    private const string Conversation = "invoice-export";

    private static Message Said(long id, long exchange, MessageType type, AgentName from, AgentName to, string payload, DateTimeOffset at) =>
        new(id, exchange, type, from, to, "model-a", payload, Conversation, at, null);

    // orders asked two things (exchanges 1 and 2); the person wrote (exchange 3); orders replied, settling 1.
    private static ConversationExport Settling()
    {
        var reply = T0.AddMinutes(10);

        return new ConversationExport(
            [
                Said(1, 1, MessageType.Request, Orders, Person, """{"ask":"Which invoice schema should the export target?"}""", T0),
                Said(2, 2, MessageType.Request, Orders, Person, """{"ask":"Gross or net?"}""", T0.AddMinutes(1)),
                Said(3, 3, MessageType.Request, Person, Orders, """{"ask":"Twelve."}""", T0.AddMinutes(5)),
                Said(4, 3, MessageType.Result, Orders, Person,
                    """{"outcomes":[{"id":"request","outcome":"answered","text":"Done."}]}""", reply),
            ],
            [
                new ExchangeSnapshot(1, ExchangeState.Closed, T0, reply, ExchangeOutcome.Settled, 3),
                new ExchangeSnapshot(2, ExchangeState.Open, T0.AddMinutes(1), null),
                new ExchangeSnapshot(3, ExchangeState.Closed, T0.AddMinutes(5), reply, ExchangeOutcome.Complete),
            ],
            null);
    }

    [Fact]
    public void A_settled_question_says_so_and_by_whom()
    {
        var state = Reading.State(Settling().Exchanges[0]);

        Assert.Contains("settled by the agent that asked", state, StringComparison.Ordinal);
        Assert.Contains("exchange 3", state, StringComparison.Ordinal);
        Assert.DoesNotContain("answered", state, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reply_that_settled_it_lists_it_beneath_whatever_its_words_say()
    {
        var export = Settling();

        Assert.Equal(["Which invoice schema should the export target?"], Reading.Settled(export.Messages[3], export));

        var lines = Reading.Lines(export);
        var replyHeader = lines.ToList().FindIndex(l => l.Kind == LineKind.Header && l.Message == 4);
        var settled = lines.Single(l => l.Text.Contains("Settled:", StringComparison.Ordinal));

        Assert.True(lines.ToList().IndexOf(settled) > replyHeader, "the list is not beneath the reply");
        Assert.Equal(4, settled.Message);
        Assert.Contains("“Which invoice schema should the export target?”", settled.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void No_other_message_carries_a_list()
    {
        var export = Settling();

        Assert.All(export.Messages.Take(3), m => Assert.Empty(Reading.Settled(m, export)));
    }

    [Fact]
    public void A_reply_at_another_instant_is_not_credited_with_a_settle()
    {
        // Settling is matched on the reply's own instant, which the broker writes with the settle in one transaction.
        var export = Settling();
        var other = export.Messages[3] with { CreatedUtc = T0.AddMinutes(11) };

        Assert.Empty(Reading.Settled(other, export));
    }

    [Fact]
    public void A_question_whose_words_were_pruned_is_named_by_its_exchange()
    {
        var export = Settling();
        var pruned = export with { Messages = [.. export.Messages.Skip(1)] };

        Assert.Equal(["exchange 1"], Reading.Settled(pruned.Messages[2], pruned));
    }

    [Fact]
    public void A_long_question_is_cut_and_its_line_breaks_are_gone()
    {
        var reply = T0.AddMinutes(10);
        var export = new ConversationExport(
            [
                Said(1, 1, MessageType.Request, Orders, Person,
                    """{"ask":"First line\nsecond line, and then a great deal more that goes on well past sixty characters"}""", T0),
                Said(2, 2, MessageType.Request, Person, Orders, """{"ask":"Yes."}""", T0.AddMinutes(5)),
                Said(3, 2, MessageType.Result, Orders, Person,
                    """{"outcomes":[{"id":"request","outcome":"answered","text":"Ok."}]}""", reply),
            ],
            [
                new ExchangeSnapshot(1, ExchangeState.Closed, T0, reply, ExchangeOutcome.Settled, 2),
                new ExchangeSnapshot(2, ExchangeState.Closed, T0.AddMinutes(5), reply, ExchangeOutcome.Complete),
            ],
            null);

        var said = Assert.Single(Reading.Settled(export.Messages[2], export));

        Assert.DoesNotContain('\n', said);
        Assert.True(said.Length <= 60, $"{said.Length} characters");
        Assert.EndsWith("…", said, StringComparison.Ordinal);
    }

    [Fact]
    public void The_export_carries_the_list_too()
    {
        var export = Settling();

        var written = Transcript.Write(Conversation, "alpha", export.Messages, export.Exchanges);

        Assert.Contains("Settled: “Which invoice schema should the export target?”", written, StringComparison.Ordinal);
    }
}
