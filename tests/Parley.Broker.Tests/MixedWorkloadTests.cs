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
/// Discover, announce, ask, drain, answer, conclude — then assert three things that no single
/// operation is responsible for: every message names a conversation that exists, everything
/// delivered arrived wrapped, and the peer list carries nothing from the registry that a peer
/// may not see. Each of those was a defect at some point in this branch, and each was reachable
/// only by doing several things in sequence.
/// </remarks>
public sealed class MixedWorkloadTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string Model = "claude-opus-5";

    private readonly SqliteConnection _connection;
    private readonly AgentRepository _agents;
    private readonly Broker _broker;

    public MixedWorkloadTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _agents = new AgentRepository(_connection);
        foreach (var name in new[] { Orders, Billing, Inventory })
        {
            _agents.UpsertAsync(
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

        // Discover peers, announce an event, ask a question, answer it, conclude.
        var peers = await _broker.ListAgentsAsync(ct);

        await _broker.NotifyAsync(
            Orders, Billing, Model, """{"text":"OrderLine gained PriceSnapshot"}""",
            T0.AddMinutes(1), "pricing", ct);

        var asked = await _broker.AskAsync(
            Orders, Billing, Model,
            """{"text":"gross or net?","consequences":"If gross I stop adding tax; if net I keep it."}""",
            T0.AddMinutes(2), "pricing", ct);

        var billingInbox = await _broker.DrainInboxAsync(Billing, T0.AddMinutes(3), cancellationToken: ct);

        await _broker.AnswerAsync(
            asked.Exchange!.Id, Billing, Model,
            """{"resolutions":[{"id":"q1","resolution":"answered","text":"net"}]}""",
            T0.AddMinutes(4), cancellationToken: ct);

        await _broker.ConcludeAsync("pricing", Orders, true, T0.AddMinutes(5), ct);

        // Every message names a conversation that exists, or none.
        var orphanedGrouping = await _connection.QuerySingleAsync<long>(
            "SELECT count(*) FROM messages m WHERE m.conversation_id IS NOT NULL " +
            "AND NOT EXISTS (SELECT 1 FROM conversations c WHERE c.id = m.conversation_id);");

        // Everything delivered was wrapped, and every envelope contains its own payload.
        var allWrapped = billingInbox.Messages.All(d =>
            d.Envelope.Contains("inert data", StringComparison.Ordinal) &&
            d.Envelope.Contains(d.Message.PayloadJson, StringComparison.Ordinal));

        // The peer list carried nothing from the registry it should not.
        var summaryFields = typeof(AgentSummary).GetProperties().Select(p => p.Name).ToArray();

        Assert.Equal(3, peers.Count);
        Assert.Equal(2, billingInbox.Messages.Count);
        Assert.Equal(0, orphanedGrouping);
        Assert.True(allWrapped, "every delivered message must carry its own payload, wrapped");
        // Owns is here on purpose and is the only field ever added to this list deliberately:
        // an operator writes it for peers to read. Anything else appearing means a column on
        // the stored record reached the projection without that decision being made.
        Assert.Equal(["Name", "Adapter", "Tier", "Presence", "Owns"], summaryFields);
    }
}
