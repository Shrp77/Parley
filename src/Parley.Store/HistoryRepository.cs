using System.Globalization;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>What the store holds of one exchange for a read: where it is, how it stands, and a run of its messages.</summary>
/// <param name="ConversationId">The conversation it is in now.</param>
/// <param name="State">How it stands, in words.</param>
/// <param name="Messages">The messages from the cursor, up to the bound, never fewer than one while any remain.</param>
/// <param name="Remaining">How many messages are left after these.</param>
public sealed record ExchangeSlice(string ConversationId, string State, IReadOnlyList<Message> Messages, int Remaining);

/// <summary>What the store holds of a search: a page of matches and how many more.</summary>
/// <param name="Matches">The page, newest first.</param>
/// <param name="NotShown">How many more match.</param>
public sealed record SearchSlice(IReadOnlyList<HistoryMatch> Matches, int NotShown);

/// <summary>Reads the project's past for an agent, and records that it did (SPEC.md §5.8, §10.1).</summary>
/// <remarks>
/// <para>
/// <b>Reads only, and one row.</b> Nothing here marks a message delivered, appends, moves an exchange or touches a
/// participant; the single write is the <c>history_reads</c> row the person sees, made in the same transaction as the
/// read so a read is never unseen. It is the only repository an agent's reading goes through, so the guarantee that a
/// read changes nothing a participant can observe is held in one file.
/// </para>
/// <para>
/// <b>Search is literal.</b> Each word is matched with <c>instr</c> against the decoded string values of the payload
/// (<c>json_tree</c>), never with <c>LIKE</c> or <c>MATCH</c>, so a term made of operators is only text. The statement is a
/// constant with a fixed number of word slots, because a statement built from the words would be the one place a
/// word could become SQL (CA2100). <c>lower</c> folds the letters A to Z only; a word in another script is matched as
/// typed.
/// </para>
/// </remarks>
/// <param name="connection">An open connection to the Parley database, already migrated.</param>
public sealed class HistoryRepository(SqliteConnection connection)
{
    /// <summary>The most words one search takes: the statement has this many slots.</summary>
    public const int MaxWords = 8;

    private const string Columns =
        """
        SELECT id              AS Id,
               exchange_id     AS ExchangeId,
               type            AS Type,
               from_agent      AS FromAgent,
               to_agent        AS ToAgent,
               responder_model AS ResponderModel,
               payload_json    AS PayloadJson,
               conversation_id AS ConversationId,
               created_utc     AS CreatedUtc,
               delivered_utc   AS DeliveredUtc
        """;

    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>Reads one exchange's messages from a cursor, and records the read.</summary>
    /// <param name="reader">The agent reading.</param>
    /// <param name="exchangeId">The exchange.</param>
    /// <param name="after">Only messages with a greater identity; zero for the start.</param>
    /// <param name="nowUtc">The instant, for the record.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The slice, or <see langword="null"/> when no exchange here has that number.</returns>
    public async Task<ExchangeSlice?> ReadExchangeAsync(
        AgentName reader,
        long exchangeId,
        long after,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        using var transaction = (SqliteTransaction)await _connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var exchange = await _connection.QueryFirstOrDefaultAsync<(string Conversation, string State, string? Outcome)?>(
            new CommandDefinition(
                "SELECT conversation_id, state, outcome FROM exchanges WHERE id = @Id;",
                new { Id = exchangeId },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        ExchangeSlice? slice = null;

        if (exchange is { } found)
        {
            var rows = (await _connection.QueryAsync<MessageRow>(
                new CommandDefinition(
                    $"""
                    {Columns}
                    FROM messages
                    WHERE exchange_id = @Exchange AND id > @After
                    ORDER BY id;
                    """,
                    new { Exchange = exchangeId, After = after },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

            var taken = new List<Message>();
            var characters = 0;

            foreach (var row in rows)
            {
                // Never fewer than one, so a message larger than the bound still arrives; then as many as fit.
                if (taken.Count > 0 && characters + row.PayloadJson.Length > HistoryLimits.ReplyCharacters)
                {
                    break;
                }

                characters += row.PayloadJson.Length;
                taken.Add(row.ToDomain());
            }

            var state = string.Equals(found.State, "OPEN", StringComparison.Ordinal)
                ? "open"
                : "closed" + (found.Outcome is { Length: > 0 } outcome ? ", " + outcome.ToLowerInvariant() : string.Empty);

            slice = new ExchangeSlice(found.Conversation, state, taken, rows.Count - taken.Count);
        }

        await RecordAsync(transaction, reader, "exchange", exchangeId, null, null, nowUtc, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return slice;
    }

    /// <summary>Searches the messages for words, and records the search.</summary>
    /// <param name="reader">The agent searching.</param>
    /// <param name="words">The words, already split, lower-cased and counted by the caller: at most <see cref="MaxWords"/>.</param>
    /// <param name="spoken">The words as the caller said them, for the record.</param>
    /// <param name="filter">What narrows the search.</param>
    /// <param name="continueAfter">Only messages older than this identity; zero for the first page.</param>
    /// <param name="nowUtc">The instant.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The page.</returns>
    public async Task<SearchSlice> SearchAsync(
        AgentName reader,
        IReadOnlyList<string> words,
        string spoken,
        HistoryFilter filter,
        long continueAfter,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(filter);

        if (words.Count is 0 or > MaxWords)
        {
            throw new ArgumentException($"A search takes between one and {MaxWords} words.", nameof(words));
        }

        string? Slot(int i) => i < words.Count ? words[i] : null;

        var parameters = new
        {
            W1 = Slot(0), W2 = Slot(1), W3 = Slot(2), W4 = Slot(3), W5 = Slot(4), W6 = Slot(5), W7 = Slot(6), W8 = Slot(7),
            Agent = filter.Agent,
            Conversation = filter.Conversation,
            Type = filter.Type is { } type ? MessageIdentity.FormatType(type) : null,
            Since = filter.Since?.ToString("O", CultureInfo.InvariantCulture),
            Until = filter.Until?.ToString("O", CultureInfo.InvariantCulture),
            Cursor = continueAfter,
            Limit = HistoryLimits.PageMatches,
        };

        using var transaction = (SqliteTransaction)await _connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var rows = (await _connection.QueryAsync<MessageRow>(
            new CommandDefinition(
                $"""
                {Columns},
                       (SELECT t.atom FROM json_tree(payload_json) AS t
                         WHERE t.type = 'text' AND instr(lower(t.atom), @W1) > 0 LIMIT 1) AS MatchedText
                FROM messages
                WHERE {Where}
                ORDER BY id DESC
                LIMIT @Limit;
                """,
                parameters,
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var total = await _connection.QuerySingleAsync<int>(
            new CommandDefinition(
                $"SELECT COUNT(*) FROM messages WHERE {Where};",
                parameters,
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var matches = rows.Select(r => ToMatch(r, words)).ToList();
        var notShown = Math.Max(0, total - matches.Count);

        await RecordAsync(transaction, reader, "search", null, spoken, total, nowUtc, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new SearchSlice(matches, notShown);
    }

    /// <summary>The reads of one exchange, newest first, for the person.</summary>
    /// <param name="exchangeId">The exchange.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Every read of it that was made.</returns>
    public async Task<IReadOnlyList<HistoryRead>> ReadsOfExchangeAsync(long exchangeId, CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<ReadRow>(
            new CommandDefinition(
                """
                SELECT reader AS Reader, exchange_id AS ExchangeId, words AS Words, matches AS Matches, created_utc AS CreatedUtc
                FROM history_reads WHERE kind = 'exchange' AND exchange_id = @Exchange ORDER BY id DESC;
                """,
                new { Exchange = exchangeId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    /// <summary>The reads of every exchange in one conversation, newest first, for the person.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Every read of an exchange that is in it now.</returns>
    public async Task<IReadOnlyList<HistoryRead>> ReadsInConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<ReadRow>(
            new CommandDefinition(
                """
                SELECT r.reader AS Reader, r.exchange_id AS ExchangeId, r.words AS Words, r.matches AS Matches, r.created_utc AS CreatedUtc
                FROM history_reads AS r
                WHERE r.kind = 'exchange' AND r.exchange_id IN (SELECT id FROM exchanges WHERE conversation_id = @Conversation)
                ORDER BY r.id DESC;
                """,
                new { Conversation = conversationId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    /// <summary>The reads one agent made, newest first, for the person.</summary>
    /// <param name="reader">The agent.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Its reads and searches.</returns>
    public async Task<IReadOnlyList<HistoryRead>> ReadsByAsync(AgentName reader, CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<ReadRow>(
            new CommandDefinition(
                """
                SELECT reader AS Reader, exchange_id AS ExchangeId, words AS Words, matches AS Matches, created_utc AS CreatedUtc
                FROM history_reads WHERE reader = @Reader ORDER BY id DESC LIMIT 200;
                """,
                new { Reader = reader.Value },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    /// <summary>The condition a search applies. A constant, with a fixed number of word slots.</summary>
    private const string Where =
        """
        (@Cursor = 0 OR id < @Cursor)
        AND (@W1 IS NULL OR EXISTS (SELECT 1 FROM json_tree(payload_json) AS t WHERE t.type = 'text' AND instr(lower(t.atom), @W1) > 0))
        AND (@W2 IS NULL OR EXISTS (SELECT 1 FROM json_tree(payload_json) AS t WHERE t.type = 'text' AND instr(lower(t.atom), @W2) > 0))
        AND (@W3 IS NULL OR EXISTS (SELECT 1 FROM json_tree(payload_json) AS t WHERE t.type = 'text' AND instr(lower(t.atom), @W3) > 0))
        AND (@W4 IS NULL OR EXISTS (SELECT 1 FROM json_tree(payload_json) AS t WHERE t.type = 'text' AND instr(lower(t.atom), @W4) > 0))
        AND (@W5 IS NULL OR EXISTS (SELECT 1 FROM json_tree(payload_json) AS t WHERE t.type = 'text' AND instr(lower(t.atom), @W5) > 0))
        AND (@W6 IS NULL OR EXISTS (SELECT 1 FROM json_tree(payload_json) AS t WHERE t.type = 'text' AND instr(lower(t.atom), @W6) > 0))
        AND (@W7 IS NULL OR EXISTS (SELECT 1 FROM json_tree(payload_json) AS t WHERE t.type = 'text' AND instr(lower(t.atom), @W7) > 0))
        AND (@W8 IS NULL OR EXISTS (SELECT 1 FROM json_tree(payload_json) AS t WHERE t.type = 'text' AND instr(lower(t.atom), @W8) > 0))
        AND (@Agent IS NULL OR from_agent = @Agent OR to_agent = @Agent)
        AND (@Conversation IS NULL OR conversation_id = @Conversation)
        AND (@Type IS NULL OR type = @Type)
        AND (@Since IS NULL OR unixepoch(created_utc, 'subsec') >= unixepoch(@Since, 'subsec'))
        AND (@Until IS NULL OR unixepoch(created_utc, 'subsec') <= unixepoch(@Until, 'subsec'))
        """;

    private async Task RecordAsync(
        SqliteTransaction transaction,
        AgentName reader,
        string kind,
        long? exchangeId,
        string? words,
        int? matches,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken) =>
        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO history_reads (reader, kind, exchange_id, words, matches, created_utc)
                VALUES (@Reader, @Kind, @Exchange, @Words, @Matches, @Created);
                """,
                new
                {
                    Reader = reader.Value,
                    Kind = kind,
                    Exchange = exchangeId,
                    Words = words,
                    Matches = matches,
                    Created = nowUtc.ToString("O", CultureInfo.InvariantCulture),
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

    private static HistoryMatch ToMatch(MessageRow row, IReadOnlyList<string> words)
    {
        var message = row.ToDomain();

        return new HistoryMatch(
            message.ExchangeId,
            message.Id,
            message.ConversationId,
            message.From,
            message.To,
            message.Type,
            message.CreatedUtc,
            Snippet(row.MatchedText, words[0]));
    }

    /// <summary>A few words around the first place a word occurs, with control characters neutralised one for one.</summary>
    /// <param name="text">The string in the payload that held the word, or <see langword="null"/>.</param>
    /// <param name="word">The first word searched for, lower-cased.</param>
    /// <returns>At most <see cref="HistoryLimits.SnippetCharacters"/> characters.</returns>
    internal static string Snippet(string? text, string word)
    {
        if (text is null)
        {
            return string.Empty;
        }

        var at = Math.Max(0, text.IndexOf(word, StringComparison.OrdinalIgnoreCase));
        var room = HistoryLimits.SnippetCharacters;
        var start = Math.Max(0, at - (room - word.Length) / 2);

        // Do not start inside a surrogate pair.
        if (start > 0 && char.IsLowSurrogate(text[start]))
        {
            start--;
        }

        var end = Math.Min(text.Length, start + room);

        if (end < text.Length && end > start && char.IsHighSurrogate(text[end - 1]))
        {
            end--;
        }

        var builder = new StringBuilder(room);

        foreach (var c in text.AsSpan(start, end - start))
        {
            builder.Append(Neutral(c) ? ' ' : c);
        }

        return builder.ToString();
    }

    /// <summary>Whether a character could break, hide or reorder a line.</summary>
    /// <remarks>Spelled as numbers: a raw line separator in a literal breaks the build.</remarks>
    private static bool Neutral(char c) =>
        c < 0x20 || (c >= 0x7f && c <= 0x9f) || c == 0xad || (c >= 0x200b && c <= 0x200f)
        || (c >= 0x2028 && c <= 0x202e) || (c >= 0x2060 && c <= 0x206f) || c == 0xfeff;

    private sealed class ReadRow
    {
        public string Reader { get; init; } = "";

        public long? ExchangeId { get; init; }

        public string? Words { get; init; }

        public long? Matches { get; init; }

        public string CreatedUtc { get; init; } = "";

        public HistoryRead ToDomain() => new(
            AgentName.Create(Reader),
            ExchangeId,
            Words,
            Matches is { } m ? (int)m : null,
            ExchangeRow.ParseTimestamp(CreatedUtc));
    }
}
