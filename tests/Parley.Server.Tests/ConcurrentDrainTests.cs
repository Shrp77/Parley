using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That two sessions of one agent draining together each see a message once, and none is lost.
/// </summary>
/// <remarks>
/// <para>
/// <c>ConcurrencyTests</c> probes two sessions of one agent *sending*
/// together, and drains once to count what arrived. This probes the other direction: two sessions
/// draining at the same moment.
/// </para>
/// <para>
/// <b>Both failures matter and they are not the same.</b> A message handed to both drains is a message
/// the agent acts on twice — it answers an ask it has already answered, and §4.2's drain is what marks
/// mail delivered, so nothing downstream would notice. A message handed to neither is mail that is
/// waiting, marked delivered, and will never be read; §14 does not time it out, so it waits for ever.
/// </para>
/// <para>
/// <b>Two sessions of one agent is an ordinary state rather than a contrived one.</b> 004's research
/// records a person seated in a browser and a terminal at once as two attachments for one name, and an
/// adapter reconnecting after a blip is the same shape. The drain is the one operation where that
/// ordinariness costs something.
/// </para>
/// </remarks>
public sealed class ConcurrentDrainTests
{
    private const int Attempts = 15;
    private const int Waiting = 6;

    [Fact]
    public async Task Two_drains_at_the_same_moment_deliver_each_message_exactly_once()
    {
        var token = TestContext.Current.CancellationToken;

        var duplicated = 0;
        var lost = 0;

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            await using var broker = await LoopbackBroker.StartAsync(token);

            await using var orders =
                await AttachedClient.AttachAsync(broker, "orders@alpha", "C:/a", token);
            await using var billing =
                await AttachedClient.AttachAsync(broker, "billing@alpha", "C:/b", token);

            var sent = new List<long>();

            for (var which = 0; which < Waiting; which++)
            {
                sent.Add(
                    Assert.Single(
                        (await orders.Client.RequestAsync(
                            Requests.Open(orders.Name, billing.Name, $"ask {attempt}.{which}"),
                            cancellationToken: token)).Outcomes).ExchangeId);
            }

            // One name, two sessions, draining at the same instant.
            var first = billing.Client.DrainInboxAsync(
                new Wire.DrainInboxRequest { Agent = billing.Name }, cancellationToken: token);

            var second = billing.Client.DrainInboxAsync(
                new Wire.DrainInboxRequest { Agent = billing.Name }, cancellationToken: token);

            var drains = await Task.WhenAll(first, second);

            var handed = drains
                .SelectMany(drain => drain.Messages)
                .Select(held => held.Message.ExchangeId)
                .ToArray();

            if (handed.Length != handed.Distinct().Count())
            {
                duplicated++;
            }

            if (sent.Except(handed).Any())
            {
                lost++;
            }
        }

        Assert.True(
            duplicated == 0,
            $"{duplicated} of {Attempts} runs handed one message to both drains, so the agent would "
            + "answer an ask it had already answered");

        Assert.True(
            lost == 0,
            $"{lost} of {Attempts} runs left a message in neither drain — marked delivered, never read, "
            + "and nothing times it out");
    }
}
