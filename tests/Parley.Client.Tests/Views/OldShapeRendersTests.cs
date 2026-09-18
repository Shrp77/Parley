using Parley.Client.Rendering;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests.Views;

/// <summary>
/// A conversation written under 0.5 or 0.6 still reads, after the protocol stopped describing it.
/// </summary>
/// <remarks>
/// <para>
/// This is FR-009, and it is the cheapest thing in the change to get wrong — invisibly. Migration 011
/// rewrites no row, so the owner's store holds Requests carrying an intent, items with kinds and gates,
/// and a <c>done_when</c>, and Results answering each item by its identifier. A renderer written only
/// against 0.7 would show those as an empty bubble, or as the raw document, and nothing would fail: the
/// message is still there, the transcript still draws, and the words a participant wrote are simply gone
/// from the screen.
/// </para>
/// <para>
/// So each assertion below is that a field the protocol no longer has is still <em>shown</em>. The
/// complement is asserted too — a 0.7 message renders as the ask and invents nothing — because a reader
/// that carried a stale field forward would be the same defect from the other side.
/// </para>
/// </remarks>
public sealed class OldShapeRendersTests
{
    private const string OldRequest =
        """{"intent":"deciding where tax is added","items":[{"id":"r1","kind":"work","text":"use the order's price"},{"id":"r2","kind":"question","gate":{"on":"r1","equals":"done"},"text":"which invoice version?"}],"done_when":"invoices show the order's price and r2 names the version"}""";

    private const string OldResult =
        """{"outcomes":[{"id":"r1","outcome":"done","text":"merged in 4.2"},{"id":"r2","outcome":"answered","sources":["inventory@test"],"text":"schema 12"}]}""";

    private static Message Message(MessageType type, string payload) =>
        new(
            1,
            1,
            type,
            AgentName.Create("orders@test"),
            AgentName.Create("billing@test"),
            "some-model-9",
            payload,
            "pricing",
            new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
            null);

    private static string Written(MessageType type, string payload) =>
        string.Join("\n", Reading.Lines([Message(type, payload)]).Select(l => l.Text));

    [Fact]
    public void A_0_6_request_still_shows_its_intent_its_items_and_what_counted_as_finished()
    {
        var said = Written(MessageType.Request, OldRequest);

        Assert.Contains("deciding where tax is added", said, StringComparison.Ordinal);
        Assert.Contains("use the order's price", said, StringComparison.Ordinal);
        Assert.Contains("which invoice version?", said, StringComparison.Ordinal);
        Assert.Contains("invoices show the order's price", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_0_6_request_still_shows_each_items_own_identifier()
    {
        // The identifiers are not decoration: an exchange opened under 0.6 is still answered by them, so a
        // person reading the transcript to work out what a Result must name has to be able to see them.
        var lines = Reading.Lines([Message(MessageType.Request, OldRequest)]);

        var points = lines.Where(l => l.Kind == LineKind.Point).Select(l => l.Text).ToArray();

        Assert.Equal(2, points.Length);
        Assert.Contains(points, p => p.Contains("r1", StringComparison.Ordinal));
        Assert.Contains(points, p => p.Contains("r2", StringComparison.Ordinal));
    }

    [Fact]
    public void A_0_6_result_still_shows_one_line_per_item_with_its_outcome_and_sources()
    {
        var said = Written(MessageType.Result, OldResult);

        Assert.Contains("merged in 4.2", said, StringComparison.Ordinal);
        Assert.Contains("schema 12", said, StringComparison.Ordinal);
        Assert.Contains("done", said, StringComparison.Ordinal);
        Assert.Contains("answered", said, StringComparison.Ordinal);
        Assert.Contains("inventory@test", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_0_7_request_shows_the_ask_and_nothing_a_reader_would_have_to_explain()
    {
        var lines = Reading.Lines([Message(MessageType.Request, """{"ask":"Can invoices use the price the order supplies?"}""")]);

        Assert.Contains(lines, l => l.Text.Contains("Can invoices use the price", StringComparison.Ordinal));

        // No items, no finish line, and no fallback to the raw document — which is what a reader that had
        // not learned `ask` would produce, and which would look almost right on screen.
        Assert.DoesNotContain(lines, l => l.Kind == LineKind.Point);
        Assert.DoesNotContain(lines, l => l.Kind == LineKind.DoneWhen);
        Assert.DoesNotContain(lines, l => l.Text.Contains("\"ask\"", StringComparison.Ordinal));
    }

    [Fact]
    public void A_0_7_result_answering_the_whole_request_reads_as_one_outcome()
    {
        var said = Written(
            MessageType.Result,
            """{"outcomes":[{"id":"request","outcome":"done","text":"It can: the order carries it."}]}""");

        Assert.Contains("It can: the order carries it.", said, StringComparison.Ordinal);
        Assert.Contains("done", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_0_4_question_carried_forward_still_reads()
    {
        // Migration 008 carried 0.4's questions across as Requests without reshaping them, so the oldest
        // thing a live store can hold is this, and it has now survived two protocol changes.
        var said = Written(
            MessageType.Request,
            """{"intent":"where is the rate","sub_questions":[{"id":"q1","text":"gross or net?"}],"consequences":"the invoice total changes"}""");

        Assert.Contains("where is the rate", said, StringComparison.Ordinal);
        Assert.Contains("gross or net?", said, StringComparison.Ordinal);
        Assert.Contains("the invoice total changes", said, StringComparison.Ordinal);
    }
}
