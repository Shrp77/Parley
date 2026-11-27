using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That a flag reaches both surfaces as the kind the broker derived.
/// </summary>
/// <remarks>
/// <para>
/// Found by the review gate, lens 3 run 3.6. <see cref="BrokerClient.FlagsAsync"/> maps the wire's kind and ends in a
/// fallback to <see cref="FlagKind.NotCollected"/>, so a kind it forgot to map would not fail — it would tell the person
/// "somebody has not collected what somebody sent", about a conversation where everything was collected. The one
/// check was name parity between the two enums, which a missing arm passes.
/// </para>
/// <para>
/// Through a real broker over the wire rather than a stub, so the server's mapping and the client's are held together.
/// </para>
/// </remarks>
[Integration]
public sealed class FlagsOverTheWireTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Model = "some-model-9";

    private const string Request =
        """{"ask":"Where does price live, and is it gross or net?"}""";

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public FlagsOverTheWireTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        var agents = new AgentRepository(_connection);

        foreach (var name in new[] { Orders, Billing })
        {
            agents.UpsertAsync(
                new AgentRecord(name, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _broker = LoopbackOperator.Start(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_vote_flag_arrives_as_a_vote_flag_and_not_as_uncollected_work()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = new Parley.Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, ct);

        var sent = await broker.RequestAsync(Orders, Billing, Model, Request, T0, "pricing", ct);
        Assert.False((await broker.ConcludeAsync("pricing", Orders, finished: true, T0.AddMinutes(1), ct)).Concluded);

        await broker.DrainInboxAsync(Billing, T0.AddMinutes(2), cancellationToken: ct);

        Assert.True((await broker.ResultAsync(
            sent.Exchange!.Id, Billing, Model,
            """{"outcomes":[{"id":"request","outcome":"answered","text":"net"}]}""",
            T0.AddMinutes(3), cancellationToken: ct)).Succeeded);

        var flag = Assert.Single((await new BrokerClient(_broker.Operators).FlagsAsync(ct)).Exchanges);

        Assert.Equal(FlagKind.NotVoted, flag.Kind);
        Assert.Equal(0, flag.ExchangeId);
        Assert.Equal("pricing", flag.ConversationId);
        Assert.Equal(Billing, flag.WaitingOn);
        Assert.Equal(Orders, flag.Waiting);
    }
}
