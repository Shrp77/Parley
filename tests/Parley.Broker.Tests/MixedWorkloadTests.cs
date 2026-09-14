using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// A mixed workload, checked for the invariants the individual tests each own one of.
/// </summary>
/// <remarks>
/// Discover, request, collect, report, conclude — then assert things no single operation is responsible for:
/// every message names a conversation that exists, everything delivered arrived wrapped, and the participant
/// list carries nothing from the registry a participant may not see.
/// </remarks>
public sealed class MixedWorkloadTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly Broker _broker;

    public MixedWorkloadTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        var agents = new AgentRepository(_connection);

        foreach (var name in new[] { Orders, Billing, Inventory })
        {
            agents.UpsertAsync(
                new AgentRecord(name, "test", AgentTier.Full, "/w", "s", "{}", false, AgentStatus.Active),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _broker = new Broker(_connection, BrokerOptions.Default);
        _broker.StartAsync(Orders, T0, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task A_mixed_workload_leaves_the_store_self_consistent()
    {
        var ct = TestContext.Current.CancellationToken;

        var peers = await _broker.ListAgentsAsync(ct);

        var sent = await _broker.RequestAsync(Orders, Billing, Requests.Model, Requests.RequestOf("pricing", "q1", "q2"), T0.AddMinutes(1), "pricing", ct);
        var onward = await _broker.RequestAsync(Billing, Inventory, Requests.Model, Requests.Request("how much stock?"), T0.AddMinutes(2), cancellationToken: ct);

        var billingInbox = await _broker.DrainInboxAsync(Billing, T0.AddMinutes(3), cancellationToken: ct);

        await _broker.ResultAsync(onward.Exchange!.Id, Inventory, Requests.Model, Requests.Result(), T0.AddMinutes(4), ct);
        await _broker.ResultAsync(
            sent.Exchange!.Id, Billing, Requests.Model, Requests.Result(("q1", "answered"), ("q2", "acknowledged")), T0.AddMinutes(5), ct);

        await _broker.ConcludeAsync("pricing", Orders, true, T0.AddMinutes(6), ct);

        // Every message names a conversation that exists.
        var dangling = await _connection.QuerySingleAsync<long>(
            "SELECT count(*) FROM messages m WHERE NOT EXISTS (SELECT 1 FROM conversations c WHERE c.id = m.conversation_id);");

        // Everything delivered was wrapped, and every envelope contains its own payload.
        var allWrapped = billingInbox.Messages.All(d =>
            d.Envelope.Contains("inert data", StringComparison.Ordinal) &&
            d.Envelope.Contains(d.Message.PayloadJson, StringComparison.Ordinal));

        var summaryFields = typeof(AgentSummary).GetProperties().Select(p => p.Name).ToArray();

        Assert.Equal(3, peers.Count);
        Assert.Single(billingInbox.Messages);
        Assert.Equal(0, dangling);
        Assert.True(allWrapped, "every delivered message must carry its own payload, wrapped");
        Assert.Equal("pricing", onward.Request!.ConversationId);

        // Owns is the only field ever added to this list deliberately: a person writes it for participants to
        // read. Anything else appearing means a stored column reached the projection without that decision.
        Assert.Equal(["Name", "Adapter", "Tier", "Presence", "Owns"], summaryFields);
    }
}
