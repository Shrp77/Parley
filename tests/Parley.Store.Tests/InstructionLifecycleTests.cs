using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// An instruction's whole life: swept, pruned, and taken with the agent it named.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three mechanisms now remove or settle an instruction and none of them knows about the
/// others.</b> A sweep settles what nothing reported; retention removes what is old; a purge takes
/// everything belonging to an agent. Each was probed alone when it was built. What has not been
/// probed is the sequences — a row swept and then pruned, a row purged while still running, a
/// readback issued in between.
/// </para>
/// <para>
/// The purge case is the one worth the most. §10.2 says a purge deletes an agent and everything
/// that referred to it, and instructions are a person's own words <i>about</i> an agent that no
/// longer exists. A cascade that missed them would leave those words orphaned in a table whose
/// foreign key says they cannot be.
/// </para>
/// </remarks>
public sealed class InstructionLifecycleTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly RetentionPolicy Policy =
        new(IdlePeriod: TimeSpan.FromDays(3), MaximumAge: TimeSpan.FromDays(30));

    private readonly SqliteConnection _connection;
    private readonly InstructionRepository _instructions;
    private readonly AgentRepository _agents;
    private readonly Pruner _pruner;

    public InstructionLifecycleTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _instructions = new InstructionRepository(_connection);
        _agents = new AgentRepository(_connection);
        _pruner = new Pruner(_connection);

        using var command = _connection.CreateCommand();

        command.CommandText =
            "INSERT INTO nodes (machine, connected, first_seen_utc) "
            + "VALUES ('test', 1, '2026-01-01T00:00:00+00:00') ON CONFLICT DO NOTHING; "
            + "INSERT INTO agents (name, adapter, tier, status) VALUES "
            + "('orders@test','test','full','active'),"
            + "('billing@test','test','full','active');";

        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Purging_an_agent_takes_the_instructions_given_to_it()
    {
        // §10.2: a purge deletes an agent and everything that referred to it. A person's words to an
        // agent that no longer exists refer to nothing.
        var mine = await _instructions.RecordAsync(Orders, "for the one being purged", T0, Ct);
        var theirs = await _instructions.RecordAsync(Billing, "for the one staying", T0, Ct);

        _ = await _instructions.SettleAsync(mine, InstructionOutcome.Taken, "did it", T0.AddMinutes(1), Ct);

        var outcome = await _agents.PurgeAsync(Orders, cancellationToken: Ct);

        Assert.Equal(PurgeVerdict.Gone, outcome.Verdict);

        Assert.Empty(await _instructions.ListAsync(id: mine, cancellationToken: Ct));

        // And only that agent's. A cascade reaching further would delete a person's words about an
        // agent they never mentioned.
        Assert.Single(await _instructions.ListAsync(id: theirs, cancellationToken: Ct));
    }

    [Fact]
    public async Task Purging_takes_an_instruction_that_is_still_running()
    {
        // The row with no settled instant. A delete written around the settled columns would leave
        // exactly this one behind -- the same shape as the retention trap, reached by a different
        // route.
        var running = await _instructions.RecordAsync(Orders, "never answered", T0, Ct);

        _ = await _agents.PurgeAsync(Orders, cancellationToken: Ct);

        Assert.Empty(await _instructions.ListAsync(id: running, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_swept_instruction_is_then_prunable_like_any_other()
    {
        // The sequence, rather than either mechanism alone. A sweep writes `failed` and a settled
        // instant; retention ages from `issued_utc` and must not start caring about the settlement
        // it did not write.
        var id = await _instructions.RecordAsync(Orders, "nothing reported", T0, Ct);

        Assert.Equal(1, await _instructions.SweepAsync(T0.AddMinutes(30), T0.AddMinutes(31), Ct));

        var swept = Assert.Single(await _instructions.ListAsync(id: id, cancellationToken: Ct));

        Assert.Equal(InstructionOutcome.Failed, swept.Outcome);

        var pruned = await _pruner.PruneAsync(Policy, T0.AddDays(31), Ct);

        Assert.Equal(1, pruned.InstructionsRemoved);
        Assert.Empty(await _instructions.ListAsync(id: id, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_readback_after_a_purge_reports_nothing_rather_than_failing_to_parse()
    {
        // The row is gone and the agent's name with it. A listing that still tried to build a name
        // from a deleted row would throw out of the readback -- turning an ordinary empty result
        // into a broken operator surface.
        _ = await _instructions.RecordAsync(Orders, "before the purge", T0, Ct);
        _ = await _instructions.RecordAsync(Billing, "kept", T0, Ct);

        _ = await _agents.PurgeAsync(Orders, cancellationToken: Ct);

        var all = await _instructions.ListAsync(cancellationToken: Ct);

        Assert.Equal(Billing, Assert.Single(all).Agent);
    }

    [Fact]
    public async Task Sweeping_after_everything_is_pruned_is_a_no_op_rather_than_an_error()
    {
        // The empty-table case for both passes. A sweep or a prune that assumed at least one row --
        // by reading a first result, say -- would fail on the quietest deployment there is.
        var id = await _instructions.RecordAsync(Orders, "gone soon", T0, Ct);

        _ = await _instructions.SettleAsync(id, InstructionOutcome.Taken, "done", T0.AddMinutes(1), Ct);
        _ = await _pruner.PruneAsync(Policy, T0.AddDays(31), Ct);

        Assert.Equal(0, await _instructions.SweepAsync(T0.AddDays(31), T0.AddDays(31), Ct));
        Assert.Equal(0, (await _pruner.PruneAsync(Policy, T0.AddDays(32), Ct)).InstructionsRemoved);
        Assert.Empty(await _instructions.ListAsync(cancellationToken: Ct));
    }
}
