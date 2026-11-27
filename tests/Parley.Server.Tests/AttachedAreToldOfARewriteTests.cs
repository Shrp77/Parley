using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>That whoever is attached is told when the broker rewrites its store and when it has finished (SPEC.md §4.0, §9.1).</summary>
/// <remarks>
/// Found by review, lens 1 run 115: a rewrite holds every operation behind the broker's one gate, seconds on a large store,
/// and the owner decided that nothing is refused meanwhile and everyone attached is told, so a wait reads as a wait.
/// </remarks>
[Integration]
public sealed class AttachedAreToldOfARewriteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("destroy")]
    [InlineData("purge")]
    public async Task Every_session_hears_a_rewrite_start_and_finish(string act)
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@m1", cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@m1", cancellationToken: Ct);
        await using (var leaving = await AttachedClient.AttachAsync(broker, "inventory@m1", cancellationToken: Ct))
        {
            _ = await leaving.Client.RequestAsync(
                new Wire.OpenRequest
                {
                    From = "inventory@m1", To = "billing@m1", ResponderModel = "test",
                    PayloadJson = "{\"ask\":\"is it gross or net?\"}", ConversationId = "pricing",
                },
                cancellationToken: Ct);
        }

        var operators = broker.ConnectAsOperator();

        if (act == "destroy")
        {
            _ = await operators.DestroyAsync(new Wire.ConversationRequest { ConversationId = "pricing" }, cancellationToken: Ct);
        }
        else
        {
            _ = await operators.RetireAsync(new Wire.AgentRequest { Agent = "inventory@m1" }, cancellationToken: Ct);
            _ = await operators.PurgeAsync(new Wire.AgentRequest { Agent = "inventory@m1" }, cancellationToken: Ct);
        }

        foreach (var attached in new[] { orders, billing })
        {
            for (var tries = 0; tries < 50 && Count(attached) < 2; tries++)
            {
                await Task.Delay(100, Ct);
            }

            lock (attached.Tidyings)
            {
                Assert.Equal([true, false], attached.Tidyings);
            }
        }
    }

    private static int Count(AttachedClient attached)
    {
        lock (attached.Tidyings)
        {
            return attached.Tidyings.Count;
        }
    }
}
