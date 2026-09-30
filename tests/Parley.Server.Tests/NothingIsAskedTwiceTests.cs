using Grpc.Net.Client;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That Parley asks an agent what it owns once, across everything that could make it ask again (SPEC.md §10.2, SC-002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Counted from the conversations the operator can list</b>, over the wire, rather than from a row: a question is
/// something that happened in the deployment, and the listing is where a person would see a second one.
/// </para>
/// <para>
/// <b>The broker restart is a real one</b>, over a store on disk, because "nothing is remembered in memory that the store
/// does not also say" is the claim the design rests on and an in-memory store cannot test it. Watched to fail with the
/// partial unique index dropped and the never-asked rule narrowed to live questions.
/// </para>
/// </remarks>
public sealed class NothingIsAskedTwiceTests
{
    [Fact]
    public async Task Reattaching_and_a_second_session_ask_nothing_more()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using (await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: token))
        {
            _ = await broker.Runtime.FacilitateAsync(token);
        }

        await broker.WaitUntilDetachedAsync("orders@alpha", TimeSpan.FromSeconds(10));

        await using var again = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: token);
        await using var second = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: token);

        _ = await broker.Runtime.FacilitateAsync(token);
        _ = await broker.Runtime.FacilitateAsync(token);

        Assert.Equal(1, await CountQuestionsAsync(broker.ConnectAsOperator(), token));
    }

    [Fact]
    public async Task A_broker_restart_asks_nothing_more()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("parley-asked-once-");

        try
        {
            var store = Path.Combine(directory.FullName, "parley.db");

            for (var run = 0; run < 2; run++)
            {
                var server = await ParleyServer.StartAsync(
                    BrokerRuntime.Open(
                        ProjectName.Create("test"), store, Parley.Broker.BrokerOptions.Default, TimeProvider.System),
                    "http://127.0.0.1:0",
                    token);

                await using (server)
                {
                    using var channel = GrpcChannel.ForAddress(server.Addresses[0]);

                    using var attach = new Wire.Agents.AgentsClient(channel).Attach(
                        new Wire.AttachRequest
                        {
                            Agent = "orders@alpha",
                            Adapter = "test",
                            Tier = Wire.AgentTier.Pull,
                            ProtocolVersion = ProtocolVersion.Current
                        },
                        cancellationToken: token);

                    Assert.True(await attach.ResponseStream.MoveNext(token));

                    _ = await server.Runtime.FacilitateAsync(token);

                    Assert.Equal(1, await CountQuestionsAsync(new Wire.Operators.OperatorsClient(channel), token));
                }
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Two_passes_at_once_ask_once()
    {
        // The gate serialises them, and the index is what holds if it ever did not.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);
        await using var orders = await AttachedClient.AttachAsync(broker, "orders@alpha", cancellationToken: token);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => broker.Runtime.FacilitateAsync(token)));

        Assert.Equal(1, await CountQuestionsAsync(broker.ConnectAsOperator(), token));
    }

    private static async Task<int> CountQuestionsAsync(Wire.Operators.OperatorsClient operators, CancellationToken token)
    {
        var listed = await operators.ListConversationsAsync(
            new Wire.ListConversationsRequest { IncludeArchived = true, PageSize = 100 }, cancellationToken: token);

        return listed.Conversations.Count(c => c.ConversationId.StartsWith(Facilitation.ConversationPrefix, StringComparison.Ordinal));
    }
}
