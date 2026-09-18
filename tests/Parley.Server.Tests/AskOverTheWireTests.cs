using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// Protocol 0.7 carried end to end, and what a second implementation gets for writing 0.6.
/// </summary>
/// <remarks>
/// <para>
/// <b>Over a real socket, because the rule being checked is a wire rule.</b> Everything that refuses the old
/// shape is a trigger in the schema, three layers below the client — so a test that called the broker in
/// process would prove the trigger fires and say nothing about whether the refusal reaches a peer as
/// something it can act on, or as a transport fault that looks like a broken deployment.
/// </para>
/// <para>
/// The probes below are written the way SPEC.md §18 tests a version change: by asking what a second
/// implementation observes. One built against 0.6 sends an intent, items and a finish line; it must be
/// refused, and refused on the way in rather than half-admitted.
/// </para>
/// </remarks>
public sealed class AskOverTheWireTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private static Wire.OpenRequest Raw(string from, string to, string payload) =>
        new()
        {
            From = from,
            To = to,
            ResponderModel = "claude-opus-5",
            PayloadJson = payload,
            ConversationId = string.Empty,
        };

    [Fact]
    public async Task A_request_carrying_only_an_ask_is_answered_whole()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/repo-docs", token);
        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/repo-infra", token);

        var sent = Assert.Single((await docs.Client.RequestAsync(
            Raw(docs.Name, infra.Name, """{"ask":"Can the retry policy move to your package?"}"""),
            cancellationToken: token)).Outcomes);

        Assert.True(sent.Ok, sent.Rejection.ToString());

        await infra.WaitForNoticeAsync(Patience);

        var delivered = Assert.Single((await infra.Client.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = infra.Name }, cancellationToken: token)).Messages);

        // The document reaches the receiver exactly as it was written. §13 hands over the stored payload
        // rather than prose assembled from it, and the ask is now the whole of that document.
        using var document = JsonDocument.Parse(delivered.Message.PayloadJson);

        Assert.Equal(["ask"], document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());

        var result = await infra.Client.ResultAsync(
            new Wire.ResultRequest
            {
                From = infra.Name,
                ExchangeId = sent.ExchangeId,
                ResponderModel = "claude-opus-5",
                PayloadJson = new JsonObject
                {
                    ["outcomes"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "request",
                        ["outcome"] = "done",
                        ["text"] = "Moved; it ships in 4.2.",
                    }),
                }.ToJsonString(),
            },
            cancellationToken: token);

        Assert.True(result.Ok, result.Refusal.ToString());

        await docs.WaitForNoticeAsync(Patience);

        var back = Assert.Single((await docs.Client.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = docs.Name }, cancellationToken: token)).Messages);

        Assert.Equal(Wire.MessageType.Result, back.Message.Type);
        Assert.Contains("ships in 4.2", back.Message.PayloadJson, StringComparison.Ordinal);

        // And the requester's own ask comes back beside it, because its context has moved on (§13).
        Assert.Contains("Can the retry policy move", back.Envelope, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"intent":"deciding where tax lives","items":[{"id":"q1","kind":"question","text":"gross or net?"}],"done_when":"the rate is stated as gross or net"}""")]
    [InlineData("""{"ask":""}""")]
    [InlineData("""{"ask":"   "}""")]
    [InlineData("""{"ask":null}""")]
    [InlineData("""{"ask":["gross or net?"]}""")]
    [InlineData("""{"ask":42}""")]
    [InlineData("{}")]
    public async Task A_request_that_does_not_carry_an_ask_is_refused_on_the_way_in(string payload)
    {
        // Each of these is what some second implementation would plausibly send: one built against 0.6, one
        // that trimmed its field to nothing, and three that guessed at the type. A string is what §5.1 says,
        // and a number admitted here would be stored as an ask nobody could read.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/repo-docs", token);
        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/repo-infra", token);

        var refused = await Assert.ThrowsAsync<RpcException>(
            async () => await docs.Client.RequestAsync(Raw(docs.Name, infra.Name, payload), cancellationToken: token));

        Assert.Equal(StatusCode.InvalidArgument, refused.StatusCode);

        // **And nothing was left behind.** A refusal that had already written the exchange would leave one
        // open with no message in it, owed by an agent that was never told — the failure `RefusedRequestRolls
        // BackTests` exists for, checked here at the far end of the wire.
        Assert.Empty((await infra.Client.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = infra.Name }, cancellationToken: token)).Messages);
    }

    [Fact]
    public async Task An_ask_over_the_cap_is_refused_and_the_one_under_it_is_not()
    {
        // Rule 8.4 is unchanged and now governs nearly the whole payload, since the framing is one key.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/repo-docs", token);
        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/repo-infra", token);

        var framing = new JsonObject { ["ask"] = string.Empty }.ToJsonString().Length;
        var room = Parley.Core.MessageLimits.MaxPayloadCharacters - framing;

        var over = await Assert.ThrowsAsync<RpcException>(async () => await docs.Client.RequestAsync(
            Raw(docs.Name, infra.Name, new JsonObject { ["ask"] = new string('a', room + 1) }.ToJsonString()),
            cancellationToken: token));

        Assert.Equal(StatusCode.InvalidArgument, over.StatusCode);

        var exact = Assert.Single((await docs.Client.RequestAsync(
            Raw(docs.Name, infra.Name, new JsonObject { ["ask"] = new string('a', room) }.ToJsonString()),
            cancellationToken: token)).Outcomes);

        Assert.True(exact.Ok, exact.Rejection.ToString());
    }

    [Fact]
    public async Task An_unknown_field_beside_the_ask_is_stored_rather_than_policed()
    {
        // **Deliberate, and the opposite of the probe above.** §9.3 puts invariants in storage; it does not
        // make storage a schema validator for the document. A later version adding a field must not be
        // refused by a broker that has not learned it yet — what is required is that an ask is there.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/repo-docs", token);
        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/repo-infra", token);

        var sent = Assert.Single((await docs.Client.RequestAsync(
            Raw(docs.Name, infra.Name, """{"ask":"who owns this?","urgency":"whenever"}"""),
            cancellationToken: token)).Outcomes);

        Assert.True(sent.Ok, sent.Rejection.ToString());

        await infra.WaitForNoticeAsync(Patience);

        var delivered = Assert.Single((await infra.Client.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = infra.Name }, cancellationToken: token)).Messages);

        Assert.Contains("whenever", delivered.Message.PayloadJson, StringComparison.Ordinal);
    }
}
