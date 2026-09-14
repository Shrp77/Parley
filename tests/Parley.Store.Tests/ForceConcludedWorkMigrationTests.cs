using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That work 0.4 left open inside a conversation a person had closed does not reach 0.5 open.
/// </summary>
/// <remarks>
/// <para>
/// Found by the review gate. 0.4's <c>force_conclude</c> closed the conversation row and nothing else; its
/// exchanges stayed open or orphaned until the ghost sweep reached them. Migration 008 carried every
/// exchange that was not closed across as open, and kept the conversation closed — a state 0.5 cannot
/// produce and cannot clear. The receiver still held the conversation, so a Request that named none was
/// assigned to it and refused as closed, and naming another was refused because it held one: the agent
/// could send no Request at all. And <c>force_conclude</c>, matching only a conversation not yet closed,
/// returned false and closed nothing, so no person could end it either.
/// </para>
/// <para>
/// Its own fixture rather than rows added to <see cref="WorkAcrossRepositoriesMigrationTests"/>, whose tests
/// count what the upgrade carried.
/// </para>
/// </remarks>
public sealed class ForceConcludedWorkMigrationTests : IDisposable
{
    private const string Stamp = "2026-01-01T00:00:00.0000000Z";
    private const string Ended = "2026-01-01T00:07:00.0000000Z";

    private const string Question =
        """{"intent":"where tax is added","sub_questions":[{"id":"q1","text":"gross or net?"}],"consequences":"If gross I stop adding tax at invoice time; if net I keep it."}""";

    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset After = DateTimeOffset.Parse("2026-02-01T00:00:00Z", CultureInfo.InvariantCulture);

    private readonly SqliteConnection _connection;

    public ForceConcludedWorkMigrationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        SchemaMigrator.Configure(_connection);

        const string Prefix = "Parley.Store.Migrations.";
        var assembly = typeof(SchemaMigrator).Assembly;

        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            if (int.Parse(name.AsSpan(Prefix.Length, 3), CultureInfo.InvariantCulture) > 7)
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = reader.ReadToEnd();
            command.ExecuteNonQuery();
            transaction.Commit();
        }

        _connection.Execute("PRAGMA user_version = 7;");

        Work.Register(
            _connection, AgentName.Create("orders@test"), Billing, AgentName.Create("inventory@test"));

        _connection.Execute($"INSERT INTO conversations (id, state, opened_utc) VALUES ('ended', 'OPEN', '{Stamp}');");
        _connection.Execute($"INSERT INTO conversation_participants (conversation_id, agent, joined_utc) VALUES ('ended','orders@test','{Stamp}'), ('ended','billing@test','{Stamp}');");

        foreach (var (id, state) in new[] { (1L, "OPEN"), (2L, "ORPHANED") })
        {
            _connection.Execute(
                """
                INSERT INTO exchanges
                    (id, pair_key, asker, answerer, owed_by, state, delivery_window_ms, working_window_ms,
                     grace_period_ms, question_hash, opened_utc, expires_utc, closed_utc, resolution, conversation_id)
                VALUES
                    (@Id, 'billing@test' || char(31) || 'orders@test', 'orders@test', 'billing@test', 'billing@test',
                     @State, 900000, 14400000, 10800000, @Hash, @Opened, @Opened, NULL, NULL, 'ended');
                """,
                new { Id = id, State = state, Hash = "h" + id, Opened = Stamp });

            _connection.Execute(
                """
                INSERT INTO messages
                    (exchange_id, type, from_agent, to_agent, responder_model, payload_json, content_hash,
                     conversation_id, created_utc, delivered_utc)
                VALUES (@Id, 'question', 'orders@test', 'billing@test', 'claude-opus-5', @Payload, @Hash, 'ended', @At, @At);
                """,
                new { Id = id, Payload = Question, Hash = id.ToString("x64", CultureInfo.InvariantCulture), At = Stamp });
        }

        // Then force-concluded, as 0.4 did it: the conversation row alone.
        _connection.Execute($"UPDATE conversations SET state = 'CLOSED', closed_utc = '{Ended}' WHERE id = 'ended';");

        Assert.Equal(1, SchemaMigrator.Migrate(_connection));
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void Work_in_a_conversation_a_person_closed_is_closed_with_it()
    {
        var rows = _connection.Query<(string State, string Outcome, string ClosedUtc)>(
            "SELECT state, outcome, closed_utc FROM exchanges ORDER BY id;").ToList();

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal("CLOSED", row.State);
            Assert.Equal("closed", row.Outcome);
            Assert.Equal(Ended, row.ClosedUtc);
        });
    }

    [Fact]
    public async Task Its_receiver_can_send_requests_afterwards()
    {
        var ct = TestContext.Current.CancellationToken;
        var exchanges = new ExchangeRepository(_connection);

        var assigned = await exchanges.AssignConversationAsync(Billing, null, senderIsPerson: false, cancellationToken: ct);

        Assert.Null(assigned.Refusal);
        Assert.NotEqual("ended", assigned.ConversationId);

        var opened = await exchanges.TryOpenAsync(
            Billing, AgentName.Create("inventory@test"), After, "hash-new", assigned.ConversationId!, cancellationToken: ct);

        Assert.True(opened.Succeeded, $"refused: {opened.Rejection}");
    }

    [Fact]
    public void The_store_is_sound_after_the_upgrade()
    {
        Assert.Equal("ok", _connection.ExecuteScalar<string>("PRAGMA integrity_check;"));
        Assert.Empty(_connection.Query("PRAGMA foreign_key_check;"));
    }
}
