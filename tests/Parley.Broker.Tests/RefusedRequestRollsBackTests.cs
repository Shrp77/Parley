using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// That a Request storage refuses leaves nothing behind.
/// </summary>
/// <remarks>
/// <para>
/// The payload is one storage refuses for having no ask. The adapter refuses
/// such a payload before sending and storage refuses it on insert, but only the adapter is tested for the first and
/// only the store for the second. Anything else reaching the broker — a surface, another
/// adapter — meets the storage refusal in the middle of an operation that has already opened the exchange. Were that not
/// rolled back with it, the result would be an open exchange with no Request: owed, unanswerable, and holding its
/// conversation open.
/// </para>
/// <para>
/// A refusal takes the exchange, the conversation and the message with it, and the sender's
/// next Request is admitted.
/// </para>
/// </remarks>
[Integration]
public sealed class RefusedRequestRollsBackTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Model = "claude-opus-5";

    /// <summary>A payload storage refuses: it carries no ask (§5.1).</summary>
    private const string NoAsk = """{"text":"move the rate"}""";

    [Fact]
    public async Task A_refused_request_opens_nothing_and_the_next_one_is_admitted()
    {
        var ct = TestContext.Current.CancellationToken;
        using var connection = ParleyDatabase.OpenInMemory();
        var agents = new AgentRepository(connection);

        foreach (var agent in new[] { Orders, Billing })
        {
            await agents.UpsertAsync(
                new AgentRecord(agent, "test", AgentTier.Full, null, null, null, false, AgentStatus.Active), ct);
        }

        var broker = new Broker(connection, BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, ct);

        var refused = await Assert.ThrowsAsync<SqliteException>(() => broker.RequestAsync(
            Orders, Billing, Model, NoAsk, T0, "pricing", ct));

        Assert.Contains("carries an ask", refused.Message, StringComparison.Ordinal);

        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM exchanges;"));
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM messages;"));
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM conversations;"));

        var admitted = await broker.RequestAsync(Orders, Billing, Model, Requests.Request("move the rate to the invoice line"), T0.AddMinutes(1), "pricing", ct);

        Assert.NotNull(admitted.Exchange);
    }
}
