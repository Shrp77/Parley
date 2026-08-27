using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// What every operation does before <see cref="Broker.StartAsync"/> has run.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §14's reboot sequence exists because nothing the previous run believed survives a
/// restart as evidence. Until it has run, the open set still holds exchanges belonging to a
/// process that is gone and the registry still holds liveness that run observed — so an
/// operation deciding against either is deciding against evidence a start invalidates.
/// </para>
/// <para>
/// These were written as probes against the opposite behaviour and passed: an ask before start
/// really was refused as <see cref="OpenRejection.AnswererUnreachable"/> off a stale mark, and
/// really did count a dead run's exchanges against the pair cap. They are kept pointed the
/// other way.
/// </para>
/// </remarks>
public sealed class StartGuardTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Model = "claude-opus-5";

    private const string Question =
        """{"text":"is the price gross or net?","consequences":"If gross I stop adding tax; if net I keep adding it."}""";

    private readonly SqliteConnection _connection;
    private readonly AgentRepository _agents;
    private readonly ExchangeRepository _exchanges;
    private readonly Broker _broker;

    public StartGuardTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _agents = new AgentRepository(_connection);

        foreach (var name in new[] { Orders, Billing })
        {
            _agents.UpsertAsync(
                new AgentRecord(name, "test", AgentTier.Full, null, null, null, false, AgentStatus.Active),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _exchanges = new ExchangeRepository(_connection);

        // Deliberately not started. That is what every test here is about.
        _broker = new Broker(_connection, BrokerOptions.Default);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Asking_before_start_is_refused()
    {
        // The specific damage: a previous run marked billing unreachable, and StartAsync clears
        // that deliberately — a restart is exactly when a deleted or moved repository gets
        // reinstalled, so the mark has to be proved again. Admitted here, the ask comes back
        // AnswererUnreachable, which is the one refusal that hands back no work and tells the
        // asker to stop asking and inform its principal.
        GoSilent(Billing);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _broker.AskAsync(
            Orders, Billing, Model, Question, T0,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_dead_peers_slots_are_reclaimed_by_the_sweep_not_by_attaching()
    {
        // Exchanges left OPEN by a process that died still hold pair slots and wait-for edges.
        // Under the broker-wide sequence, any agent attaching cleared them all — which is
        // exactly the behaviour that made one session orphan another's live work.
        //
        // Scoped to the attaching agent, these are billing's debts and orders attaching does not
        // touch them. What reclaims them is the deadline machinery: their windows expire and the
        // sweep orphans them, which is the same mechanism that handles a peer that stops
        // answering for any other reason.
        for (var i = 0; i < ExchangePolicy.Default.OpenPairCap; i++)
        {
            var stale = await _exchanges.TryOpenAsync(
                Orders, Billing, T0, $"stale-{i}",
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(stale.Succeeded);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => _broker.AskAsync(
            Orders, Billing, Model, Question, T0.AddDays(1),
            cancellationToken: TestContext.Current.CancellationToken));

        await _broker.StartAsync(Orders, T0.AddDays(1), TestContext.Current.CancellationToken);

        // Orders attaching leaves billing's debts alone, so the pair is still saturated.
        var stillFull = await _broker.AskAsync(
            Orders, Billing, Model, Question, T0.AddDays(1),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(OpenRejection.PairSaturated, stillFull.Rejection);

        // The sweep is what reclaims them, and it needs no cooperation from the dead process.
        var swept = await _broker.TickAsync(
            T0.AddDays(1), TestContext.Current.CancellationToken);
        Assert.Equal(ExchangePolicy.Default.OpenPairCap, swept.Exchanges.Orphaned);

        var asked = await _broker.AskAsync(
            Orders, Billing, Model, Question, T0.AddDays(1),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(asked.Succeeded, $"expected admission, got {asked.Rejection}");
    }

    [Fact]
    public async Task Clarifying_before_start_is_refused()
    {
        var stale = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, "stale",
            cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _broker.ClarifyAsync(
            stale.Exchange!.Id, Billing, Model, """{"text":"gross of what?"}""",
            T0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Answering_before_start_is_refused()
    {
        // Not merely untidy: this would close an exchange from a dead run as though it had been
        // worked on, and a closed exchange is terminal, so the reboot sequence never gets to
        // orphan it and no replay reaches it.
        var stale = await _exchanges.TryOpenAsync(
            Orders, Billing, T0, "stale",
            cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _broker.AnswerAsync(
            stale.Exchange!.Id, Billing, Model, """{"resolutions":[]}""",
            T0, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Draining_before_start_is_refused()
    {
        // The drain would hand a live agent a question from a dead run and grant its exchange a
        // fresh working window, which is exactly the deadline nobody is working towards that
        // SPEC.md §14 orphans to remove.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _broker.DrainInboxAsync(
            Billing, T0, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Resuming_before_start_is_refused()
    {
        // Replay is defined against orphans, and the reboot sequence is what creates them. Before
        // it runs there are none, so this would report "nothing to resume" for an agent that
        // owes answers — an empty list being indistinguishable from a correct one is what makes
        // silence the wrong answer here.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _broker.ResumeOwedByAsync(
            Billing, T0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Ticking_before_start_is_refused()
    {
        // Already true before this change; kept here so the guard is covered in one place and a
        // later edit cannot quietly drop one of the six.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _broker.TickAsync(
            T0, TestContext.Current.CancellationToken));
    }

    /// <summary>Takes an agent's machine off the air, the way a lost node does.</summary>
    /// <param name="agent">The agent to strand.</param>
    /// <remarks>
    /// This used to age a heartbeat into the past, because unreachability was arithmetic on a
    /// timestamp and there was no other way to produce one. It is now an observation with a
    /// writer -- the node's connection closed -- so the test performs the same write the broker
    /// does, and there is nothing left to simulate.
    /// </remarks>
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
