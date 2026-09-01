using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

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
