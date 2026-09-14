using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// Two brokers on two connections against one database file.
/// </summary>
/// <remarks>
/// <para>
/// Not the deployment SPEC.md §4.1 describes — that has one broker per project — but it is the arrangement §13.1
/// contemplates when it names the hashing algorithm so that independent implementations sharing a database agree
/// on what a duplicate is. Sharing is admitted, so contention has to resolve into a refusal a participant can
/// read rather than an exception or a half-written exchange.
/// </para>
/// <para>
/// These use a real file, because an in-memory database is private to the connection that opened it and would
/// make two brokers into two universes that could not possibly collide.
/// </para>
/// </remarks>
public sealed class ConcurrentBrokerTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"parley-concurrent-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(_path + suffix);
            }
            catch (IOException)
            {
                // Best effort. The temp directory is not what is under test.
            }
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(Broker One, Broker Two)> TwoBrokersAsync(
        Microsoft.Data.Sqlite.SqliteConnection one, Microsoft.Data.Sqlite.SqliteConnection two)
    {
        Requests.Register(one, Orders, Billing);

        var brokerOne = new Broker(one, BrokerOptions.Default);
        var brokerTwo = new Broker(two, BrokerOptions.Default);
        await brokerOne.StartAsync(Orders, T0, Ct);
        await brokerTwo.StartAsync(Orders, T0, Ct);
        return (brokerOne, brokerTwo);
    }

    private static Task<RequestOutcome> Send(Broker broker) =>
        broker.RequestAsync(Orders, Billing, Requests.Model, Requests.Request(), T0.AddMinutes(1), "pricing", Ct);

    [Fact]
    public async Task The_same_request_sent_at_once_by_both_opens_exactly_one_exchange()
    {
        // busy_timeout makes the loser wait for the lock, and the distinctness index of §7.1 refuses it when it
        // gets there.
        using var one = ParleyDatabase.OpenAndMigrate(_path);
        using var two = ParleyDatabase.OpenAndMigrate(_path);
        var (brokerOne, brokerTwo) = await TwoBrokersAsync(one, two);

        var results = await Task.WhenAll(Task.Run(() => Send(brokerOne)), Task.Run(() => Send(brokerTwo)));

        Assert.Single(results, r => r.Succeeded);
        Assert.Single(results, r => r.Rejection == RequestRejection.Duplicate);
    }

    [Fact]
    public async Task The_loser_of_a_race_writes_no_request()
    {
        using var one = ParleyDatabase.OpenAndMigrate(_path);
        using var two = ParleyDatabase.OpenAndMigrate(_path);
        var (brokerOne, brokerTwo) = await TwoBrokersAsync(one, two);

        await Task.WhenAll(Task.Run(() => Send(brokerOne)), Task.Run(() => Send(brokerTwo)));

        Assert.Single(await new ExchangeRepository(one).GetOpenAsync(cancellationToken: Ct));
        Assert.Single(await new MessageRepository(one).GetPendingAsync(Billing, cancellationToken: Ct));
    }

    [Fact]
    public async Task Two_results_for_one_exchange_close_it_once()
    {
        // Both read inside their own write-locked transaction, so they serialise: the second reads the row the
        // first committed and is refused against a closed exchange. What must not happen is two Results on one
        // exchange, which would put two settlements in the requester's inbox for one piece of work.
        using var one = ParleyDatabase.OpenAndMigrate(_path);
        using var two = ParleyDatabase.OpenAndMigrate(_path);
        var (brokerOne, brokerTwo) = await TwoBrokersAsync(one, two);

        var sent = await Send(brokerOne);
        Assert.True(sent.Succeeded);

        var results = await Task.WhenAll(Task.Run(() => Result(brokerOne)), Task.Run(() => Result(brokerTwo)));

        Assert.Single(results, r => r);
        Assert.Single(results, r => !r);
        Assert.Single((await brokerOne.DrainInboxAsync(Orders, T0.AddMinutes(5), cancellationToken: Ct)).Messages);

        async Task<bool> Result(Broker broker) =>
            (await broker.ResultAsync(sent.Exchange!.Id, Billing, Requests.Model, Requests.Result(), T0.AddMinutes(3), Ct)).Succeeded;
    }
}
