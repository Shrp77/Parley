using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// §10.1's <c>agents()</c>, and what the broker does with a name it does not know.
/// </summary>
/// <remarks>
/// <c>agents()</c> is the only operation that tells a participant anything about another, which makes what it
/// withholds as much the subject as what it returns.
/// </remarks>
[Integration]
public sealed class AgentsTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly AgentRepository _agents;
    private readonly Broker _broker;

    public AgentsTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _agents = new AgentRepository(_connection);

        foreach (var name in new[] { Orders, Billing, Inventory })
        {
            _agents.UpsertAsync(
                new AgentRecord(name, "test-adapter", AgentTier.Full,
                    Workspace: "/home/someone/secret-repo",
                    SessionRef: "session-abc123",
                    AdapterConfig: """{"endpoint":"https://internal.example/v1"}""",
                    Connected: false,
                    Status: AgentStatus.Active),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _broker = new Broker(_connection, BrokerOptions.Default);
        _broker.StartAsync(Orders, T0, Ct).GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Listing_agents_says_who_can_be_asked_and_nothing_more()
    {
        var listed = await _broker.ListAgentsAsync(Ct);

        Assert.Equal(3, listed.Count);

        var billing = Assert.Single(listed, a => a.Name == Billing);
        Assert.Equal("test-adapter", billing.Adapter);
        Assert.Equal(AgentTier.Full, billing.Tier);

        // The registry also holds a workspace path, a session handle and the adapter's own configuration.
        // None has anywhere to go in this type, which is the point: rule 3 says never describe your internals.
        var exposed = typeof(AgentSummary).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain("Workspace", exposed);
        Assert.DoesNotContain("SessionRef", exposed);
        Assert.DoesNotContain("AdapterConfig", exposed);
    }

    [Fact]
    public async Task An_unreachable_agent_is_listed_and_a_retired_one_is_not()
    {
        // An agent nobody has open is where most agents are most of the time, and a Request is what starts it.
        // A retired one would only be refused, so listing it teaches the sender nothing and costs it a turn.
        GoSilent(Billing);
        await _agents.RetireAsync(Inventory, Ct);

        var listed = await _broker.ListAgentsAsync(Ct);

        Assert.Contains(listed, a => a.Name == Billing);
        Assert.DoesNotContain(listed, a => a.Name == Inventory);
    }

    [Fact]
    public async Task A_reinstated_agent_is_listed_again()
    {
        await _agents.RetireAsync(Inventory, Ct);
        await _agents.ReinstateAsync(Inventory, Ct);

        Assert.Contains(await _broker.ListAgentsAsync(Ct), a => a.Name == Inventory);
    }

    [Fact]
    public async Task Listing_agents_before_start_is_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => new Broker(_connection).ListAgentsAsync(Ct));
    }

    [Fact]
    public async Task An_unknown_receiver_is_refused_rather_than_thrown()
    {
        // An ordinary mistake — a typo, a name remembered from before a re-registration — gets a refusal the
        // sender can act on, and agents() is what it acts on.
        var sent = await _broker.RequestAsync(
            Orders, AgentName.Create("shipping@test"), Requests.Model, Requests.Request(), T0.AddMinutes(1), cancellationToken: Ct);

        Assert.Equal(RequestRejection.ReceiverNotRegistered, sent.Rejection);
    }

    [Fact]
    public async Task An_adapter_configured_with_an_unregistered_identity_is_told_so()
    {
        // A deployment fault rather than an agent one, which is why it throws where an unknown receiver is
        // refused: an agent cannot register itself, so there is nothing for it to act on.
        var nobody = AgentName.Create("nobody@test");

        var sending = await Assert.ThrowsAsync<InvalidOperationException>(() => _broker.RequestAsync(
            nobody, Billing, Requests.Model, Requests.Request(), T0.AddMinutes(1), cancellationToken: Ct));

        Assert.Contains("nobody", sending.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_may_not_claim_the_brokers_identity()
    {
        // §11.3: a forged parley:broker would make work abandoned by an agent read as closed by a person.
        await Assert.ThrowsAsync<ArgumentException>(() => _broker.RequestAsync(
            Orders, Billing, Responder.Broker, Requests.Request(), T0.AddMinutes(1), cancellationToken: Ct));
    }

    [Fact]
    public async Task Sending_before_start_is_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => new Broker(_connection).RequestAsync(
            Orders, Billing, Requests.Model, Requests.Request(), T0, cancellationToken: Ct));
    }

    /// <summary>Takes an agent's machine off the air, the way a lost node does.</summary>
    private void GoSilent(AgentName agent)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            UPDATE agents SET connected = 0 WHERE name = @Name;
            UPDATE nodes  SET connected = 0
             WHERE machine = (SELECT machine FROM agents WHERE name = @Name);
            """;
        command.Parameters.AddWithValue("@Name", agent.Value);
        command.ExecuteNonQuery();
    }
}
