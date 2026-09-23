using System.Text.Json;
using Grpc.Core;
using Parley.Client.Seating;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Client.Tests;

/// <summary>
/// What a seat actually puts on the wire under protocol 0.7.
/// </summary>
/// <remarks>
/// <para>
/// <b>The document rather than the sentence it came back with.</b> Every other test of the composer reads the
/// report a send hands the person; none of them reads the payload, so a surface that kept writing an
/// <c>intent</c> beside the ask would pass all of them while storing a shape §5.1 no longer describes — and
/// the store would take it, because storage checks that an ask is there and is deliberately blind to what
/// else a document carries (§9.3).
/// </para>
/// <para>
/// So this asserts the keys, not just the value. An extra key is the failure worth catching: it is invisible
/// on every screen, and it is what a second implementation would read.
/// </para>
/// </remarks>
public sealed class AskShapeTests
{
    private static readonly ProjectSettings Alpha =
        new(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:5403", null, null);

    private static AgentName Orders => AgentName.Create("orders@test");

    [Fact]
    public async Task A_request_carries_the_ask_and_nothing_else()
    {
        var token = TestContext.Current.CancellationToken;
        var agents = new Listening();
        var voice = new SeatVoice(agents, Seated());

        await voice.RequestAsync(Orders, "Can invoices use the price the order supplies?", "pricing", token);

        using var document = JsonDocument.Parse(agents.LastRequest!);

        Assert.Equal(
            ["ask"],
            document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());

        Assert.Equal(
            "Can invoices use the price the order supplies?",
            document.RootElement.GetProperty("ask").GetString());
    }

    [Fact]
    public async Task The_conversation_travels_beside_the_ask_rather_than_inside_it()
    {
        // §5.0 keeps the conversation a property of the message, not of the payload. Folding it into the
        // document would make it something a sender could contradict.
        var token = TestContext.Current.CancellationToken;
        var agents = new Listening();
        var voice = new SeatVoice(agents, Seated());

        await voice.RequestAsync(Orders, "who owns the total?", "pricing", token);

        Assert.Equal("pricing", agents.LastConversation);
        Assert.DoesNotContain("pricing", agents.LastRequest!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ask_of_one_word_is_sent_rather_than_refused()
    {
        // The floor that went with `done_when`. A receiver that cannot scope this sends a Clarify (§5.2);
        // the sender is not the one in a position to say how far it goes, which is the whole argument of 0.7.
        var token = TestContext.Current.CancellationToken;
        var agents = new Listening();
        var voice = new SeatVoice(agents, Seated());

        var said = await voice.RequestAsync(Orders, "why?", "pricing", token);

        Assert.StartsWith("Sent.", said, StringComparison.Ordinal);
        Assert.Equal("""{"ask":"why?"}""", agents.LastRequest);
    }

    [Fact]
    public async Task A_result_answering_a_0_7_request_names_the_one_item_request()
    {
        // The Result side of the same change. A 0.7 Request has no items, so `Delivered.Items()` offers the
        // single spelling §5.3 reserves — and what reaches the wire must be that, under the `outcomes` array
        // the broker has always read.
        var token = TestContext.Current.CancellationToken;
        var agents = new Listening();
        var voice = new SeatVoice(agents, Seated());

        await voice.ResultAsync(7, [(PayloadShape.WholeRequest, "It can: the order carries it.", "done")], token);

        using var document = JsonDocument.Parse(agents.LastResult!);
        var outcome = Assert.Single(document.RootElement.GetProperty("outcomes").EnumerateArray());

        Assert.Equal("request", outcome.GetProperty("id").GetString());
        Assert.Equal("done", outcome.GetProperty("outcome").GetString());
        Assert.Equal("It can: the order carries it.", outcome.GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_result_answering_a_stored_0_6_request_still_names_its_items()
    {
        // The half FR-009 rests on. An exchange opened under 0.6 outlives the upgrade — nothing times out —
        // so the same method has to keep writing the ids that Request declared.
        var token = TestContext.Current.CancellationToken;
        var agents = new Listening();
        var voice = new SeatVoice(agents, Seated());

        await voice.ResultAsync(
            9,
            [("r1", "merged", "done"), ("r2", "schema 12", "answered")],
            token);

        using var document = JsonDocument.Parse(agents.LastResult!);

        Assert.Equal(
            ["r1", "r2"],
            document.RootElement.GetProperty("outcomes").EnumerateArray()
                .Select(o => o.GetProperty("id").GetString())
                .ToArray());
    }

    private static Seat Seated()
    {
        Assert.True(Seat.TryRead("arvind", out var seat, out _));

        return seat;
    }

    /// <summary>A client that accepts everything and keeps what it was given.</summary>
    /// <remarks>
    /// Accepting rather than refusing on purpose: a refusal short-circuits before the payload is assembled in
    /// one of the two paths, and this file is about the document rather than about what comes back.
    /// </remarks>
    private sealed class Listening : Wire.Agents.AgentsClient
    {
        public string? LastRequest { get; private set; }

        public string? LastConversation { get; private set; }

        public string? LastResult { get; private set; }

        public override AsyncUnaryCall<Wire.RequestReply> RequestAsync(
            Wire.OpenRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request.PayloadJson;
            LastConversation = request.ConversationId;

            return Answer(new Wire.RequestReply
            {
                Outcomes =
                {
                    new Wire.RequestOutcome
                    {
                        Ok = true,
                        ExchangeId = 1,
                        Receiver = request.To,
                        ConversationId = request.ConversationId,
                    },
                },
            });
        }

        public override AsyncUnaryCall<Wire.SayReply> ResultAsync(
            Wire.ResultRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            LastResult = request.PayloadJson;

            return Answer(new Wire.SayReply { Ok = true });
        }

        private static AsyncUnaryCall<T> Answer<T>(T reply) =>
            new(Task.FromResult(reply),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });
    }
}
