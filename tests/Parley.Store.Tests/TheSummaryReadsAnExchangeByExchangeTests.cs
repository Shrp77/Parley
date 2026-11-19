using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That the summary export's two reads of an exchange's messages search by exchange and never walk the conversation.
/// </summary>
/// <remarks>
/// Review, performance lens (0.62.8): written with a plain <c>conversation_id</c> test the planner chose the by-conversation
/// index and walked every message of the conversation for each exchange, so a conversation of 100 exchanges took 357 ms to
/// summarise where the whole export took 35, and the Inbox of 200 of them 72 s. The tests before this one used one or two
/// exchanges to a conversation, where either plan is instant.
/// </remarks>
[Integration]
public sealed class TheSummaryReadsAnExchangeByExchangeTests : IDisposable
{
    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();

    public void Dispose() => _connection.Dispose();

    [Theory]
    [InlineData(MessageRepository.NewestPerSender)]
    [InlineData(MessageRepository.NewestResult)]
    public void Each_read_is_a_search_of_the_exchanges_index_and_does_not_touch_the_conversation_indexes(string where)
    {
        var plan = string.Join(
            " | ",
            _connection.Query($"EXPLAIN QUERY PLAN SELECT id FROM messages WHERE {where}", new { Exchange = 5L, Conversation = "c" })
                .Select(r => (string)((IDictionary<string, object>)r)["detail"]));

        Assert.Contains("ix_messages_exchange", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("ix_messages_conversation", plan, StringComparison.Ordinal);
    }
}
