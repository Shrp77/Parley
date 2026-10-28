using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>Problems with the file location, kept until the person dismisses them (SPEC.md §9.5).</summary>
/// <remarks>
/// Written by the broker's sweep alone and read and dismissed by the person alone (§10.2): no agent operation reaches
/// it, because what was found on disk is nobody's message and may be anybody's text.
/// </remarks>
public sealed class FileProblemRepository(SqliteConnection connection)
{
    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>Raises a problem, unless the same entry already has one of the same kind waiting.</summary>
    /// <param name="kind">What happened.</param>
    /// <param name="conversationId">The conversation whose folder held it, or <see langword="null"/>.</param>
    /// <param name="path">The entry, as a path inside the location.</param>
    /// <param name="placed">For a quarantined file, the hash placed.</param>
    /// <param name="found">For a quarantined file, the hash found.</param>
    /// <param name="movedTo">For a quarantined file, where it now is.</param>
    /// <param name="detail">The broker's sentence about it.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> when a problem was raised.</returns>
    /// <remarks>
    /// Once per entry while it waits: a file another program holds open is found again by every sweep, and a problem
    /// per sweep would bury the first. A quarantined file is a new file each time, so it is never folded into another.
    /// </remarks>
    public async Task<bool> RaiseAsync(
        FileProblemKind kind,
        string? conversationId,
        string path,
        ContentHash? placed,
        ContentHash? found,
        string? movedTo,
        string detail,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(detail);

        var raised = await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO file_problems
                    (kind, conversation_id, path, sha256_placed, sha256_found, moved_to, detail, raised_utc)
                SELECT @Kind, @Conversation, @Path, @Placed, @Found, @MovedTo, @Detail, @When
                 WHERE @Kind = 'quarantined'
                    OR NOT EXISTS (SELECT 1 FROM file_problems
                                    WHERE path = @Path AND kind = @Kind AND dismissed_utc IS NULL);
                """,
                new
                {
                    Kind = Format(kind),
                    Conversation = conversationId,
                    Path = path,
                    Placed = placed?.Value,
                    Found = found?.Value,
                    MovedTo = movedTo,
                    Detail = detail,
                    When = ExchangeRow.FormatTimestamp(nowUtc),
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return raised == 1;
    }

    /// <summary>Lists problems, those waiting first and each oldest first.</summary>
    /// <param name="includeDismissed">Whether to include those the person dismissed.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The problems.</returns>
    public async Task<IReadOnlyList<FileProblem>> ListAsync(
        bool includeDismissed,
        CancellationToken cancellationToken = default) =>
        [
            .. (await _connection.QueryAsync<ProblemRow>(
                new CommandDefinition(
                    """
                    SELECT id AS Id, kind AS Kind, conversation_id AS ConversationId, path AS Path,
                           sha256_placed AS Placed, sha256_found AS Found, moved_to AS MovedTo, detail AS Detail,
                           raised_utc AS RaisedUtc, dismissed_utc AS DismissedUtc
                      FROM file_problems
                     WHERE @All OR dismissed_utc IS NULL
                     ORDER BY dismissed_utc IS NOT NULL, id;
                    """,
                    new { All = includeDismissed },
                    cancellationToken: cancellationToken)).ConfigureAwait(false))
            .Select(r => r.ToDomain()),
        ];

    /// <summary>Counts the problems the person has not dismissed.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The count.</returns>
    public Task<int> CountWaitingAsync(CancellationToken cancellationToken = default) =>
        _connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT count(*) FROM file_problems WHERE dismissed_utc IS NULL;",
                cancellationToken: cancellationToken));

    /// <summary>Dismisses a problem.</summary>
    /// <param name="id">The problem.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> when a waiting problem was dismissed.</returns>
    /// <remarks>Dismissing deletes nothing on disk: a quarantined file stays for the person to look at or delete.</remarks>
    public async Task<bool> DismissAsync(long id, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
        await _connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE file_problems SET dismissed_utc = @When WHERE id = @Id AND dismissed_utc IS NULL;",
                new { Id = id, When = ExchangeRow.FormatTimestamp(nowUtc) },
                cancellationToken: cancellationToken)).ConfigureAwait(false) == 1;

    private static string Format(FileProblemKind kind) => kind switch
    {
        FileProblemKind.Quarantined => "quarantined",
        FileProblemKind.Locked => "locked",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a kind of file problem."),
    };

    private sealed class ProblemRow
    {
        public long Id { get; init; }

        public string Kind { get; init; } = "";

        public string? ConversationId { get; init; }

        public string Path { get; init; } = "";

        public string? Placed { get; init; }

        public string? Found { get; init; }

        public string? MovedTo { get; init; }

        public string Detail { get; init; } = "";

        public string RaisedUtc { get; init; } = "";

        public string? DismissedUtc { get; init; }

        public FileProblem ToDomain() => new(
            Id,
            Kind == "quarantined" ? FileProblemKind.Quarantined : FileProblemKind.Locked,
            ConversationId,
            Path,
            Placed is null ? null : ContentHash.Parse(Placed),
            Found is null ? null : ContentHash.Parse(Found),
            MovedTo,
            Detail,
            ExchangeRow.ParseTimestamp(RaisedUtc),
            DismissedUtc is null ? null : ExchangeRow.ParseTimestamp(DismissedUtc));
    }
}
