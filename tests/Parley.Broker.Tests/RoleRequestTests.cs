using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// A Request addressed to a role rather than to one agent, per SPEC.md §5.5.
/// </summary>
/// <remarks>
/// The whole design is that this is not a new kind of exchange. N ordinary ones are opened and everything
/// downstream treats them exactly as it would have treated each on its own. So most of what is pinned here is
/// the restraint: that the group binds nothing, that a refusal for one member is not a refusal for the rest, and
/// that every member lands in the same conversation.
/// </remarks>
[Integration]
public sealed class RoleRequestTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly AgentName Docs = AgentName.Create("docs@test");

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly Broker _broker;

    public RoleRequestTests()
    {
        _broker = new Broker(_connection, BrokerOptions.Default);

        // Three machines running the same role: one repository checked out in three places.
        foreach (var name in new[] { "docs@test", "infra@alpha", "infra@beta", "infra@gamma" })
        {
            Insert(name);
        }

        _broker.StartAsync(Docs, T0, Ct).GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<IReadOnlyList<RequestOutcome>> SendToRole(string role, string? conversation = null) =>
        _broker.RequestRoleAsync(Docs, role, "some-model-9", Requests.Request("who owns the domain"), T0.AddMinutes(1), conversation, Ct);

    private void Insert(string name)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO agents (name, adapter, tier, status) VALUES ($name, 'test', 'pull', 'active');";
        command.Parameters.AddWithValue("$name", name);
        command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task A_role_opens_one_exchange_per_agent_under_it()
    {
        var outcomes = await SendToRole("infra");

        Assert.Equal(3, outcomes.Count);
        Assert.All(outcomes, o => Assert.True(o.Succeeded));
        Assert.Equal(3, Scalar<long>("SELECT count(*) FROM exchanges;"));
    }

    [Fact]
    public async Task They_are_bound_by_a_group_and_a_conversation_and_by_nothing_else()
    {
        var outcomes = await SendToRole("infra");

        // One group across all three, so the requester can tell which Results belong together.
        Assert.Single(outcomes.Select(o => o.RequestGroup).Distinct());
        Assert.Equal(1, Scalar<long>("SELECT count(DISTINCT request_group) FROM exchanges WHERE request_group IS NOT NULL;"));

        // One conversation, assigned once before anything opened — including one the broker named.
        Assert.Single(outcomes.Select(o => o.Request!.ConversationId).Distinct());

        // And every one of them is an ordinary open exchange.
        Assert.Equal(3, Scalar<long>("SELECT count(*) FROM exchanges WHERE state = 'OPEN';"));
    }

    [Fact]
    public async Task An_ordinary_request_belongs_to_no_group()
    {
        await _broker.RequestAsync(Docs, AgentName.Create("infra@alpha"), "some-model-9", Requests.Request("just the one"), T0.AddMinutes(1), cancellationToken: Ct);

        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM exchanges WHERE request_group IS NOT NULL;"));
    }

    [Fact]
    public async Task The_requester_is_not_in_its_own_group()
    {
        Insert("docs@alpha");

        Assert.True(Assert.Single(await SendToRole("docs")).Succeeded);
    }

    [Fact]
    public async Task A_role_is_matched_whole_and_never_as_a_prefix()
    {
        // infra is a prefix of infrastructure, so a LIKE 'infra%' would quietly put work in front of agents that
        // answer to something else entirely.
        Insert("infrastructure@alpha");

        Assert.Equal(3, (await SendToRole("infra")).Count);
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM exchanges WHERE receiver LIKE 'infrastructure%';"));
    }

    [Fact]
    public async Task A_role_nobody_answers_to_is_refused_rather_than_returning_nothing()
    {
        // "Asked nobody, all done" reads as success, and a role nothing is registered under is almost always a
        // misspelling.
        var refused = Assert.Single(await SendToRole("infrastructure"));

        Assert.Equal(RequestRejection.NoSuchRole, refused.Rejection);
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM exchanges;"));
    }

    [Fact]
    public async Task A_retired_member_is_not_asked()
    {
        await new AgentRepository(_connection).RetireAsync(AgentName.Create("infra@beta"), Ct);

        Assert.Equal(2, (await SendToRole("infra")).Count);
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM exchanges WHERE receiver = 'infra@beta';"));
    }

    [Fact]
    public async Task One_member_refusing_does_not_silence_the_request_to_the_others()
    {
        // A member already holding this Request is a fact about that pair and says nothing about the rest.
        await _broker.RequestAsync(Docs, AgentName.Create("infra@beta"), "some-model-9", Requests.Request("who owns the domain"), T0, "domains", Ct);

        var outcomes = await SendToRole("infra", "domains");

        Assert.Equal(3, outcomes.Count);
        Assert.Equal(2, outcomes.Count(o => o.Succeeded));
        Assert.Equal(RequestRejection.Duplicate, Assert.Single(outcomes, o => !o.Succeeded).Rejection);
    }

    [Fact]
    public async Task A_member_on_a_machine_that_is_off_is_still_asked()
    {
        // §5.5's central decision. Nothing about infra@gamma is connected, and it is asked anyway, so the missing
        // part of the work is flagged rather than silently absent.
        await SendToRole("infra");

        Assert.Equal(1, Scalar<long>("SELECT count(*) FROM exchanges WHERE receiver = 'infra@gamma';"));
    }
}
