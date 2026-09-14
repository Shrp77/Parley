using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// Deleting an agent and everything it was party to, per SPEC.md §14.
/// </summary>
/// <remarks>
/// <para>
/// Purge refuses nothing. It removes the agent, its traffic, and every conversation it took part
/// in — including the halves belonging to peers who did nothing wrong, because a conversation
/// with one party deleted out of it is not a smaller record but a misleading one. Retirement is
/// the command for an agent whose history matters.
/// </para>
/// <para>
/// So what is pinned here is the blast radius: that it reaches far enough to leave nothing
/// dangling, that it stops at agents who were not involved, and that it stays derived from the
/// schema rather than from a list somebody has to remember to update.
/// </para>
/// <para>
/// Placeholder agents are <c>orders</c>, <c>billing</c> and <c>inventory</c>, per CLAUDE.md.
/// </para>
/// </remarks>
public sealed class PurgeTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");
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
                new AgentRecord(name, "test", AgentTier.Full, null, null, null, false, AgentStatus.Active),
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
        Assert.DoesNotContain(Orders, await Registered());

        // Nothing but its own row, so no toll worth reporting.
        Assert.Empty(outcome.Destroyed);
    }

    [Fact]
    public async Task A_purged_name_can_be_registered_again()
    {
        // The whole point of the operation. Retirement burns the name for good, which is the
        // right trade only when there is a history behind it worth the cost.
        await _agents.PurgeAsync(Orders, cancellationToken: TestContext.Current.CancellationToken);

        await _agents.UpsertAsync(
            new AgentRecord(Orders, "test", AgentTier.Pull, "/src/acme-orders", null, null, false, AgentStatus.Active),
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
                new AgentRecord(name, "mcp", AgentTier.Headless, "/src/acme", null, null, false, AgentStatus.Active),
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
            AgentName.Create("shipping@test"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(PurgeVerdict.NotRegistered, outcome.Verdict);
        Assert.Empty(outcome.Destroyed);
    }

    [Fact]
    public async Task An_agent_that_has_asked_something_is_deleted_with_its_question()
    {
        await Work.OpenAsync(
            _connection, Orders, Billing, T0, cancellationToken: TestContext.Current.CancellationToken);

        var outcome = await _agents.PurgeAsync(
            Orders, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(PurgeVerdict.Gone, outcome.Verdict);
        Assert.DoesNotContain(Orders, await Registered());
        Assert.Equal(0, await Count("SELECT count(*) FROM exchanges;"));
        Assert.Contains(outcome.Destroyed, t => t.Table == "exchanges" && t.Rows == 1);

        // The agent's own row is not tolled: the toll is an account of collateral, and that row
        // is what was asked for.
        Assert.DoesNotContain(outcome.Destroyed, t => t.Table == "agents");
    }

    [Fact]
    public async Task The_counterparty_loses_its_side_too()
    {
        // The collateral, asserted rather than apologised for. Billing did nothing and keeps
        // nothing: its record of having been asked lives inside the exchange being deleted, and
        // an exchange whose asker no longer exists is a question from nobody.
        //
        // This is the cost that makes retirement the right instrument whenever the history
        // matters, and it belongs in a test so nobody re-derives purge as "delete the row and
        // leave the rest" and finds the suite still green.
        await Work.OpenAsync(
            _connection, Orders, Billing, T0, cancellationToken: TestContext.Current.CancellationToken);

        await _agents.PurgeAsync(
            Orders, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(Billing, await Registered());
        Assert.Equal(0, await Count("SELECT count(*) FROM exchanges WHERE receiver = 'billing@test';"));
    }

    [Fact]
    public async Task An_agent_that_was_asked_something_takes_the_question_with_it()
    {
        await Work.OpenAsync(
            _connection, Orders, Billing, T0, cancellationToken: TestContext.Current.CancellationToken);

        var outcome = await _agents.PurgeAsync(
            Billing, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(PurgeVerdict.Gone, outcome.Verdict);
        Assert.Equal(0, await Count("SELECT count(*) FROM exchanges;"));
        Assert.Contains(Orders, await Registered());
    }

    [Fact]
    public async Task Nothing_is_left_pointing_at_an_agent_that_no_longer_exists()
    {
        // The whole reason the keys are deferred to commit rather than the deletes ordered by
        // hand: if any reference were missed the COMMIT fails, so this passing is the database
        // itself confirming the sweep was complete rather than this test trusting a list.
        await Work.OpenAsync(
            _connection, Orders, Billing, T0, cancellationToken: TestContext.Current.CancellationToken);

        await _agents.PurgeAsync(
            Billing, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, await Count("SELECT count(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public async Task Two_purges_on_one_connection_do_not_collide()
    {
        // The temporary tables holding the doomed set live in the connection's temp schema, not
        // in the transaction's, and one operator clearing a registry purges several agents in a
        // row down the same connection. If the first purge's tables outlived it, the second would
        // either fail to create them or -- far worse -- read the first agent's doomed set and
        // delete an entirely unrelated conversation.
        //
        // Probed rather than reasoned about, because the reasoning is exactly the kind that
        // sounds right: temporary tables are transactional, so a completed transaction must have
        // dropped them. That argument does not cover the path where the drop is the thing that
        // was forgotten.
        await Work.OpenAsync(
            _connection, Orders, Billing, T0, cancellationToken: TestContext.Current.CancellationToken);

        await _agents.PurgeAsync(Orders, cancellationToken: TestContext.Current.CancellationToken);

        var second = await _agents.PurgeAsync(
            Inventory, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(PurgeVerdict.Gone, second.Verdict);
        Assert.Equal([Billing], await Registered());
    }

    [Fact]
    public async Task The_sweep_does_not_have_to_delete_in_dependency_order()
    {
        // What the deferred foreign keys are actually for, and it was worth finding out: turning
        // the pragma off broke no test at all, because today's tables happen to come out of the
        // schema scan in an order that works. A backstop nothing exercises is a backstop that
        // gets deleted by the next person tidying up.
        //
        // The scan returns tables alphabetically, which is a property of sqlite_master and not a
        // dependency order. Two tables from some later migration are enough to separate them: a
        // parent that sorts first and a child that sorts second, so the sweep deletes the parent
        // while the child still points at it. Per-statement enforcement fails there; deferring to
        // commit lets both go and still verifies the result.
        await Execute(
            """
            CREATE TABLE later_a (
                id    INTEGER PRIMARY KEY,
                agent TEXT NOT NULL REFERENCES agents(name)
            );
            CREATE TABLE later_b (
                id       INTEGER PRIMARY KEY,
                agent    TEXT NOT NULL REFERENCES agents(name),
                later_id INTEGER NOT NULL REFERENCES later_a(id)
            );
            INSERT INTO later_a (id, agent) VALUES (1, 'inventory@test');
            INSERT INTO later_b (agent, later_id) VALUES ('inventory@test', 1);
            """);

        var outcome = await _agents.PurgeAsync(
            Inventory, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(PurgeVerdict.Gone, outcome.Verdict);
        Assert.Equal(0, await Count("SELECT count(*) FROM later_a;"));
        Assert.Equal(0, await Count("SELECT count(*) FROM later_b;"));
        Assert.Equal(0, await Count("SELECT count(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public async Task A_purge_that_fails_leaves_the_store_exactly_as_it_was()
    {
        // Foreign keys are deferred to commit, which means a purge that missed something fails at
        // the very end rather than partway through. That is the safe direction only if the whole
        // transaction rolls back, so it is worth pinning that it does: a half-completed purge
        // would leave messages pointing at an agent that no longer exists, which is the one
        // outcome worse than either refusing or deleting.
        //
        // Forced with a table the sweep cannot reach: it references conversations rather than
        // agents, so nothing discovers it, and its row survives to fail the commit.
        await Execute(
            """
            CREATE TABLE later_annotations (
                id              INTEGER PRIMARY KEY,
                conversation_id TEXT NOT NULL REFERENCES conversations(id)
            );
            """);

        await Execute(
            """
            INSERT INTO conversations (id, state, opened_utc)
            VALUES ('c1', 'OPEN', '2026-01-01T00:00:00+00:00');
            INSERT INTO conversation_participants (conversation_id, agent, joined_utc)
            VALUES ('c1', 'orders@test', '2026-01-01T00:00:00+00:00');
            INSERT INTO later_annotations (conversation_id) VALUES ('c1');
            """);

        var before = await Count("SELECT count(*) FROM conversations;");

        await Assert.ThrowsAsync<SqliteException>(() =>
            _agents.PurgeAsync(Orders, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(Orders, await Registered());
        Assert.Equal(before, await Count("SELECT count(*) FROM conversations;"));
        Assert.Equal(1, await Count("SELECT count(*) FROM later_annotations;"));
    }

    [Fact]
    public async Task An_uninvolved_agent_is_still_purgeable_while_others_trade()
    {
        // The blast radius is this agent's own involvement, not the deployment's activity.
        // Otherwise purging anybody would take the whole registry with it.
        await Work.OpenAsync(
            _connection, Orders, Billing, T0, cancellationToken: TestContext.Current.CancellationToken);

        var outcome = await _agents.PurgeAsync(
            Inventory, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(PurgeVerdict.Gone, outcome.Verdict);
    }

    [Fact]
    public async Task Retirement_neither_permits_nor_prevents_a_purge()
    {
        // Status is not the test. Retirement is the gentler decision, and purge is not gated on
        // having made it first: an operator who wants an agent gone should not have to retire it
        // as a ceremony on the way.
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
        Assert.Equal(0, await Count("SELECT count(*) FROM agent_credentials WHERE agent = 'orders@test';"));
    }

    [Fact]
    public async Task A_reference_the_code_has_never_heard_of_still_holds_an_agent()
    {
        // The load-bearing test. Purge finds what to delete by asking the schema which columns
        // reference agents(name), and the reason it asks rather than carrying a list is that a
        // list goes stale at the next migration — at which point the purge fails on a foreign key
        // and surfaces as an unhandled exception where an operator expected an answer.
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

        await Execute("INSERT INTO later_migration (agent) VALUES ('inventory@test');");

        var outcome = await _agents.PurgeAsync(
            Inventory, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(PurgeVerdict.Gone, outcome.Verdict);
        Assert.Equal(0, await Count("SELECT count(*) FROM later_migration;"));
        Assert.Contains(outcome.Destroyed, t => t.Table == "later_migration" && t.Rows == 1);
    }

    [Fact]
    public async Task A_later_reference_that_cascades_goes_with_the_agent()
    {
        // The other half of the same decision, and the reason the scan filters on ON DELETE
        // CASCADE rather than on every reference. A cascading declaration is the schema saying
        // those rows belong to the agent, so they go when its row does and need no statement of
        // their own — naming them again would double-count them in the toll.
        await Execute(
            """
            CREATE TABLE later_belongings (
                id    INTEGER PRIMARY KEY,
                agent TEXT NOT NULL REFERENCES agents(name) ON DELETE CASCADE
            );
            """);

        await Execute("INSERT INTO later_belongings (agent) VALUES ('inventory@test');");

        var outcome = await _agents.PurgeAsync(
            Inventory, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(PurgeVerdict.Gone, outcome.Verdict);
        Assert.Equal(0, await Count("SELECT count(*) FROM later_belongings;"));
    }

    [Fact]
    public async Task The_foreign_keys_remain_the_backstop_under_the_check()
    {
        // Purge defers the keys to commit time, which is safe only while the keys are on at all.
        // Asserted directly because the pragma is a connection setting that defaults to off, so a
        // connection opened without ParleyDatabase would silently drop the backstop and every
        // other test here would still pass.
        await Work.OpenAsync(
            _connection, Orders, Billing, T0, cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<SqliteException>(() =>
            Execute("DELETE FROM agents WHERE name = 'orders@test';"));
    }
}
