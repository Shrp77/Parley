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

    private Task<int> Register(AgentName name, AgentStatus status = AgentStatus.Registered) =>
        _agents.UpsertAsync(
            new AgentRecord(name, "test", AgentTier.Full, null, null, null, null, status),
            TestContext.Current.CancellationToken);

    private async Task<AgentRecord> Load(AgentName name) =>
        (await _agents.GetAllAsync(TestContext.Current.CancellationToken))
            .Single(a => a.Name == name);

    [Theory]
    [InlineData(AgentStatus.Registered)]
    [InlineData(AgentStatus.Live)]
    [InlineData(AgentStatus.Offline)]
    [InlineData(AgentStatus.Unreachable)]
    public async Task Every_status_round_trips(AgentStatus status)
    {
        // The enum is written out as a token so the schema can validate it, which means the
        // formatter and the parser have to agree. A value added to one and not the other
        // compiles cleanly and fails only when a row carrying it is read back.
        await Register(Orders, status);

        Assert.Equal(status, (await Load(Orders)).Status);
    }

    [Theory]
    [InlineData(AgentTier.Full)]
    [InlineData(AgentTier.Pull)]
    [InlineData(AgentTier.Headless)]
    public async Task Every_tier_round_trips(AgentTier tier)
    {
        await _agents.UpsertAsync(
            new AgentRecord(Orders, "test", tier, null, null, null, null, AgentStatus.Registered),
            TestContext.Current.CancellationToken);

        Assert.Equal(tier, (await Load(Orders)).Tier);
    }

    [Fact]
    public async Task Marking_unreachable_records_the_state()
    {
        await Register(Orders);

        Assert.Equal(1, await _agents.MarkUnreachableAsync(
            Orders, TestContext.Current.CancellationToken));
        Assert.Equal(AgentStatus.Unreachable, (await Load(Orders)).Status);
    }

    [Fact]
    public async Task Marking_an_unknown_agent_unreachable_reports_nothing_changed()
    {
        Assert.Equal(0, await _agents.MarkUnreachableAsync(
            Orders, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Marking_unreachable_preserves_the_session_handle()
    {
        // An agent that reappears should resume rather than start cold, so the handle that
        // makes resumption possible must survive being marked unreachable.
        await _agents.UpsertAsync(
            new AgentRecord(Orders, "test", AgentTier.Full, null, "sess-1", null, null, AgentStatus.Registered),
            TestContext.Current.CancellationToken);
        await _agents.MarkLiveAsync(Orders, "sess-1", T0, TestContext.Current.CancellationToken);

        await _agents.MarkUnreachableAsync(Orders, TestContext.Current.CancellationToken);

        var loaded = await Load(Orders);
        Assert.Equal(AgentStatus.Unreachable, loaded.Status);
        Assert.Equal("sess-1", loaded.SessionRef);
    }

    [Fact]
    public async Task An_agent_that_comes_back_leaves_the_unreachable_state()
    {
        await Register(Orders);
        await _agents.MarkUnreachableAsync(Orders, TestContext.Current.CancellationToken);

        await _agents.MarkLiveAsync(Orders, null, T0, TestContext.Current.CancellationToken);

        Assert.Equal(AgentStatus.Live, (await Load(Orders)).Status);
    }

    [Fact]
    public async Task Reloading_a_manifest_does_not_clear_an_unreachable_mark()
    {
        // Upsert carries configuration, not liveness. A manifest reload that reset the mark
        // would resume routing to a repository nobody has checked is back.
        await Register(Orders);
        await _agents.MarkUnreachableAsync(Orders, TestContext.Current.CancellationToken);

        await Register(Orders);

        Assert.Equal(AgentStatus.Unreachable, (await Load(Orders)).Status);
    }

    [Fact]
    public async Task Marking_all_offline_does_not_spare_the_unreachable()
    {
        // Start-up clears stale liveness, and an unreachable mark is liveness the previous run
        // recorded. Sparing it would carry a judgement across a restart that the restart may
        // well have invalidated — reinstalling the repository is exactly the kind of thing that
        // happens between runs.
        await Register(Orders);
        await Register(Billing);
        await _agents.MarkUnreachableAsync(Orders, TestContext.Current.CancellationToken);

        await _agents.MarkAllOfflineAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AgentStatus.Offline, (await Load(Orders)).Status);
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
            new AgentCredential("api_key", "keychain:parley/new"), TestContext.Current.CancellationToken);

        var creds = await _agents.GetCredentialsAsync(Orders, TestContext.Current.CancellationToken);

        Assert.Equal("keychain:parley/new", Assert.Single(creds).Reference);
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
            new AgentRecord(Orders, "test", AgentTier.Headless, null, null, config, null, AgentStatus.Registered),
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
}
