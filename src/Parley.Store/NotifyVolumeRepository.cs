using Microsoft.Data.Sqlite;

namespace Parley.Store;

/// <summary>
/// What one agent has sent another as notifications.
/// </summary>
/// <param name="From">The sender.</param>
/// <param name="To">The recipient.</param>
/// <param name="Sent">How many notifications.</param>
/// <param name="Characters">Their total size, measured as rule 8.5 measures a message.</param>
/// <param name="Largest">The biggest single one.</param>
/// <param name="FirstUtc">When the first was sent.</param>
/// <param name="LastUtc">When the most recent was sent.</param>
public sealed record NotifyFlow(
    string From, string To, long Sent, long Characters, long Largest,
    string FirstUtc, string LastUtc)
{
    /// <summary>Gets the mean size of a notification on this flow.</summary>
    public long Mean => Sent == 0 ? 0 : Characters / Sent;
}

/// <summary>
/// The volume Notify actually carries, for SPEC.md §17 question 1.
/// </summary>
/// <param name="Flows">One entry per sending pair, busiest first.</param>
/// <remarks>
/// <para>
/// The question records that nothing bounds Notify and that the answer is to measure rather than
/// guess a limit. Two numbers decide it — how many and how big — and this keeps both past the
/// retention that removes the messages themselves.
/// </para>
/// <para>
/// A rollup rather than a row per message, unlike the referral record. A referral names a third
/// agent and a counter cannot hold which; a notification's content is prose that is pruned with
/// the message and should be, so nothing a decision needs is lost by counting.
/// </para>
/// </remarks>
public sealed record NotifyVolumeReport(IReadOnlyList<NotifyFlow> Flows)
{
    /// <summary>Gets whether anything has been notified at all.</summary>
    public bool IsEmpty => Flows is null || Flows.Count == 0;

    /// <summary>Gets every notification sent, across all flows.</summary>
    public long Sent => Flows?.Sum(f => f.Sent) ?? 0;

    /// <summary>Gets the mean size of a notification across all flows.</summary>
    /// <remarks>
    /// Weighted by the messages rather than averaged across the flows, so one pair sending a
    /// thousand short notices is not balanced out by a pair that sent one long one.
    /// </remarks>
    public long Mean
    {
        get
        {
            var sent = Sent;
            return sent == 0 ? 0 : (Flows?.Sum(f => f.Characters) ?? 0) / sent;
        }
    }

    /// <summary>
    /// How many notifications each recipient has received in total.
    /// </summary>
    /// <remarks>
    /// The number a survey threshold reads. Asking an agent whether notifications are worth
    /// having is only meaningful once it has had enough to judge, and the count that matters to
    /// it is everything it received rather than what any one peer sent.
    /// </remarks>
    public IReadOnlyList<(string Agent, long Received)> Received =>
        (Flows ?? [])
            .GroupBy(f => f.To, StringComparer.Ordinal)
            .Select(g => (Agent: g.Key, Received: g.Sum(f => f.Sent)))
            .OrderByDescending(x => x.Received)
            .ThenBy(x => x.Agent, StringComparer.Ordinal)
            .ToArray();
}

/// <summary>
/// Reads what Notify has carried, for the operator report of SPEC.md §10.2.
/// </summary>
/// <param name="connection">The open, migrated database.</param>
public sealed class NotifyVolumeRepository(SqliteConnection connection)
{
    private readonly SqliteConnection _connection = connection;

    /// <summary>Reads every flow.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The report, empty when nothing has been notified.</returns>
    /// <remarks>
    /// Read through the raw reader rather than Dapper, for the reason recorded on
    /// <see cref="ClarifyReportRepository"/>: SQLite gives an aggregate no declared type, and over
    /// an empty table the mapping fails — which is the state this is first run in.
    /// </remarks>
    public async Task<NotifyVolumeReport> ReadAsync(CancellationToken cancellationToken = default)
    {
        var flows = new List<NotifyFlow>();

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT from_agent, to_agent, sent, characters, largest, first_utc, last_utc
              FROM notify_volume
             ORDER BY sent DESC, from_agent, to_agent;
            """;

        using var reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            flows.Add(new NotifyFlow(
                reader.GetString(0), reader.GetString(1),
                reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4),
                reader.GetString(5), reader.GetString(6)));
        }

        return new NotifyVolumeReport(flows);
    }
}
