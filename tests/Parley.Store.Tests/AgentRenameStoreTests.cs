using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// Giving an agent a new name without losing anything it was party to (SPEC.md §4.1.1).
/// </summary>
/// <remarks>
/// A rename touches every table that names an agent, and the schema is the only list of them that cannot go stale, so
/// the checks here ask the schema rather than a list: nothing may name the old agent afterwards, and the database must
/// still agree with itself.
/// </remarks>
public sealed class AgentRenameStoreTests : IDisposable
{
    private const string Id = "9f2c1a77-3b41-4e0e-8a6f-7c4b2d9e5011";
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Payments = AgentName.Create("payments@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly AgentRepository _agents;

    public AgentRenameStoreTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _agents = new AgentRepository(_connection);

        foreach (var name in new[] { Orders, Billing })
        {
            _agents.UpsertAsync(
                new AgentRecord(
                    name, "test", AgentTier.Full, "/work/" + name.Role, null, null, false, AgentStatus.Active,
                    Identity: name == Billing ? AgentIdentity.Read(Id) : default),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }
    }

    public void Dispose() => _connection.Dispose();

    private Task<long> Count(string sql, object? parameters = null) =>
        _connection.ExecuteScalarAsync<long>(new CommandDefinition(
            sql, parameters, cancellationToken: TestContext.Current.CancellationToken));

    /// <summary>How many rows anywhere in the schema still name an agent, asked of the schema.</summary>
    private async Task<long> NamedAnywhere(string agent)
    {
        long found = 0;

        var references = (await _connection.QueryAsync<(string Table, string Column)>(new CommandDefinition(
            """
            SELECT m.name, f."from" FROM sqlite_master AS m
              JOIN pragma_foreign_key_list(m.name) AS f
             WHERE m.type = 'table' AND f."table" = 'agents';
            """,
            cancellationToken: TestContext.Current.CancellationToken))).ToList();

        foreach (var (table, column) in references)
        {
            found += await Count($"""SELECT count(*) FROM "{table}" WHERE "{column}" = @Agent;""", new { Agent = agent });
        }

        return found;
    }

    [Fact]
    public async Task Everything_the_agent_was_party_to_follows_it_and_the_database_stays_whole()
    {
        var closed = await Work.OpenAsync(
            _connection, Orders, Billing, T0, cancellationToken: TestContext.Current.CancellationToken);
        _ = await Work.CloseAsync(
            _connection, closed.Exchange, T0.AddMinutes(1), cancellationToken: TestContext.Current.CancellationToken);
        _ = await Work.OpenAsync(
            _connection, Billing, Orders, T0.AddMinutes(2), Work.Request("and the other way?"),
            conversationId: "c-two", cancellationToken: TestContext.Current.CancellationToken);

        var before = await Count("SELECT count(*) FROM messages;");
        var exchangesBefore = await Count("SELECT count(*) FROM exchanges;");

        Assert.True(await _agents.RenameAsync(Billing, Payments, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, await NamedAnywhere(Billing.Value));
        Assert.Equal(0, await Count("SELECT count(*) FROM agents WHERE name = @Name;", new { Name = Billing.Value }));
        Assert.Equal(0, await Count("SELECT count(*) FROM pragma_foreign_key_check;"));
        Assert.Equal(before, await Count("SELECT count(*) FROM messages;"));
        Assert.Equal(exchangesBefore, await Count("SELECT count(*) FROM exchanges;"));

        // The pair key is the two names, and a check holds it to them.
        Assert.Equal(
            2,
            await Count(
                "SELECT count(*) FROM exchanges WHERE pair_key = min(requester, receiver) || char(31) || max(requester, receiver)" +
                " AND @Name IN (requester, receiver);",
                new { Name = Payments.Value }));
    }

    [Fact]
    public async Task The_new_row_keeps_what_the_old_one_said_about_itself()
    {
        _ = await _connection.ExecuteAsync(new CommandDefinition(
            "UPDATE agents SET owns = 'invoices', status = 'retired', session_ref = 's1' WHERE name = @Name;",
            new { Name = Billing.Value },
            cancellationToken: TestContext.Current.CancellationToken));

        _ = await _agents.RenameAsync(Billing, Payments, cancellationToken: TestContext.Current.CancellationToken);

        var row = Assert.Single(
            await _agents.GetAllAsync(TestContext.Current.CancellationToken), agent => agent.Name == Payments);

        Assert.Equal("invoices", row.Owns);
        Assert.Equal(AgentStatus.Retired, row.Status);
        Assert.Equal("s1", row.SessionRef);
        Assert.Equal("/work/billing", row.Workspace);
        Assert.Equal(AgentIdentity.Read(Id), row.Identity);
    }

    [Fact]
    public async Task A_vote_in_a_closed_conversation_is_moved_not_recast()
    {
        var opened = await Work.OpenAsync(
            _connection, Orders, Billing, T0, cancellationToken: TestContext.Current.CancellationToken);
        _ = await Work.CloseAsync(
            _connection, opened.Exchange, T0.AddMinutes(1), cancellationToken: TestContext.Current.CancellationToken);

        _ = await _connection.ExecuteAsync(new CommandDefinition(
            "UPDATE conversation_participants SET polled_utc = '2026-01-01T00:01:00.000Z', vote = 'done', " +
            "voted_utc = '2026-01-01T00:01:30.000Z' WHERE agent = @Name;" +
            "UPDATE conversations SET state = 'CLOSED', closed_utc = '2026-01-01T00:02:00.000Z', closed_by = 'consensus' " +
            "WHERE id = @Id;",
            new { Id = Work.Conversation, Name = Billing.Value },
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(await _agents.RenameAsync(Billing, Payments, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(
            "done",
            await _connection.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT vote FROM conversation_participants WHERE agent = @Name;",
                new { Name = Payments.Value },
                cancellationToken: TestContext.Current.CancellationToken)));

        // A vote cast after the close is still refused.
        var late = await Assert.ThrowsAsync<SqliteException>(async () => await _connection.ExecuteAsync(new CommandDefinition(
            "UPDATE conversation_participants SET vote = 'not_done' WHERE agent = @Name;",
            new { Name = Payments.Value },
            cancellationToken: TestContext.Current.CancellationToken)));

        Assert.Contains("conversation is closed", late.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_columns_that_are_text_by_design_follow_the_name()
    {
        var opened = await Work.OpenAsync(
            _connection, Orders, Billing, T0, cancellationToken: TestContext.Current.CancellationToken);

        _ = await _connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO file_placements (agent, placements, bytes, last_utc) VALUES (@Name, 1, 10, '2026-01-01T00:00:00.000Z');
            INSERT INTO conversation_folders (conversation_id, folder, created_utc) VALUES (@Id, @Id, '2026-01-01T00:00:00.000Z');
            INSERT INTO shared_files (conversation_id, name_key, name, size_bytes, sha256, placed_by, placed_utc, changed_utc,
                                      modified_ticks)
            VALUES (@Id, 'NOTES.MD', 'notes.md', 10, @Sha, @Name, '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z', 0);
            INSERT INTO roadmaps (number, title, origin, proposer, created_utc, updated_utc)
            VALUES (1, 'a plan', 'proposed', @Name, '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z');
            """,
            new { Id = Work.Conversation, Name = Billing.Value, Sha = new string('a', 64) },
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(await _agents.RenameAsync(Billing, Payments, cancellationToken: TestContext.Current.CancellationToken));

        var name = new { Name = Payments.Value };

        Assert.Equal(1, await Count("SELECT count(*) FROM file_placements WHERE agent = @Name;", name));
        Assert.Equal(1, await Count("SELECT count(*) FROM shared_files WHERE placed_by = @Name;", name));
        Assert.Equal(1, await Count("SELECT count(*) FROM roadmaps WHERE proposer = @Name;", name));
    }

    [Fact]
    public async Task A_placer_cannot_be_changed_by_anything_but_its_own_rename()
    {
        var opened = await Work.OpenAsync(
            _connection, Orders, Billing, T0, cancellationToken: TestContext.Current.CancellationToken);

        _ = await _connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO conversation_folders (conversation_id, folder, created_utc) VALUES (@Id, @Id, '2026-01-01T00:00:00.000Z');
            INSERT INTO shared_files (conversation_id, name_key, name, size_bytes, sha256, placed_by, placed_utc, changed_utc,
                                      modified_ticks)
            VALUES (@Id, 'NOTES.MD', 'notes.md', 10, @Sha, @Name, '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z', 0);
            """,
            new { Id = Work.Conversation, Name = Billing.Value, Sha = new string('a', 64) },
            cancellationToken: TestContext.Current.CancellationToken));

        // Both are registered, so handing the file to the other is rewriting who placed it.
        var refused = await Assert.ThrowsAsync<SqliteException>(async () => await _connection.ExecuteAsync(new CommandDefinition(
            "UPDATE shared_files SET placed_by = @Other;",
            new { Other = Orders.Value },
            cancellationToken: TestContext.Current.CancellationToken)));

        Assert.Contains("keeps its conversation, its placer", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_table_the_code_has_never_heard_of_is_moved_too()
    {
        _ = await _connection.ExecuteAsync(new CommandDefinition(
            """
            CREATE TABLE later_a (id INTEGER PRIMARY KEY, agent TEXT NOT NULL REFERENCES agents(name));
            INSERT INTO later_a (id, agent) VALUES (1, 'billing@test');
            """,
            cancellationToken: TestContext.Current.CancellationToken));

        _ = await _agents.RenameAsync(Billing, Payments, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, await Count("SELECT count(*) FROM later_a WHERE agent = 'payments@test';"));
    }

    [Fact]
    public async Task A_name_that_is_taken_is_refused_and_nothing_moves()
    {
        var opened = await Work.OpenAsync(
            _connection, Orders, Billing, T0, cancellationToken: TestContext.Current.CancellationToken);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _agents.RenameAsync(Billing, Orders, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("already belongs", refused.Message, StringComparison.Ordinal);
        Assert.Equal(
            1,
            await Count("SELECT count(*) FROM exchanges WHERE id = @Id AND receiver = @Name;",
                new { Id = opened.Exchange.Id, Name = Billing.Value }));
    }

    [Fact]
    public async Task An_unregistered_name_is_reported_as_such()
    {
        Assert.False(await _agents.RenameAsync(
            AgentName.Create("ghost@test"), Payments, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task One_machine_holds_one_agent_per_identity_and_another_machine_may_hold_another()
    {
        var elsewhere = AgentName.Create("billing@other");

        await _agents.UpsertAsync(
            new AgentRecord(
                elsewhere, "test", AgentTier.Full, "/work/billing", null, null, false, AgentStatus.Active,
                Identity: AgentIdentity.Read(Id)),
            TestContext.Current.CancellationToken);

        var twin = await Assert.ThrowsAsync<SqliteException>(async () => await _agents.UpsertAsync(
            new AgentRecord(
                Payments, "test", AgentTier.Full, "/work/billing", null, null, false, AgentStatus.Active,
                Identity: AgentIdentity.Read(Id)),
            TestContext.Current.CancellationToken));

        Assert.Contains("UNIQUE", twin.Message, StringComparison.Ordinal);

        var holder = await _agents.HolderOfIdentityAsync(
            AgentIdentity.Read(Id), "other", TestContext.Current.CancellationToken);

        Assert.Equal(elsewhere, holder!.Name);
    }
}
