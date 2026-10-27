using System.Text;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That migration 002 upgrades a schema 1 store in place, keeping every row, and that what it changes holds afterwards
/// (SPEC.md §9.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by review, round 35.</b> A proposal's id was the highest plus one, so a purge that removed the highest gave
/// the next proposal the same id, and a person holding the purged one open confirmed or declined another agent's plan.
/// The id is now never used twice. The rebuild runs with foreign keys on, where dropping the old table cascades to every
/// item and need, so the first case holds that they all come through.
/// </para>
/// <para>
/// <b>The store is made at schema 1 and filled through the repositories,</b> which read and write the same columns at
/// both versions, so what is migrated is what a live store would hold.
/// </para>
/// </remarks>
public sealed class MigrationTwoTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = SchemaOne();

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RoadmapRepository Roadmaps => new(_connection);

    private static AgentName Orders => AgentName.Create("orders@test");

    private static AgentName Billing => AgentName.Create("billing@test");

    [Fact]
    public async Task Every_proposal_item_need_notice_and_roadmap_comes_through_and_the_store_has_this_builds_shape()
    {
        var kept = await ProposeAsync(Orders, "Billing export");
        var declined = await ProposeAsync(Billing, "Invoice run");
        await ProposeAsync(Billing, "Still waiting");

        var proposal = await Roadmaps.GetProposalAsync(kept, Ct);
        await Roadmaps.ConfirmAsync(kept, proposal!.Content, null, Now, cancellationToken: Ct);
        await Roadmaps.DeclineAsync(declined, Now, cancellationToken: Ct);

        var before = Dump();

        Assert.Equal(1, SchemaMigrator.Migrate(_connection));

        Assert.Equal(2, SchemaMigrator.GetUserVersion(_connection));
        Assert.Equal(before, Dump());
        Assert.Equal(0L, Count("SELECT count(*) FROM pragma_foreign_key_check;"));
        Assert.Empty(StoreShape.Problems(_connection));

        // The items and needs are the old table's children, which its drop deleted: they must be back and still joined.
        var read = await Roadmaps.GetProposalAsync(kept, Ct);
        Assert.Equal(3, read!.Content.Items.Count);
        Assert.Equal([2], read.Content.Items[2].Needs);
    }

    [Fact]
    public async Task An_id_a_notice_or_a_roadmap_still_names_is_never_given_again()
    {
        var first = await ProposeAsync(Orders, "First");
        var second = await ProposeAsync(Billing, "Second");

        var proposal = await Roadmaps.GetProposalAsync(second, Ct);
        await Roadmaps.ConfirmAsync(second, proposal!.Content, null, Now, cancellationToken: Ct);

        // Both proposals gone, as retention or a purge leaves them: the roadmap still names the second.
        Execute("DELETE FROM roadmap_proposals;");

        SchemaMigrator.Migrate(_connection);

        var next = await ProposeAsync(Orders, "After the upgrade");
        Assert.True(next > second, $"id {next} was given again; {first} and {second} were used before");
    }

    [Fact]
    public async Task After_the_upgrade_a_purged_proposals_id_goes_to_no_other()
    {
        SchemaMigrator.Migrate(_connection);

        var purged = await ProposeAsync(Orders, "Orders plan");

        // A purge of the proposer cascades to its proposals.
        Execute("DELETE FROM agents WHERE name = 'orders@test';");

        var next = await ProposeAsync(Billing, "Billing plan");

        Assert.NotEqual(purged, next);
        Assert.Null(await Roadmaps.GetProposalAsync(purged, Ct));
    }

    [Fact]
    public async Task An_agent_may_still_answer_in_an_items_conversation_after_retention_removed_its_messages()
    {
        SchemaMigrator.Migrate(_connection);

        var id = await ProposeAsync(Orders, "Billing export");
        var proposal = await Roadmaps.GetProposalAsync(id, Ct);
        await Roadmaps.ConfirmAsync(id, proposal!.Content, null, Now, cancellationToken: Ct);

        Open("rm1-1", "human-user@test", "orders@test");

        // Retention removes messages and keeps exchanges.
        Execute("DELETE FROM messages;");

        Open("rm1-1", "orders@test", "billing@test");

        // And an agent still cannot be the first to write in a reserved conversation.
        var refused = Assert.Throws<SqliteException>(() => Open("rm1-2", "orders@test", "billing@test"));
        Assert.Contains("opened by the person", refused.Message, StringComparison.Ordinal);
    }

    private static SqliteConnection SchemaOne()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        SchemaMigrator.Configure(connection);

        var assembly = typeof(SchemaMigrator).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".001_initial.sql", StringComparison.Ordinal));

        using (var stream = assembly.GetManifestResourceStream(name)!)
        using (var reader = new StreamReader(stream))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = reader.ReadToEnd() + "\nPRAGMA user_version = 1;";
            command.ExecuteNonQuery();
        }

        using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-01-01T00:00:00+00:00');
                INSERT INTO agents (name, adapter, tier, status) VALUES
                    ('orders@test', 'claude-code', 'full', 'active'),
                    ('billing@test', 'claude-code', 'full', 'active'),
                    ('human-user@test', 'parley-console', 'pull', 'active');
                """;
            seed.ExecuteNonQuery();
        }

        return connection;
    }

    private async Task<long> ProposeAsync(AgentName by, string title)
    {
        var (id, problem) = await Roadmaps.ProposeAsync(
            by,
            null,
            null,
            new RoadmapContent(
                title,
                [
                    new RoadmapItemDraft(null, "Add CSV export", by, []),
                    new RoadmapItemDraft(null, "Call it from the report", null, [1]),
                    new RoadmapItemDraft(null, "Document the column", null, [2]),
                ]),
            Now,
            cancellationToken: Ct);

        Assert.Null(problem);

        return id!.Value;
    }

    // Every row of every table the rebuild touches or that names a proposal, in a fixed order.
    private string Dump()
    {
        var text = new StringBuilder();

        foreach (var table in new[] { "roadmap_proposals", "roadmap_proposal_items", "roadmap_proposal_needs", "roadmap_notices", "roadmaps", "roadmap_items" })
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table} ORDER BY 1, 2;";

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                text.Append(table);

                for (var i = 0; i < reader.FieldCount; i++)
                {
                    text.Append('|').Append(reader.IsDBNull(i) ? "null" : Convert.ToString(reader.GetValue(i), provider: null));
                }

                text.AppendLine();
            }
        }

        return text.ToString();
    }

    private void Open(string conversation, string from, string to)
    {
        var pair = string.CompareOrdinal(from, to) < 0 ? from + (char)31 + to : to + (char)31 + from;

        Execute($$"""
            INSERT INTO conversations (id, state, opened_utc) VALUES ('{{conversation}}', 'OPEN', '{{Now:O}}')
              ON CONFLICT DO NOTHING;
            INSERT INTO conversation_participants (conversation_id, agent, joined_utc)
            VALUES ('{{conversation}}', '{{from}}', '{{Now:O}}'), ('{{conversation}}', '{{to}}', '{{Now:O}}')
              ON CONFLICT DO NOTHING;
            INSERT INTO exchanges
                (pair_key, requester, receiver, owed_by, state, conversation_id, opened_utc,
                 uncollected_flag_ms, stalled_flag_ms, request_hash)
            VALUES ('{{pair}}', '{{from}}', '{{to}}', '{{to}}', 'OPEN', '{{conversation}}',
                    '{{Now:O}}', 900000, 14400000, 'hash-{{Guid.NewGuid():N}}');
            INSERT INTO messages
                (exchange_id, type, from_agent, to_agent, responder_model, payload_json,
                 content_hash, conversation_id, created_utc)
            VALUES (last_insert_rowid(), 'request', '{{from}}', '{{to}}', 'claude-opus-5',
                    '{"ask":"Add CSV export"}', '{{Guid.NewGuid():N}}{{Guid.NewGuid():N}}', '{{conversation}}', '{{Now:O}}');
            """);
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private long Count(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), provider: null);
    }
}
