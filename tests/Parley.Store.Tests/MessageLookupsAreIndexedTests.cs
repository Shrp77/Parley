using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That the reads that name an exchange, a conversation or an agent search an index and do not walk every message (migration 010).
/// </summary>
/// <remarks>
/// Review, performance lens run 1 (0.62.1): without these, one exchange's read cost about 120 ms and a conversation's
/// wrote-elsewhere notes about 400 ms at 100,000 messages, and the Inbox asks for the second of each active conversation.
/// </remarks>
[Integration]
public sealed class MessageLookupsAreIndexedTests : IDisposable
{
    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();

    public void Dispose() => _connection.Dispose();

    [Theory]
    [InlineData("SELECT id FROM messages WHERE exchange_id = 5 AND id > 0 ORDER BY id LIMIT 10;", "ix_messages_exchange")]
    [InlineData("SELECT COUNT(*) FROM messages WHERE exchange_id = 5 AND id > 0;", "ix_messages_exchange")]
    [InlineData("SELECT MIN(id) FROM messages WHERE conversation_id = 'c';", "ix_messages_conversation")]
    [InlineData("SELECT MIN(created_utc) FROM messages WHERE conversation_id = 'c';", "ix_messages_conversation_time")]
    [InlineData("SELECT MAX(created_utc) FROM messages WHERE conversation_id = 'c';", "ix_messages_conversation_time")]
    [InlineData("SELECT started_utc FROM invocations i WHERE i.agent = 'a@b' ORDER BY i.id DESC LIMIT 1;", "ix_invocations_agent_id")]
    [InlineData("SELECT id FROM exchanges WHERE owed_by = 'a@b';", "ix_exchanges_owed_by")]
    [InlineData("SELECT 1 FROM exchanges e WHERE e.conversation_id = 'c' AND e.state = 'OPEN';", "ix_exchanges_conversation")]
    [InlineData("SELECT max(created_utc) FROM messages WHERE conversation_id = 'c' AND from_agent = 'a@b';", "ix_messages_conversation_from_time")]
    [InlineData("SELECT 1 FROM messages m WHERE m.exchange_id = 5 AND +m.to_agent = 'a@b';", "ix_messages_exchange")]
    [InlineData("SELECT id FROM messages WHERE from_agent = 'a@b' AND id >= 3;", "ix_messages_from")]
    [InlineData("SELECT id FROM messages WHERE to_agent = 'a@b' AND id >= 3;", "ix_messages_to")]
    public void A_read_by_exchange_conversation_or_agent_uses_its_index(string sql, string index)
    {
        var plan = string.Join(
            " | ",
            _connection.Query($"EXPLAIN QUERY PLAN {sql}").Select(r => (string)((IDictionary<string, object>)r)["detail"]));

        // Whole-word, so ix_messages_conversation is not satisfied by ix_messages_conversation_time, which contains its name.
        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(
                plan, System.Text.RegularExpressions.Regex.Escape(index) + "(?![_a-z])", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)),
            plan);
    }
}
