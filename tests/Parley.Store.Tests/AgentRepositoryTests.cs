using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

public sealed class AgentRepositoryTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly AgentRepository _agents;

    public AgentRepositoryTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _agents = new AgentRepository(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private Task<int> Register(AgentName name, AgentStatus status = AgentStatus.Offline) =>
        _agents.UpsertAsync(
            new AgentRecord(name, "test", AgentTier.Full, null, null, null, null, status),
            TestContext.Current.CancellationToken);

    /// <summary>What the registry would report for a record, now.</summary>
    /// <param name="record">The stored record.</param>
    /// <returns>The derived status.</returns>
    private static AgentStatus Derived(AgentRecord record) =>
        AgentLiveness.Derive(
            record.Status, record.LastSeenUtc, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));

    private async Task<AgentRecord> Load(AgentName name) =>
        (await _agents.GetAllAsync(TestContext.Current.CancellationToken))
            .Single(a => a.Name == name);

    [Theory]
    [InlineData(AgentStatus.Offline)]
    [InlineData(AgentStatus.Live)]
    [InlineData(AgentStatus.Retired)]
    public async Task Every_storable_status_round_trips(AgentStatus status)
    {
        // The enum is written out as a token so the schema can validate it, which means the
        // formatter and the parser have to agree. A value added to one and not the other
        // compiles cleanly and fails only when a row carrying it is read back.
        //
        // Three rather than five: NotResponding and Unreachable are readings of last_seen_utc
        // and have nothing to round-trip. The test below is what stops them being written.
        await Register(Orders, status);

        Assert.Equal(status, (await Load(Orders)).Status);
    }

    [Theory]
    [InlineData(AgentStatus.NotResponding)]
    [InlineData(AgentStatus.Unreachable)]
    public async Task A_derived_status_cannot_be_stored(AgentStatus derived)
    {
        // Refused where the mistake is made rather than accepted and half-honoured. A stored
        // liveness is a claim nothing withdraws -- which is the whole defect this replaced, and
        // it would come straight back the first time a caller wrote one of these down.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Register(Orders, derived));
    }

    [Theory]
    [InlineData(AgentTier.Full)]
    [InlineData(AgentTier.Pull)]
    [InlineData(AgentTier.Headless)]
    public async Task Every_tier_round_trips(AgentTier tier)
    {
        await _agents.UpsertAsync(
            new AgentRecord(Orders, "test", tier, null, null, null, null, AgentStatus.Offline),
            TestContext.Current.CancellationToken);

        Assert.Equal(tier, (await Load(Orders)).Tier);
    }

    [Fact]
    public async Task Silence_reads_as_unreachable_without_anything_writing_it()
    {
        // The defect this replaced: 'unreachable' used to be a stored value, and the only thing
        // that could write it was a process that had stopped. Read off the timestamp instead and
        // the answer is right whether or not anything is running.
        await Register(Orders);
        GoSilent(Orders);

        Assert.Equal(AgentStatus.Unreachable, Derived(await Load(Orders)));
    }

    [Fact]
    public async Task An_agent_nobody_has_opened_is_offline_rather_than_unreachable()
    {
        // Offline must stay routable -- it is a repository somebody configured and has not
        // opened yet, and refusing traffic to it would refuse the very traffic meant to wake it.
        // Silence for five minutes is a different claim and needs a timestamp to make it.
        await Register(Orders);

        Assert.Null((await Load(Orders)).LastSeenUtc);
        Assert.Equal(AgentStatus.Offline, Derived(await Load(Orders)));
    }

    [Fact]
    public async Task A_missed_sweep_or_two_is_not_yet_a_verdict()
    {
        // Three sweeps rather than one, because a late sweep says nothing about the agent -- a
        // busy machine, a contended write, a moment of suspend -- and a status that flickered on
        // each of those would train everybody to ignore it.
        await Register(Orders);
        await _agents.MarkLiveAsync(Orders, null, T0, TestContext.Current.CancellationToken);

        var sweep = TimeSpan.FromSeconds(5);

        Assert.Equal(
            AgentStatus.Live,
            AgentLiveness.Derive(AgentStatus.Live, T0, T0 + (sweep * 2), sweep));

        Assert.Equal(
            AgentStatus.NotResponding,
            AgentLiveness.Derive(AgentStatus.Live, T0, T0 + (sweep * 4), sweep));
    }

    [Fact]
    public async Task Silence_preserves_the_session_handle()
    {
        // An agent that reappears should resume rather than start cold, so the handle that makes
        // resumption possible must survive falling silent. Nothing writes on the way out now,
        // which makes this easier to guarantee than it was.
        await _agents.UpsertAsync(
            new AgentRecord(Orders, "test", AgentTier.Full, null, "sess-1", null, null, AgentStatus.Offline),
            TestContext.Current.CancellationToken);
        await _agents.MarkLiveAsync(Orders, "sess-1", T0, TestContext.Current.CancellationToken);

        GoSilent(Orders);

        var loaded = await Load(Orders);
        Assert.Equal(AgentStatus.Unreachable, Derived(loaded));
        Assert.Equal("sess-1", loaded.SessionRef);
    }

    [Fact]
    public async Task An_agent_that_comes_back_reads_as_live_again()
    {
        await Register(Orders);
        GoSilent(Orders);

        await _agents.MarkLiveAsync(
            Orders, null, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.Equal(AgentStatus.Live, Derived(await Load(Orders)));
    }

    [Fact]
    public async Task Reloading_a_manifest_does_not_touch_the_heartbeat()
    {
        // Upsert carries configuration, not liveness. There is no mark to clear now; what must
        // survive a reload is the timestamp the derivation reads, because a reload that
        // refreshed it would report every agent live the moment somebody edited a manifest.
        await Register(Orders);
        GoSilent(Orders);

        await Register(Orders);

        Assert.Equal(AgentStatus.Unreachable, Derived(await Load(Orders)));
    }

    [Fact]
    public async Task Marking_all_offline_does_not_spare_the_unreachable()
    {
        // An unreachable mark is a conclusion the broker drew from failing to spawn something,
        // and the agent attaching is the evidence that overturns it. Better evidence than the
        // broker-wide clear this replaced, which reset the mark on agents nobody had heard from
        // — reinstalling the repository is exactly the kind of thing that happens in between,
        // but so is the repository still being gone.
        await Register(Orders);
        await Register(Billing);
        GoSilent(Orders);

        await _agents.MarkLiveAsync(Orders, null, T0, TestContext.Current.CancellationToken);

        Assert.Equal(AgentStatus.Live, (await Load(Orders)).Status);
        Assert.Equal(AgentStatus.Offline, (await Load(Billing)).Status);
    }

    // ---- retirement ----

    [Fact]
    public async Task Retiring_records_the_state()
    {
        await Register(Orders);

        Assert.Equal(1, await _agents.RetireAsync(Orders, TestContext.Current.CancellationToken));
        Assert.Equal(AgentStatus.Retired, (await Load(Orders)).Status);
    }

    [Fact]
    public async Task Retiring_an_unknown_agent_reports_nothing_changed()
    {
        Assert.Equal(0, await _agents.RetireAsync(Orders, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Start_up_does_not_clear_a_retirement()
    {
        // The distinction the status exists for. Every other status is what the previous run
        // observed and a restart may well have invalidated it, so start-up clears them.
        // Retirement is a person saying a repository is gone for good, and a restart is no
        // evidence against that.
        await Register(Orders);
        await Register(Billing);
        await _agents.RetireAsync(Orders, TestContext.Current.CancellationToken);
        GoSilent(Billing);

        // Neither end of the lifecycle disturbs a retirement. Attaching is the closest thing to
        // registration there is, and §14 says registration never clears one.
        await _agents.MarkLiveAsync(Orders, null, T0, TestContext.Current.CancellationToken);
        await _agents.MarkOfflineAsync(Orders, TestContext.Current.CancellationToken);
        await _agents.MarkLiveAsync(Billing, null, T0, TestContext.Current.CancellationToken);
        await _agents.MarkOfflineAsync(Billing, TestContext.Current.CancellationToken);

        Assert.Equal(AgentStatus.Retired, (await Load(Orders)).Status);
        Assert.Equal(AgentStatus.Offline, (await Load(Billing)).Status);
    }

    [Fact]
    public async Task Registering_again_does_not_clear_a_retirement()
    {
        // The reason retirement needs its own counterpart rather than being undone by
        // registration: the registry is reloaded from the manifest at every start-up, so this
        // would silently un-retire the agent on the next restart and every restart after.
        await Register(Orders);
        await _agents.RetireAsync(Orders, TestContext.Current.CancellationToken);

        await Register(Orders);

        Assert.Equal(AgentStatus.Retired, (await Load(Orders)).Status);
    }

    [Fact]
    public async Task Reinstating_returns_a_retired_agent_to_service()
    {
        await Register(Orders);
        await _agents.RetireAsync(Orders, TestContext.Current.CancellationToken);

        Assert.Equal(1, await _agents.ReinstateAsync(Orders, TestContext.Current.CancellationToken));

        // Offline rather than live: nothing has yet demonstrated that this agent can be
        // reached, and offline is the ordinary resting state of one that can.
        Assert.Equal(AgentStatus.Offline, (await Load(Orders)).Status);
    }

    [Fact]
    public async Task Reinstating_an_agent_that_was_not_retired_changes_nothing()
    {
        // Guarded on the current status, so reinstating is not a way to reach into liveness.
        // Silence is an observation and retirement is a judgement; the operator's command
        // withdraws the judgement and has nothing to say about the observation.
        await Register(Orders);
        GoSilent(Orders);

        Assert.Equal(0, await _agents.ReinstateAsync(Orders, TestContext.Current.CancellationToken));
        Assert.Equal(AgentStatus.Unreachable, Derived(await Load(Orders)));
    }

    [Fact]
    public async Task Retiring_preserves_the_row_so_history_survives()
    {
        // §9.4 keeps exchange rows after pruning their message text so telemetry outlives the
        // traffic, and both exchanges and messages are foreign keys to this row. Deleting the
        // agent would cascade to the skeleton that exists to be kept.
        await Register(Orders);
        await _agents.RetireAsync(Orders, TestContext.Current.CancellationToken);

        Assert.Contains(
            await _agents.GetAllAsync(TestContext.Current.CancellationToken),
            a => a.Name == Orders);
    }

    // ---- credentials ----

    [Fact]
    public async Task Credentials_round_trip_by_name()
    {
        await Register(Orders);

        await _agents.SetCredentialAsync(Orders,
            new AgentCredential("api_key", "env:VENDOR_API_KEY"), TestContext.Current.CancellationToken);
        await _agents.SetCredentialAsync(Orders,
            new AgentCredential("org_token", "keychain:parley/org"), TestContext.Current.CancellationToken);

        var creds = await _agents.GetCredentialsAsync(Orders, TestContext.Current.CancellationToken);

        Assert.Equal(
            [new AgentCredential("api_key", "env:VENDOR_API_KEY"),
             new AgentCredential("org_token", "keychain:parley/org")],
            creds);
    }

    [Fact]
    public async Task Setting_a_credential_twice_replaces_the_reference()
    {
        // Rotation moves the secret in its store; if it also moved store, the reference has to
        // follow. Two rows for one name would leave which one wins undefined.
        await Register(Orders);

        await _agents.SetCredentialAsync(Orders,
            new AgentCredential("api_key", "env:OLD"), TestContext.Current.CancellationToken);
        await _agents.SetCredentialAsync(Orders,
            new AgentCredential("api_key", "file:/etc/parley/new.token"), TestContext.Current.CancellationToken);

        var creds = await _agents.GetCredentialsAsync(Orders, TestContext.Current.CancellationToken);

        Assert.Equal("file:/etc/parley/new.token", Assert.Single(creds).Reference);
    }

    [Fact]
    public async Task A_secret_value_cannot_be_stored_as_a_reference()
    {
        // The guarantee SPEC 12 rests on. The schema refuses, so it does not depend on every
        // caller remembering to.
        await Register(Orders);

        await Assert.ThrowsAsync<SqliteException>(() => _agents.SetCredentialAsync(
            Orders, new AgentCredential("api_key", "sk-live-1a2b3c4d"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Removing_a_credential_reports_whether_there_was_one()
    {
        await Register(Orders);
        await _agents.SetCredentialAsync(Orders,
            new AgentCredential("api_key", "env:VENDOR_API_KEY"), TestContext.Current.CancellationToken);

        Assert.True(await _agents.RemoveCredentialAsync(
            Orders, "api_key", TestContext.Current.CancellationToken));
        Assert.False(await _agents.RemoveCredentialAsync(
            Orders, "api_key", TestContext.Current.CancellationToken));
        Assert.Empty(await _agents.GetCredentialsAsync(Orders, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Credentials_are_scoped_to_their_agent()
    {
        await Register(Orders);
        await Register(Billing);
        await _agents.SetCredentialAsync(Orders,
            new AgentCredential("api_key", "env:ORDERS_KEY"), TestContext.Current.CancellationToken);

        Assert.Empty(await _agents.GetCredentialsAsync(Billing, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Reloading_a_manifest_leaves_credentials_alone()
    {
        // Upsert carries configuration. Credentials are not configuration in that sense: they
        // are registered once, and a manifest reload that dropped them would break invocation
        // for every agent on the next start.
        await Register(Orders);
        await _agents.SetCredentialAsync(Orders,
            new AgentCredential("api_key", "env:VENDOR_API_KEY"), TestContext.Current.CancellationToken);

        await Register(Orders);

        Assert.Single(await _agents.GetCredentialsAsync(Orders, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Adapter_configuration_round_trips()
    {
        const string config = """{"model":"claude-opus-5","timeout_ms":30000}""";

        await _agents.UpsertAsync(
            new AgentRecord(Orders, "test", AgentTier.Headless, null, null, config, null, AgentStatus.Offline),
            TestContext.Current.CancellationToken);

        Assert.Equal(config, (await Load(Orders)).AdapterConfig);
    }

    [Fact]
    public async Task A_null_credential_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _agents.SetCredentialAsync(
            Orders, null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Agents_are_listed_in_name_order()
    {
        await Register(Orders);
        await Register(Billing);

        var all = await _agents.GetAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal([Billing, Orders], all.Select(a => a.Name));
    }

    [Fact]
    public async Task A_null_agent_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _agents.UpsertAsync(null!, TestContext.Current.CancellationToken));
    }

    /// <summary>Ages an agent's heartbeat until it reads as unreachable.</summary>
    /// <param name="agent">The agent to silence.</param>
    /// <remarks>
    /// Unreachable is a reading of <c>last_seen_utc</c> now rather than a status anybody writes,
    /// so this is the only way a deployment can produce one: the agent stops checking in. There
    /// is deliberately no method to call instead — a test that could write the status directly
    /// would be testing a path the product does not have.
    /// </remarks>
    private void GoSilent(AgentName agent)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "UPDATE agents SET status = 'live', last_seen_utc = @Then WHERE name = @Name;";
        command.Parameters.AddWithValue(
            "@Then", DateTimeOffset.UtcNow.AddHours(-1).ToString("o"));
        command.Parameters.AddWithValue("@Name", agent.Value);
        command.ExecuteNonQuery();
    }

}
