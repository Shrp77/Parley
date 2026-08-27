using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

public sealed class AgentRepositoryTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly AgentRepository _agents;

    public AgentRepositoryTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _agents = new AgentRepository(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private Task<int> Register(AgentName name, AgentStatus status = AgentStatus.Active) =>
        _agents.UpsertAsync(
            new AgentRecord(name, "test", AgentTier.Full, null, null, null, false, status),
            TestContext.Current.CancellationToken);

    /// <summary>What the registry reports for a record.</summary>
    /// <param name="record">The loaded record.</param>
    /// <returns>Its presence, which the read observed rather than this test computing.</returns>
    /// <remarks>
    /// Kept as a helper even though it is now a property access, because every assertion below
    /// used to call a derivation and the point of the change is that there is no longer one to
    /// call. Inlining it would hide that these tests stopped doing arithmetic.
    /// </remarks>
    private static AgentPresence Reported(AgentRecord record) => record.Presence;

    private async Task<AgentRecord> Load(AgentName name) =>
        (await _agents.GetAllAsync(TestContext.Current.CancellationToken))
            .Single(a => a.Name == name);

    [Theory]
    [InlineData(AgentStatus.Active)]
    [InlineData(AgentStatus.Retired)]
    public async Task Every_storable_status_round_trips(AgentStatus status)
    {
        // The enum is written out as a token so the schema can validate it, which means the
        // formatter and the parser have to agree. A value added to one and not the other
        // compiles cleanly and fails only when a row carrying it is read back.
        //
        // Two rather than five, and the three that left were never really statuses: offline,
        // not-responding and unreachable all described how reachable an agent was, which is now
        // observed on a connection and has nothing to round-trip through storage.
        await Register(Orders, status);

        Assert.Equal(status, (await Load(Orders)).Status);
    }

    [Fact]
    public void A_reachability_reading_is_not_a_status_the_compiler_will_let_you_store()
    {
        // This replaces a theory that tried to store NotResponding and Unreachable and expected
        // a throw. Those values are gone from AgentStatus entirely and live in AgentPresence, so
        // the mistake the theory guarded against no longer compiles -- which is a better guard
        // than a runtime one and worth asserting rather than silently deleting.
        //
        // What is left to check is that the two enums have not quietly grown back together.
        Assert.Equal(
            ["Active", "Retired"],
            Enum.GetNames<AgentStatus>().Order(StringComparer.Ordinal));

        Assert.Equal(
            ["Running", "Startable", "Stranded"],
            Enum.GetNames<AgentPresence>().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Silence_reads_as_unreachable_without_anything_writing_it()
    {
        // The defect this replaced: 'unreachable' used to be a stored value, and the only thing
        // that could write it was a process that had stopped. Read off the timestamp instead and
        // the answer is right whether or not anything is running.
        await Register(Orders);
        Disconnect(Orders);

        Assert.Equal(AgentPresence.Stranded, Reported(await Load(Orders)));
    }

    [Fact]
    public async Task An_agent_nobody_has_opened_is_registered_and_routable()
    {
        // The ordinary state of a repository somebody configured and has not opened. It must
        // stay routable, because refusing traffic to it would refuse the very traffic meant to
        // wake it -- which is the same argument the old `offline` carried, and the one thing
        // about that model that was always right.
        //
        // A registration on its own says nothing about a node, so the read finds no connection
        // anywhere and reports Stranded. That is honest: nothing has told the broker this
        // machine exists yet.
        await Register(Orders);

        var record = await Load(Orders);

        Assert.Equal(AgentStatus.Active, record.Status);
        Assert.Equal(AgentPresence.Stranded, Reported(record));
        Assert.True(AgentLiveness.Routable(record.Status));
    }

    [Fact]
    public async Task Silence_preserves_the_session_handle()
    {
        // An agent that reappears should resume rather than start cold, so the handle that makes
        // resumption possible must survive falling silent. Nothing writes on the way out now,
        // which makes this easier to guarantee than it was.
        await _agents.UpsertAsync(
            new AgentRecord(Orders, "test", AgentTier.Full, null, "sess-1", null, false, AgentStatus.Active),
            TestContext.Current.CancellationToken);
        await _agents.MarkLiveAsync(Orders, "sess-1", T0, TestContext.Current.CancellationToken);

        Disconnect(Orders);

        var loaded = await Load(Orders);
        Assert.Equal(AgentPresence.Stranded, Reported(loaded));
        Assert.Equal("sess-1", loaded.SessionRef);
    }

    [Fact]
    public async Task An_agent_that_comes_back_reads_as_live_again()
    {
        await Register(Orders);
        Disconnect(Orders);

        await _agents.MarkLiveAsync(
            Orders, null, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.Equal(AgentPresence.Running, Reported(await Load(Orders)));
    }

    [Fact]
    public async Task Reloading_a_manifest_does_not_touch_the_heartbeat()
    {
        // Upsert carries configuration, not liveness. There is no mark to clear now; what must
        // survive a reload is the timestamp the derivation reads, because a reload that
        // refreshed it would report every agent live the moment somebody edited a manifest.
        await Register(Orders);
        Disconnect(Orders);

        await Register(Orders);

        Assert.Equal(AgentPresence.Stranded, Reported(await Load(Orders)));
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
        Disconnect(Orders);

        await _agents.MarkLiveAsync(Orders, null, T0, TestContext.Current.CancellationToken);

        Assert.Equal(AgentStatus.Active, (await Load(Orders)).Status);
        Assert.Equal(AgentStatus.Active, (await Load(Billing)).Status);
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
        Disconnect(Billing);

        // Neither end of the lifecycle disturbs a retirement. Attaching is the closest thing to
        // registration there is, and §14 says registration never clears one.
        await _agents.MarkLiveAsync(Orders, null, T0, TestContext.Current.CancellationToken);
        await _agents.MarkOfflineAsync(Orders, TestContext.Current.CancellationToken);
        await _agents.MarkLiveAsync(Billing, null, T0, TestContext.Current.CancellationToken);
        await _agents.MarkOfflineAsync(Billing, TestContext.Current.CancellationToken);

        Assert.Equal(AgentStatus.Retired, (await Load(Orders)).Status);
        Assert.Equal(AgentStatus.Active, (await Load(Billing)).Status);
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
        Assert.Equal(AgentStatus.Active, (await Load(Orders)).Status);
    }

    [Fact]
    public async Task Reinstating_an_agent_that_was_not_retired_changes_nothing()
    {
        // Guarded on the current status, so reinstating is not a way to reach into liveness.
        // Silence is an observation and retirement is a judgement; the operator's command
        // withdraws the judgement and has nothing to say about the observation.
        await Register(Orders);
        Disconnect(Orders);

        Assert.Equal(0, await _agents.ReinstateAsync(Orders, TestContext.Current.CancellationToken));
        Assert.Equal(AgentPresence.Stranded, Reported(await Load(Orders)));
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
            new AgentRecord(Orders, "test", AgentTier.Headless, null, null, config, false, AgentStatus.Active),
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

    /// <summary>Takes an agent's machine off the air, the way a lost node does.</summary>
    /// <param name="agent">The agent to strand.</param>
    /// <remarks>
    /// This used to rewind a heartbeat into the past, because unreachability was arithmetic on a
    /// timestamp and there was no other way to produce one. It is now an event with a writer, so
    /// the test performs the same two writes the broker does and there is nothing to simulate.
    /// </remarks>
    private void Disconnect(AgentName agent)
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
