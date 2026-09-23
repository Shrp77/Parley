using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a message past the cap is refused whatever type it is, including the one nothing else probes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Review gate, lens 2.</b> The cap is a security control rather than a style preference — a
/// context-poisoning defence — which is why it lives in the schema, where no code path can route around
/// it. A type that slipped through would be the whole defence with a door in it.
/// </para>
/// <para>
/// <b>Clarify is the reason this exists.</b> A Request past the cap is covered by
/// <c>AskOverTheWireTests</c>, a Result by <c>AgentVerbTests</c> and an instruction's reply by
/// <c>InstructionReportGuardTests</c>. A Clarify was covered nowhere over the wire, and it is the type an
/// agent sends most freely: §5.2 leaves clarification depth uncapped by default, so it is the one an
/// agent can send most of.
/// </para>
/// <para>
/// <b>What this deliberately does not assert, and why that is a correction rather than a gap.</b> A first
/// version required a <c>size_refusals</c> row per refusal, on the grounds that SPEC.md §17 question 16
/// reads that table as the evidence the cap is set from. That encodes a misreading: <c>RecordRefusal</c>
/// is the *adapter's* act — its own proto comment says "records that an adapter refused to send something
/// for being over the size cap" — so the evidence is gathered where a message is composed, and storage is
/// the backstop for anything that arrives anyway. Asserting the backstop also records would have reported
/// a defect that is not there.
/// </para>
/// <para>
/// <b>Equally deliberately not asserted: that a caller cannot record a refusal in another agent's name.</b>
/// It can, and §16 concedes exactly that — "anything able to reach the broker can claim to be any agent,
/// read its mail, and answer in its name". Probing it would report the threat model as a defect.
/// </para>
/// </remarks>
public sealed class CapHoldsForEveryTypeTests
{
    /// <summary>Comfortably past the cap, so nothing turns on framing arithmetic.</summary>
    private const int Beyond = 1_000;

    [Fact]
    public async Task A_message_past_the_cap_is_refused_whether_it_is_a_request_a_clarify_or_a_result()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", token);
        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/b", token);

        // One that fits, so the other two have an exchange to be sent against — and so a run where
        // nothing worked at all cannot pass by refusing everything.
        var sent = Assert.Single(
            (await docs.Client.RequestAsync(
                Requests.Open(docs.Name, infra.Name, "who owns dns"),
                cancellationToken: token)).Outcomes);

        Assert.True(sent.Ok, "the request that fits was refused, so this probe measured nothing");

        var tooMuch = new string('x', MessageLimits.MaxPayloadCharacters + Beyond);

        await RefusedAsync(
            "Request",
            async () => Assert.Single((await docs.Client.RequestAsync(
                new Wire.OpenRequest
                {
                    From = docs.Name,
                    To = infra.Name,
                    ResponderModel = "test",
                    PayloadJson = Requests.Payload(tooMuch),
                    ConversationId = string.Empty
                },
                cancellationToken: token)).Outcomes).Ok);

        await RefusedAsync(
            "Clarify",
            async () => (await infra.Client.ClarifyAsync(
                new Wire.ClarifyRequest
                {
                    From = infra.Name,
                    ExchangeId = sent.ExchangeId,
                    ResponderModel = "test",
                    PayloadJson = $$"""{"question":"{{tooMuch}}"}"""
                },
                cancellationToken: token)).Ok);

        await RefusedAsync(
            "Result",
            async () => (await infra.Client.ResultAsync(
                new Wire.ResultRequest
                {
                    From = infra.Name,
                    ExchangeId = sent.ExchangeId,
                    ResponderModel = "test",
                    PayloadJson =
                        $$"""{"outcomes":[{"id":"request","outcome":"answered","text":"{{tooMuch}}"}]}"""
                },
                cancellationToken: token)).Ok);

        // The exchange is still open and still answerable, which is the half that would matter to the
        // agent holding it: a refused oversize message must not have consumed the turn.
        Assert.True(
            (await infra.Client.ResultAsync(
                Requests.Result(infra.Name, sent.ExchangeId), cancellationToken: token)).Ok,
            "after three refusals for size, the exchange could no longer be answered at all");
    }

    /// <summary>Asserts a send was refused, by either of the two shapes a refusal takes.</summary>
    /// <param name="type">Which message type, for the failure message.</param>
    /// <param name="send">The send.</param>
    /// <returns>A task that completes when the send has been made and judged.</returns>
    /// <remarks>
    /// A refusal arrives either as <c>Ok = false</c> or, where storage is what refuses, as
    /// <c>InvalidArgument</c> — <c>Requests</c> records that second shape. Both are refusals; which one a
    /// given type produces is not what this file is about, and pinning it would make this fail the day a
    /// refusal moved earlier, which is the direction anybody would want it to move.
    /// </remarks>
    private static async Task RefusedAsync(string type, Func<Task<bool>> send)
    {
        try
        {
            Assert.False(await send(), $"a {type} past the cap was admitted");
        }
        catch (Grpc.Core.RpcException refused)
            when (refused.StatusCode == Grpc.Core.StatusCode.InvalidArgument)
        {
        }
    }
}
