using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// What the rest of the deployment sees after the person purges an agent, per SPEC.md §14.
/// </summary>
/// <remarks>
/// The store tests fix what purge deletes. This fixes the part a person actually feels: nothing stops a purge
/// landing while other servers are live and still holding the name, and a Request aimed at the gap must not
/// surface as a fault in somebody else's session.
/// </remarks>
public sealed class PurgedAgentTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly AgentRepository _agents;
    private readonly Broker _broker;

    public PurgedAgentTests()
    {
        Requests.Register(_connection, Orders, Billing);
        _agents = new AgentRepository(_connection);
        _broker = new Broker(_connection, BrokerOptions.Default);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_request_aimed_at_a_purged_agent_is_refused_the_ordinary_way()
    {
        // The answer has to be the refusal every other absence produces — not a foreign key error surfacing as a
        // crash in somebody else's tool call.
        await _broker.StartAsync(Orders, T0, Ct);

        Assert.Equal(PurgeVerdict.Gone, (await _agents.PurgeAsync(Billing, cancellationToken: Ct)).Verdict);

        var sent = await _broker.RequestAsync(Orders, Billing, "some-model-9", Requests.Request(), T0.AddMinutes(1), cancellationToken: Ct);

        Assert.Equal(RequestRejection.ReceiverNotRegistered, sent.Rejection);
        Assert.Null(sent.Exchange);
    }
}
