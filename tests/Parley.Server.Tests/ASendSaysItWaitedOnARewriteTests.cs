using Parley.Store;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>That the broker's reply to a send says whether it waited on a rewrite of the store (SPEC.md §4.0, §9.1).</summary>
/// <remarks>
/// Found by the Windows nightlies of 2a55820: the adapter decided it from the attachment's stream, which said a rewrite
/// started and finished on another stream than the reply, and in one slot a finish could overwrite. A send that waited
/// was told nothing. The broker decides it at the gate now and says it in the reply.
/// </remarks>
[Integration]
public sealed class ASendSaysItWaitedOnARewriteTests
{
    private const string Orders = "orders@m1";
    private const string Billing = "billing@m1";
    private const string Inventory = "inventory@m1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("clarify")]
    [InlineData("result")]
    public async Task A_reply_sent_behind_a_rewrite_says_it_waited_and_one_that_was_not_does_not(string act)
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var inventory = await AttachedClient.AttachAsync(broker, Inventory, cancellationToken: Ct);

        var asked = await Ask(orders, "pricing");
        Assert.False(asked.Waited);
        _ = await Ask(inventory, "doomed");

        var exchange = asked.Outcomes.Single().ExchangeId;

        // The broker busy, a destroy that rewrites the store queued behind it, and the reply behind that.
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var hold = broker.Runtime.UseConnectionAsync(
            async (_, _, _) =>
            {
                entered.SetResult();
                await release.Task;
                return 0;
            },
            Ct);
        await entered.Task;

        var destroying = broker.Runtime.UseConnectionAsync(
            (connection, now, ct) => new ConversationRepository(connection).DestroyAsync("doomed", now, cancellationToken: ct),
            Ct);
        var replying = Reply(billing, act, exchange);

        for (var tries = 0; tries < 300 && broker.Runtime.AgentWorkWaiting == 0; tries++)
        {
            await Task.Delay(100, Ct);
        }

        Assert.Equal(1, broker.Runtime.AgentWorkWaiting);

        release.SetResult();
        _ = await hold;
        _ = await destroying;
        var replied = await replying;

        Assert.True(replied.Ok, replied.ToString());
        Assert.True(replied.Waited);

        // Nothing rewritten since: the next send waited on nothing.
        var again = await Ask(orders, "pricing");
        Assert.False(again.Waited);
    }

    private static async Task<Wire.RequestReply> Ask(AttachedClient from, string conversation) =>
        await from.Client.RequestAsync(
            new Wire.OpenRequest
            {
                From = from.Name, To = Billing, ResponderModel = "test",
                PayloadJson = "{\"ask\":\"is it gross or net?\"}", ConversationId = conversation,
            },
            cancellationToken: Ct);

    private static Task<Wire.SayReply> Reply(AttachedClient from, string act, long exchange) =>
        act == "clarify"
            ? from.Client.ClarifyAsync(
                new Wire.ClarifyRequest
                {
                    From = from.Name, ExchangeId = exchange, ResponderModel = "test",
                    PayloadJson = """{"text":"gross of what?"}""",
                },
                cancellationToken: Ct).ResponseAsync
            : from.Client.ResultAsync(
                new Wire.ResultRequest
                {
                    From = from.Name, ExchangeId = exchange, ResponderModel = "test",
                    PayloadJson = ResultPayload,
                },
                cancellationToken: Ct).ResponseAsync;

    private const string ResultPayload = """{"outcomes":[{"id":"request","outcome":"answered","text":"net"}]}""";
}
