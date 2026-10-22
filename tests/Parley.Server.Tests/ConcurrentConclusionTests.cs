using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That agreement reached by two agents at the same instant is not lost to the race between them.
/// </summary>
/// <remarks>
/// <para>
/// This asks
/// about the end of §5's lifecycle — whether a conversation can fail to *end* when everybody has agreed
/// it should. Closure by consensus is counted across several callers, so the count is read and written by
/// each of them, and two votes arriving together are the case where a read-then-write loses one.
/// </para>
/// <para>
/// <b>What is asserted is deliberately narrow, and that is a decision rather than timidity.</b> Exactly
/// which participants §14 asks, and which reply carries the closure, are rules this probe does not
/// restate — a probe that encodes a misreading of the protocol reports a defect that is not there, and
/// this repository treats a confident wrong ruling about the protocol as expensive. So it asserts only
/// what must hold on any reading: **no vote is refused, and once every holder has voted Done the
/// conversation is concluded.** A lost vote shows up as a conversation that everybody agreed to end and
/// that never ended.
/// </para>
/// <para>
/// Repeated, because one attempt at a race proves nothing.
/// </para>
/// </remarks>
public sealed class ConcurrentConclusionTests
{
    private const int Attempts = 15;

    [Fact]
    public async Task Two_agents_agreeing_at_the_same_moment_end_the_conversation()
    {
        var token = TestContext.Current.CancellationToken;

        var unconcluded = 0;
        var refused = 0;

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            await using var broker = await LoopbackBroker.StartAsync(token);

            await using var orders =
                await AttachedClient.AttachAsync(broker, "orders@alpha", "C:/a", token);
            await using var billing =
                await AttachedClient.AttachAsync(broker, "billing@alpha", "C:/b", token);
            await using var inventory =
                await AttachedClient.AttachAsync(broker, "inventory@alpha", "C:/c", token);

            var toBilling = Assert.Single(
                (await orders.Client.RequestAsync(
                    Requests.Open(orders.Name, billing.Name, "what is owed"),
                    cancellationToken: token)).Outcomes);

            var conversation = toBilling.ConversationId;

            var toInventory = Assert.Single(
                (await orders.Client.RequestAsync(
                    Requests.Open(
                        orders.Name, inventory.Name, "and what is on the shelf", conversation),
                    cancellationToken: token)).Outcomes);

            _ = await billing.Client.DrainInboxAsync(
                new Wire.DrainInboxRequest { Agent = billing.Name }, cancellationToken: token);

            _ = await inventory.Client.DrainInboxAsync(
                new Wire.DrainInboxRequest { Agent = inventory.Name }, cancellationToken: token);

            Assert.True(
                (await billing.Client.ResultAsync(
                    Answer(billing.Name, toBilling.ExchangeId), cancellationToken: token)).Ok);

            Assert.True(
                (await inventory.Client.ResultAsync(
                    Answer(inventory.Name, toInventory.ExchangeId), cancellationToken: token)).Ok);

            // Both holders agree, at the same instant. Neither is doing anything it is not entitled to.
            var billingVote = billing.Client.ConcludeAsync(
                Vote(billing.Name, conversation), cancellationToken: token);

            var inventoryVote = inventory.Client.ConcludeAsync(
                Vote(inventory.Name, conversation), cancellationToken: token);

            var votes = await Task.WhenAll(billingVote.ResponseAsync, inventoryVote.ResponseAsync);

            if (votes.Any(vote => !vote.Ok))
            {
                refused++;
                continue;
            }

            // The requester agrees too, which settles any question about who else §14 asks — after this
            // nobody is holding anything and nobody has withheld agreement.
            var closing = await orders.Client.ConcludeAsync(
                Vote(orders.Name, conversation), cancellationToken: token);

            if (!closing.Ok)
            {
                refused++;
                continue;
            }

            if (!closing.Concluded)
            {
                unconcluded++;
            }
        }

        Assert.True(
            refused == 0,
            $"{refused} of {Attempts} runs had a vote refused, which no participant had asked for");

        Assert.True(
            unconcluded == 0,
            $"{unconcluded} of {Attempts} conversations were agreed by everybody and never concluded, "
            + "so a vote was lost to the race between two of them");

        static Wire.ConcludeRequest Vote(string from, string conversation) =>
            new() { From = from, ConversationId = conversation, Vote = Wire.Vote.Done };

        static Wire.ResultRequest Answer(string from, long exchangeId) =>
            new()
            {
                From = from,
                ExchangeId = exchangeId,
                ResponderModel = "test",
                PayloadJson = """{"outcomes":[{"id":"request","outcome":"answered","text":"yes"}]}"""
            };
    }
}
