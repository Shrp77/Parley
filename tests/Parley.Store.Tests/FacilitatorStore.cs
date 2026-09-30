using Microsoft.Data.Sqlite;

namespace Parley.Store.Tests;

/// <summary>
/// A store holding the facilitator, two agents, and a seat, written directly rather than through the repositories.
/// </summary>
/// <remarks>
/// <b>Direct SQL on purpose.</b> These tests are about what storage refuses (SPEC.md §9.3), so they must reach
/// storage without passing through any code that might refuse first. A repository that checked the reserved role
/// itself would make a missing trigger invisible here, which is the gap Principle V exists to close.
/// </remarks>
internal sealed class FacilitatorStore : IDisposable
{
    /// <summary>The facilitator's name in these stores.</summary>
    public const string Facilitator = "parley-facilitator@test";

    /// <summary>The unit separator the pair key's CHECK requires.</summary>
    private const string Separator = "\u001F";

    private int _hashes;

    public FacilitatorStore()
    {
        Connection = ParleyDatabase.OpenInMemory();

        Execute($"""
            INSERT INTO nodes (machine, connected, first_seen_utc)
            VALUES ('test', 1, '2026-01-01T00:00:00+00:00') ON CONFLICT DO NOTHING;
            INSERT INTO agents (name, adapter, tier, status) VALUES
                ('{Facilitator}', 'parley-facilitator', 'pull', 'active'),
                ('orders@test', 'claude-code', 'full', 'active'),
                ('billing@test', 'claude-code', 'full', 'active'),
                ('arvind@test', 'parley-console', 'pull', 'active');
            """);
    }

    /// <summary>The store.</summary>
    public SqliteConnection Connection { get; }

    public void Dispose() => Connection.Dispose();

    /// <summary>
    /// Opens a conversation holding one Request from <paramref name="from"/> to <paramref name="to"/>, and returns the
    /// exchange's identifier.
    /// </summary>
    public long Asked(string conversation, string from, string to)
    {
        _hashes++;

        Execute($"""
            INSERT INTO conversations (id, state, opened_utc) VALUES ('{conversation}', 'OPEN', '{Stamp}');
            INSERT INTO conversation_participants (conversation_id, agent, joined_utc)
            VALUES ('{conversation}', '{from}', '{Stamp}'), ('{conversation}', '{to}', '{Stamp}');
            INSERT INTO exchanges
                (pair_key, requester, receiver, owed_by, state, conversation_id, opened_utc,
                 uncollected_flag_ms, stalled_flag_ms, request_hash)
            VALUES ('{PairKey(from, to)}', '{from}', '{to}', '{to}', 'OPEN', '{conversation}', '{Stamp}',
                    900000, 14400000, 'hash-{_hashes}');
            """);

        var exchange = (long)(Scalar("SELECT last_insert_rowid();") ?? 0L);

        Execute($$"""
            INSERT INTO messages
                (exchange_id, type, from_agent, to_agent, responder_model, payload_json,
                 content_hash, conversation_id, created_utc)
            VALUES ({{exchange}}, 'request', '{{from}}', '{{to}}', 'parley:facilitator',
                    '{"ask":"What do you own, and what do you not?"}', '{{_hashes:x64}}', '{{conversation}}', '{{Stamp}}');
            """);

        return exchange;
    }

    /// <summary>Records a question the facilitator asked, in its first state.</summary>
    public void Question(string agent, string conversation, long exchange) =>
        Execute($"""
            INSERT INTO ownership_questions (agent, conversation_id, exchange_id, state, asked_utc)
            VALUES ('{agent}', '{conversation}', {exchange}, 'asked', '{Stamp}');
            """);

    /// <summary>Records an accepted line agreed in <paramref name="conversation"/>.</summary>
    public void Accepted(string agent, string conversation, string source = "proposed") =>
        Execute($"""
            INSERT INTO agent_ownership_accepted
                (agent, statement, source, proposed_length, conversation_id, accepted_utc)
            VALUES ('{agent}', 'orders and their totals; not tax', '{source}', 32, '{conversation}', '{Stamp}');
            """);

    public const string Stamp = "2026-01-01T00:00:00.0000000+00:00";

    public void Execute(string sql)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public object? Scalar(string sql)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public long Count(string sql) => Convert.ToInt64(Scalar(sql), provider: null);

    private static string PairKey(string a, string b) =>
        string.CompareOrdinal(a, b) < 0 ? $"{a}{Separator}{b}" : $"{b}{Separator}{a}";
}
