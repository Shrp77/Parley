using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// What the two invocation columns hold, checked against the storage rather than the caller.
/// </summary>
/// <remarks>
/// The schema has always kept them apart -- <c>invoke_command</c> is how to start an agent,
/// <c>invoke_when_idle</c> is whether a supervisor may with nobody watching -- and the trigger
/// enforces only that consent implies a command, never the reverse. The repository derived one
/// from the other anyway, which made the first unobtainable without the second.
/// </remarks>
public sealed class InvocationConsentTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders");

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly AgentRepository _agents;

    public InvocationConsentTests() => _agents = new AgentRepository(_connection);

    public void Dispose() => _connection.Dispose();

    private Task<int> Register() =>
        _agents.UpsertAsync(
            new AgentRecord(Orders, "test", AgentTier.Full, null, null, null, null, AgentStatus.Offline),
            TestContext.Current.CancellationToken);

    private (string? Command, long Idle) Stored()
    {
        using var command = _connection.CreateCommand();

        command.CommandText =
            "SELECT invoke_command, invoke_when_idle FROM agents WHERE name = 'orders';";

        using var reader = command.ExecuteReader();

        Assert.True(reader.Read());

        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetInt64(1));
    }

    [Fact]
    public async Task A_command_without_the_grant_is_recorded_and_grants_nothing()
    {
        // The behaviour that was wrong. Recording how to start a repository is not permission to
        // start it unattended, and the column that says so must stay zero -- otherwise the only
        // way to be startable by an operator is to also be startable by nobody.
        await Register();

        await _agents.RefreshInvocationConsentAsync(
            Orders, "claude", invokeWhenIdle: false, TestContext.Current.CancellationToken);

        Assert.Equal(("claude", 0L), Stored());
    }

    [Fact]
    public async Task The_grant_and_the_command_together_are_both_recorded()
    {
        await Register();

        await _agents.RefreshInvocationConsentAsync(
            Orders, "claude", invokeWhenIdle: true, TestContext.Current.CancellationToken);

        Assert.Equal(("claude", 1L), Stored());
    }

    [Fact]
    public async Task A_grant_with_nothing_to_run_is_refused_rather_than_stored()
    {
        // Consent with no command is a candidate that fails on every pass, silently, because a
        // missing command is not an error anywhere else. The schema refuses it by trigger; this
        // checks the repository does not walk into that refusal and abort a launch over a
        // configuration mistake nobody could read from the exception.
        await Register();

        await _agents.RefreshInvocationConsentAsync(
            Orders, null, invokeWhenIdle: true, TestContext.Current.CancellationToken);

        Assert.Equal((null, 0L), Stored());
    }

    [Fact]
    public async Task Withdrawing_the_command_withdraws_everything()
    {
        // Removing the variable and opening the repository once has to be enough. A grant that
        // outlived its command would be a row the supervisor keeps trying and the schema forbids.
        await Register();

        await _agents.RefreshInvocationConsentAsync(
            Orders, "claude", invokeWhenIdle: true, TestContext.Current.CancellationToken);

        await _agents.RefreshInvocationConsentAsync(
            Orders, null, invokeWhenIdle: false, TestContext.Current.CancellationToken);

        Assert.Equal((null, 0L), Stored());
    }

    [Fact]
    public async Task Withdrawing_only_the_grant_leaves_the_agent_startable_by_hand()
    {
        // The case the old shape could not express at all, and the reason any of this changed.
        await Register();

        await _agents.RefreshInvocationConsentAsync(
            Orders, "claude", invokeWhenIdle: true, TestContext.Current.CancellationToken);

        await _agents.RefreshInvocationConsentAsync(
            Orders, "claude", invokeWhenIdle: false, TestContext.Current.CancellationToken);

        Assert.Equal(("claude", 0L), Stored());
    }
}
