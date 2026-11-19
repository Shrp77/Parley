using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That the wrote-elsewhere read, narrowed to follow each agent's next message through the by-agent indexes, answers exactly
/// what the read that scanned every later message answered (SPEC.md 5.0).
/// </summary>
/// <remarks>
/// Review, performance lens run 2: the first narrowing by agent set left a busy pair or a hub agent at about 600 ms a call
/// at 100,000 messages. The reference below is the earlier query, kept as the definition of the answer. Random stores with
/// moves, messages written elsewhere than their exchange now is, and moves with no recorded last message.
/// </remarks>
[Integration]
[Slow]
public sealed class WroteElsewhereKeepsItsAnswerTests
{
    private const string Reference =
        """
        WITH written AS (
            -- Where each message was written: a move rewrites conversation_id, so a message on an exchange
            -- moved after it was written is placed by that move's source, the first such move's. "After" is by
            -- message id, which follows the broker's order exactly; a timestamp can tie with a move.
            SELECT msg.id, msg.exchange_id, msg.type, msg.from_agent, msg.to_agent,
                   COALESCE(
                       (SELECT m.source
                          FROM conversation_move_exchanges e
                          JOIN conversation_moves m ON m.id = e.move_id
                         WHERE e.exchange_id = msg.exchange_id
                           AND COALESCE(m.last_message, 0) >= msg.id
                         ORDER BY m.id
                         LIMIT 1),
                       msg.conversation_id) AS conversation_id
              FROM messages msg
             WHERE msg.id >= (SELECT MIN(id) FROM messages WHERE conversation_id = @Id)
        ), seen AS (
            SELECT id, exchange_id, conversation_id, type, from_agent AS agent, to_agent AS other, 1 AS wrote
              FROM written
            UNION ALL
            SELECT id, exchange_id, conversation_id, type, to_agent, from_agent, 0
              FROM written
        ), ordered AS (
            SELECT id, exchange_id, conversation_id, type, agent, other, wrote,
                   LAG(conversation_id) OVER (PARTITION BY agent ORDER BY id) AS before_conversation,
                   LAG(id) OVER (PARTITION BY agent ORDER BY id) AS before_id
              FROM seen
        )
        SELECT id AS Message, before_id AS AfterMessage, agent AS Author, other AS Recipient,
               conversation_id AS Conversation
          FROM ordered
         WHERE wrote = 1
           AND before_conversation = @Id
           AND conversation_id <> @Id
           AND type IN ('request', 'clarify')
           AND agent NOT LIKE @Person AND other NOT LIKE @Person
           AND agent NOT LIKE @Facilitator AND other NOT LIKE @Facilitator
         ORDER BY id;
        """;

    [Fact]
    public async Task The_narrowed_read_answers_what_the_full_scan_answered()
    {
        var withRows = 0;

        for (var seed = 1; seed <= 150; seed++)
        {
            using var connection = ParleyDatabase.OpenInMemory();
            Build(connection, new Random(seed));

            foreach (var conversation in new[] { "c1", "c2", "c3", "c4" })
            {
                var expected = (await connection.QueryAsync<(long Message, long AfterMessage, string Author, string Recipient, string Conversation)>(
                    Reference + ";",
                    new { Id = conversation, Person = "human-user@%", Facilitator = "parley-facilitator@%" })).ToList();

                var actual = await new ConversationMoveRepository(connection).GetWroteElsewhereAsync(
                    conversation, TestContext.Current.CancellationToken);

                Assert.Equal(
                    expected.Select(r => (r.Message, r.AfterMessage, r.Author, r.Recipient, r.Conversation)),
                    actual.Select(r => (r.Message, r.AfterMessage, r.From.Value, r.To.Value, r.Conversation)));

                if (expected.Count > 0)
                {
                    withRows++;
                }
            }
        }

        Assert.True(withRows >= 30, $"only {withRows} cases returned rows, so agreement proves little");
    }

    private static void Build(SqliteConnection connection, Random random)
    {
        connection.Execute("PRAGMA foreign_keys = OFF;");

        foreach (var trigger in connection.Query<string>("SELECT name FROM sqlite_master WHERE type = 'trigger' AND tbl_name IN ('messages', 'exchanges');").ToList())
        {
            connection.Execute($"DROP TRIGGER \"{trigger}\";");
        }

        string[] agents = ["a@t", "b@t", "c@t", "d@t", "human-user@t", "parley-facilitator@t"];
        string[] conversations = ["c1", "c2", "c3", "c4"];
        string[] types = ["request", "clarify", "result"];
        var messages = random.Next(10, 60);
        var exchanges = random.Next(2, 9);

        for (var i = 1; i <= messages; i++)
        {
            var from = agents[random.Next(agents.Length)];
            string to;

            do
            {
                to = agents[random.Next(agents.Length)];
            }
            while (to == from);

            connection.Execute(
                "INSERT INTO messages (id, exchange_id, type, from_agent, to_agent, responder_model, payload_json, content_hash, conversation_id, created_utc) " +
                "VALUES (@Id, @Exchange, @Type, @From, @To, 'm', '{}', @Hash, @Conversation, '2026-11-19T10:00:00Z');",
                new
                {
                    Id = i,
                    Exchange = random.Next(1, exchanges + 1),
                    Type = types[random.Next(types.Length)],
                    From = from,
                    To = to,
                    Hash = i.ToString("x", System.Globalization.CultureInfo.InvariantCulture).PadLeft(64, '0'),
                    Conversation = conversations[random.Next(conversations.Length)],
                });
        }

        var moves = random.Next(0, 5);

        for (var m = 1; m <= moves; m++)
        {
            var source = conversations[random.Next(conversations.Length)];
            string destination;

            do
            {
                destination = conversations[random.Next(conversations.Length)];
            }
            while (destination == source);

            connection.Execute(
                "INSERT INTO conversation_moves (id, source, destination, moved_by, moved_utc, first_message, messages, last_message) " +
                "VALUES (@Id, @Source, @Destination, 'human-user@t', '2026-11-19T10:00:00Z', NULL, 0, @Last);",
                new { Id = m, Source = source, Destination = destination, Last = random.Next(3) == 0 ? (long?)null : random.Next(0, messages + 1) });

            foreach (var exchange in Enumerable.Range(1, exchanges).Where(_ => random.Next(2) == 0))
            {
                connection.Execute("INSERT INTO conversation_move_exchanges (move_id, exchange_id) VALUES (@M, @E);", new { M = m, E = exchange });
            }
        }
    }
}
