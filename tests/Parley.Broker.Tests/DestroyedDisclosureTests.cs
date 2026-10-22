using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// What the destroyed record discloses, and to whom.
/// </summary>
/// <remarks>
/// <para>
/// The record names which agents were
/// party to a conversation. That is precisely the shape of disclosure §10.1 withholds from an
/// agent: an agent may address a peer and must not be able to enumerate what its peers have
/// discussed with anybody else.
/// </para>
/// <para>
/// It is on `export`, which is an operator operation, so the boundary is where it should be. What
/// this asks is whether anything else reaches it — and whether the record says more about a
/// conversation than the conversation itself would have.
/// </para>
/// </remarks>
public sealed class DestroyedDisclosureTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly OperatorConsole _console;

    public DestroyedDisclosureTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _console = new OperatorConsole(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A payload the schema accepts, kept out of the interpolation.</summary>
    private const string Payload =
        @"{""ask"":""who owns the invoice total, and does it include tax?""}";

    [Fact]
    public void The_record_is_reachable_only_through_the_operator_console()
    {
        // The structural half. `Broker` is what an adapter is handed, and a record naming who talked
        // to whom must not be on it -- SurfaceBoundaryTests enumerates the console's surface, and
        // this asks the other side: that nothing on the agent-facing type returns one.
        var broker = typeof(Broker)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(method => method.DeclaringType == typeof(Broker))
            .ToList();

        Assert.NotEmpty(broker);
        Assert.DoesNotContain(
            broker,
            method => method.ReturnType.FullName?.Contains("Destroyed", StringComparison.Ordinal) == true
                   || method.Name.Contains("Destroyed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Exporting_a_conversation_that_was_never_destroyed_carries_no_record()
    {
        // Reading the record on every export would put a second query on the common path and, worse,
        // would make an absent record indistinguishable from an unread one.
        var export = await _console.ExportAsync("never-existed", Ct);

        Assert.Empty(export.Messages);
        Assert.Null(export.Destroyed);
    }

    [Fact]
    public void The_record_type_has_nowhere_to_put_message_content()
    {
        // §9.4's argument made structural. The type has four members and none of them is free text
        // from a payload -- so keeping content past a destroy would require adding a field, which is
        // a thing somebody has to decide to do rather than a thing that can happen by accident.
        var members = typeof(DestroyedConversation)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(property => property.Name != "EqualityContract")
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["DestroyedUtc", "Id", "Messages", "Participants"], members);
    }

    [Fact]
    public void The_record_says_no_more_about_participants_than_the_registry_already_does()
    {
        // Names, and nothing beside them. A record that carried a workspace would disclose through
        // a destroy what §10.1 withholds from `agents()` -- and the conversation it replaced never
        // held one.
        var participants = typeof(DestroyedConversation)
            .GetProperty(nameof(DestroyedConversation.Participants))!;

        Assert.Equal(
            typeof(IReadOnlyList<AgentName>), participants.PropertyType);
    }

    [Fact]
    public async Task A_destroy_and_an_export_agree_about_how_much_went()
    {
        // The report an operator is given at the moment of destroying, and the record they are given
        // when they ask later, are two accounts of one event. Two accounts that disagree are worse
        // than one, because there is nothing left to settle which was right.
        Seed("alpha", messages: 4);

        var destroyed = await _console.DestroyConversationAsync(
            "alpha", DateTimeOffset.UnixEpoch, Ct);

        var export = await _console.ExportAsync("alpha", Ct);

        Assert.Equal(
            destroyed.Destroyed.Single(toll => toll.Table == "messages").Rows,
            export.Destroyed!.Messages);
    }

    private void Seed(string id, int messages)
    {
        Execute($"""
            INSERT INTO nodes (machine, connected, first_seen_utc)
            VALUES ('test', 1, '2026-01-01T00:00:00+00:00') ON CONFLICT DO NOTHING;
            INSERT INTO agents (name, adapter, tier, status) VALUES
                ('orders@test','test','full','active'),
                ('billing@test','test','full','active')
            ON CONFLICT DO NOTHING;

            INSERT INTO conversations (id, state, opened_utc)
            VALUES ('{id}', 'OPEN', '2026-01-01T00:00:00.0000000+00:00');

            INSERT INTO conversation_participants (conversation_id, agent, joined_utc)
            VALUES ('{id}', 'orders@test', '2026-01-01T00:00:00.0000000+00:00'),
                   ('{id}', 'billing@test', '2026-01-01T00:00:00.0000000+00:00');

            INSERT INTO exchanges
                (pair_key, requester, receiver, owed_by, state, conversation_id, opened_utc,
                 closed_utc, clarify_depth, uncollected_flag_ms, stalled_flag_ms, request_hash, outcome)
            VALUES ('billing@testSEPorders@test', 'orders@test', 'billing@test',
                    'billing@test', 'OPEN', '{id}', '2026-01-01T00:00:00.0000000+00:00',
                    NULL, 0, 900000, 14400000, 'request-hash-{id}', NULL);
            """.Replace("SEP", "\u001F", StringComparison.Ordinal));

        var exchange = (long)(Scalar("SELECT last_insert_rowid();") ?? 0L);

        for (var i = 0; i < messages; i++)
        {
            Execute($"""
                INSERT INTO messages
                    (exchange_id, type, from_agent, to_agent, responder_model, payload_json,
                     content_hash, conversation_id, created_utc)
                VALUES ({exchange}, 'request', 'orders@test', 'billing@test', 'some-model-9',
                        '{Payload}',
                        '{i:x64}', '{id}', '2026-01-01T00:0{i}:00.0000000+00:00');
                """);
        }
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
