using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// Deleting a registration that never traded, per SPEC.md §14.
/// </summary>
/// <remarks>
/// <para>
/// The interesting cases are all about the boundary rather than the delete. §14 argues that an
/// agent's history must outlive the agent, and purge is only defensible while it cannot reach
/// any of that history — so most of what is pinned here is what purge <em>refuses</em>, and the
/// last test pins the fact that the refusal is derived from the schema rather than from a list
/// somebody has to remember to update.
/// </para>
/// <para>
/// Placeholder agents are <c>orders</c>, <c>billing</c> and <c>inventory</c>, per CLAUDE.md.
/// </para>
/// </remarks>
public sealed class PurgeTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");
    private static readonly AgentName Inventory = AgentName.Create("inventory");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly AgentRepository _agents;
    private readonly ExchangeRepository _exchanges;

    public PurgeTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _agents = new AgentRepository(_connection);

        foreach (var name in new[] { Orders, Billing, Inventory })
        {
            _agents.UpsertAsync(
                new AgentRecord(name, "test", AgentTier.Full, null, null, null, null,
                    AgentStatus.Offline),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _exchanges = new ExchangeRepository(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private Task<int> Execute(string sql) =>
        _connection.ExecuteAsync(new CommandDefinition(
            sql, cancellationToken: TestContext.Current.CancellationToken));

    private Task<long> Count(string sql) =>
        _connection.ExecuteScalarAsync<long>(new CommandDefinition(
            sql, cancellationToken: TestContext.Current.CancellationToken));

    private async Task<IReadOnlyList<AgentName>> Registered() =>
        (await _agents.GetAllAsync(TestContext.Current.CancellationToken))
            .Select(a => a.Name).ToList();

    [Fact]
    public async Task A_registration_that_never_traded_is_deleted()
    {
        var outcome = await _agents.PurgeAsync(
            Orders, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(PurgeVerdict.Gone, outcome.Verdict);
        Assert.Empty(outcome.Holds);
        Assert.DoesNotContain(Orders, await Registered());
    }

    [Fact]
    public async Task A_purged_name_can_be_registered_again()
    {
        // The whole point of the operation. Retirement burns the name for good, which is the
        // right trade only when there is a history behind it worth the cost.
        await _agents.PurgeAsync(Orders, cancellationToken: TestContext.Current.CancellationToken);

        await _agents.UpsertAsync(
            new AgentRecord(Orders, "test", AgentTier.Pull, "/src/acme-orders", null, null, null,
                AgentStatus.Offline),
            TestContext.Current.CancellationToken);

        Assert.Contains(Orders, await Registered());
    }

    [Fact]
    public async Task A_purged_repository_registers_itself_again_the_next_time_it_is_opened()
    {
        // The difference from retirement that an operator is choosing between, at the one place
        // it shows: a server registers itself with RegisterIfAbsentAsync at launch, which writes
        // nothing when the name is known. §14 makes that the reason retirement survives a
        // relaunch — a control an agent could clear by restarting is not a control — and a
        // retired agent stays retired below to keep that visible beside this.
        //
        // Purge is the other decision: the registration was a mistake rather than a judgement,
        // so the name is absent and a repository that comes back is simply new again. That is
        // what makes purge the operation for clearing a registry somebody intends to rebuild.
        await _agents.RetireAsync(Billing, TestContext.Current.CancellationToken);
        await _agents.PurgeAsync(Orders, cancellationToken: TestContext.Current.CancellationToken);

        foreach (var name in new[] { Orders, Billing })
        {
            await _agents.RegisterIfAbsentAsync(
                new AgentRecord(name, "mcp", AgentTier.Headless, "/src/acme", null, null, null,
                    AgentStatus.Offline),
                TestContext.Current.CancellationToken);
        }

        var reloaded = await _agents.GetAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AgentTier.Headless, reloaded.Single(a => a.Name == Orders).Tier);
        Assert.Equal(AgentStatus.Retired, reloaded.Single(a => a.Name == Billing).Status);
    }

    [Fact]
    public async Task A_name_that_was_never_registered_is_reported_as_such()
    {
        // Distinct from a refusal, because the operator's next move differs: this one is
        // almost always a typo, and offering "it has history" for a name that does not exist
        // would send somebody looking for traffic that was never there.
        var outcome = await _agents.PurgeAsync(
            AgentName.Create("shipping"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(PurgeVerdict.NotRegistered, outcome.Verdict);
        Assert.Empty(outcome.Holds);
    }

    [Fact]
    public async Task An_agent_that_has_asked_something_is_held()
    {
        await _exchanges.TryOpenAsync(
            Orders, Billing, T0, "q1", cancellationToken: TestContext.Current.CancellationToken);

        var outcome = await _agents.PurgeAsync(
            Orders, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(PurgeVerdict.Held, outcome.Verdict);
        Assert.Contains(Orders, await Registered());
        Assert.Contains(
            outcome.Holds,
            hold => hold.Table == "exchanges" && hold.Column == "asker" && hold.Rows == 1);
    }

    [Fact]
    public async Task An_agent_that_was_asked_something_is_held()
    {
        // The counterparty side matters on its own. An agent that never spoke still appears in
        // somebody else's record of having tried, and deleting it would take that with it.
        await _exchanges.TryOpenAsync(
            Orders, Billing, T0, "q1", cancellationToken: TestContext.Current.CancellationToken);

        var outcome = await _agents.PurgeAsync(
            Billing, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(PurgeVerdict.Held, outcome.Verdict);
        Assert.Contains(
            outcome.Holds,
            hold => hold.Table == "exchanges" && hold.Column == "answerer");
    }

    [Fact]
    public async Task A_hold_names_every_column_that_references_the_agent()
    {
        // Reported per column rather than per table, because an agent that both asked and was
        // owed an answer is named twice by one table and the two facts are different.
        await _exchanges.TryOpenAsync(
            Orders, Billing, T0, "q1", cancellationToken: TestContext.Current.CancellationToken);

        var outcome = await _agents.PurgeAsync(
            Billing, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { "answerer", "owed_by" },
            outcome.Holds.Where(h => h.Table == "exchanges").Select(h => h.Column).ToArray());
    }

    [Fact]
    public async Task An_uninvolved_agent_is_still_purgeable_while_others_trade()
    {
        // The refusal is about this agent's own references, not about whether the deployment
        // has been used. Otherwise the first exchange anywhere would freeze the registry.
        await _exchanges.TryOpenAsync(
            Orders, Billing, T0, "q1", cancellationToken: TestContext.Current.CancellationToken);

        var outcome = await _agents.PurgeAsync(
            Inventory, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(PurgeVerdict.Gone, outcome.Verdict);
    }

    [Fact]
    public async Task Retirement_neither_permits_nor_prevents_a_purge()
    {
        // Status is not the test. A retired agent that never traded is exactly the mistake
        // this operation exists to undo — somebody retired a typo because retirement was the
        // only thing available.
        await _agents.RetireAsync(Orders, TestContext.Current.CancellationToken);

        var outcome = await _agents.PurgeAsync(
            Orders, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(PurgeVerdict.Gone, outcome.Verdict);
    }

    [Fact]
    public async Task An_agent_s_own_credentials_go_with_it()
    {
        // Declared ON DELETE CASCADE, which is the schema saying these rows belong to the
        // agent rather than to a counterparty. A credential reference for an agent that no
        // longer exists is not history anybody can use.
        await _agents.SetCredentialAsync(
            Orders,
            new AgentCredential("token", "env:ORDERS_TOKEN"),
            TestContext.Current.CancellationToken);

        var outcome = await _agents.PurgeAsync(
            Orders, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(PurgeVerdict.Gone, outcome.Verdict);
        Assert.Equal(0, await Count("SELECT count(*) FROM agent_credentials WHERE agent = 'orders';"));
    }

    [Fact]
    public async Task A_reference_the_code_has_never_heard_of_still_holds_an_agent()
    {
        // The load-bearing test. Purge decides what "never traded" means by asking the schema
        // which columns reference agents(name), and the reason it asks rather than carrying a
        // list is that a list goes stale at the next migration — silently, and in the direction
        // that deletes history.
        //
        // A table created here and nowhere else is the strongest available statement of that:
        // nothing in src has heard of it, so a scan that finds it cannot be finding it from a
        // list. Pinning the real tables instead would only assert that today's list matches
        // today's schema, which is the thing that stops being true later.
        await Execute(
            """
            CREATE TABLE later_migration (
                id    INTEGER PRIMARY KEY,
                agent TEXT NOT NULL REFERENCES agents(name)
            );
            """);

        await Execute("INSERT INTO later_migration (agent) VALUES ('inventory');");

        var outcome = await _agents.PurgeAsync(
            Inventory, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(PurgeVerdict.Held, outcome.Verdict);
        Assert.Contains(
            outcome.Holds,
            hold => hold.Table == "later_migration" && hold.Column == "agent");
    }

    [Fact]
    public async Task A_later_reference_that_cascades_goes_with_the_agent()
    {
        // The other half of the same decision, and the reason the scan filters on ON DELETE
        // CASCADE rather than on every reference. A cascading declaration is the schema saying
        // those rows belong to the agent; refusing to purge because of them would mean an agent
        // that never traded could still be held by its own belongings.
        await Execute(
            """
            CREATE TABLE later_belongings (
                id    INTEGER PRIMARY KEY,
                agent TEXT NOT NULL REFERENCES agents(name) ON DELETE CASCADE
            );
            """);

        await Execute("INSERT INTO later_belongings (agent) VALUES ('inventory');");

        var outcome = await _agents.PurgeAsync(
            Inventory, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(PurgeVerdict.Gone, outcome.Verdict);
        Assert.Equal(0, await Count("SELECT count(*) FROM later_belongings;"));
    }

    [Fact]
    public async Task The_foreign_keys_remain_the_backstop_under_the_check()
    {
        // If the scan above were ever wrong, this is what stops a purge taking the traffic with
        // it: the delete fails rather than succeeding quietly. Asserted directly because the
        // pragma is a connection setting that defaults to off, so a connection opened without
        // ParleyDatabase would disable it and nothing else here would notice.
        await _exchanges.TryOpenAsync(
            Orders, Billing, T0, "q1", cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<SqliteException>(() =>
            Execute("DELETE FROM agents WHERE name = 'orders';"));
    }
}
