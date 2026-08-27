using System.Globalization;
using Microsoft.Data.Sqlite;
using Parley.Broker;
using Parley.Core;

namespace Parley.Bridge;

/// <summary>One agent, as the board shows it.</summary>
/// <param name="Name">The agent's name.</param>
/// <param name="Status">What its heartbeat says it is now.</param>
/// <param name="Tier">How it receives messages.</param>
/// <param name="Workspace">Where it works.</param>
/// <param name="Owns">What it says it owns.</param>
/// <param name="LastSeenUtc">When it last swept, or <see langword="null"/> if never.</param>
/// <param name="Uncollected">Messages sent to it that it has not collected.</param>
/// <param name="Owed">Open questions it has not answered.</param>
/// <param name="Hosted">Whether this bridge is the process that started it.</param>
public sealed record AgentView(
    string Name,
    string Status,
    string Tier,
    string? Workspace,
    string? Owns,
    string? LastSeenUtc,
    int Uncollected,
    int Owed,
    bool Hosted);

/// <summary>One message, as the traffic feed shows it.</summary>
/// <param name="Id">The message's identifier.</param>
/// <param name="Type">Question, answer, notify, and so on.</param>
/// <param name="From">Who sent it.</param>
/// <param name="To">Who it is for.</param>
/// <param name="CreatedUtc">When it was sent.</param>
/// <param name="DeliveredUtc">When it was collected, or <see langword="null"/> if it is still waiting.</param>
public sealed record TrafficView(
    long Id, string Type, string From, string To, string CreatedUtc, string? DeliveredUtc);

/// <summary>One exchange that is still open.</summary>
/// <param name="Id">The exchange's identifier.</param>
/// <param name="State">Where it has got to.</param>
/// <param name="Asker">Who asked.</param>
/// <param name="Answerer">Who was asked.</param>
/// <param name="OwedBy">Who owes the next move.</param>
/// <param name="OpenedUtc">When it was opened.</param>
public sealed record ExchangeView(
    long Id, string State, string Asker, string Answerer, string? OwedBy, string OpenedUtc);

/// <summary>Everything the board draws, in one reading.</summary>
/// <param name="Agents">The fleet.</param>
/// <param name="Traffic">Recent messages, newest first.</param>
/// <param name="Exchanges">Exchanges that have not concluded.</param>
/// <param name="ReadUtc">When this was read.</param>
public sealed record FleetSnapshot(
    IReadOnlyList<AgentView> Agents,
    IReadOnlyList<TrafficView> Traffic,
    IReadOnlyList<ExchangeView> Exchanges,
    string ReadUtc);

/// <summary>
/// Reads the whole board in one pass.
/// </summary>
/// <remarks>
/// <para>
/// The agent list comes from <see cref="OperatorConsole"/> rather than from SQL here, and that is
/// not laziness. That method derives liveness from the heartbeat instead of trusting the stored
/// column, which is the difference between a board that reports what is true and one that paints
/// a session that died last Tuesday bright green for ever. Re-implementing the query would have
/// re-implemented the bug this project spent a week removing.
/// </para>
/// <para>
/// The counts are the columns that were never built and were repeatedly wished for: a live agent
/// with three uncollected messages and a live agent with none look identical in the registry, and
/// they are the two cases an operator most needs to tell apart.
/// </para>
/// </remarks>
/// <param name="console">The operator surface. Every read here is one it already offers.</param>
/// <param name="connection">The connection to count against.</param>
public sealed class FleetView(OperatorConsole console, SqliteConnection connection)
{
    private const int TrafficWindow = 200;

    private readonly OperatorConsole _console =
        console ?? throw new ArgumentNullException(nameof(console));

    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>
    /// Reads the board.
    /// </summary>
    /// <param name="hosted">Names this bridge is currently running as child processes.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>What to draw.</returns>
    public async Task<FleetSnapshot> ReadAsync(
        IReadOnlySet<string> hosted, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hosted);

        var registry = await _console.DescribeRegistryAsync(cancellationToken).ConfigureAwait(false);

        var uncollected = await CountAsync(Tally.Uncollected, cancellationToken)
            .ConfigureAwait(false);

        var owed = await CountAsync(Tally.Owed, cancellationToken).ConfigureAwait(false);

        var agents = registry
            .Select(a => new AgentView(
                a.Name.Value,
                AgentStatusFormat(a.Status),
                a.Tier.ToString().ToLowerInvariant(),
                a.Workspace,
                a.Owns,
                a.LastSeenUtc?.ToString("O", CultureInfo.InvariantCulture),
                uncollected.GetValueOrDefault(a.Name.Value),
                owed.GetValueOrDefault(a.Name.Value),
                hosted.Contains(a.Name.Value)))
            .ToArray();

        return new FleetSnapshot(
            agents,
            await TrafficAsync(cancellationToken).ConfigureAwait(false),
            await ExchangesAsync(cancellationToken).ConfigureAwait(false),
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
    }

    private static string AgentStatusFormat(AgentStatus status) => status switch
    {
        AgentStatus.NotResponding => "not responding",
        _ => status.ToString().ToLowerInvariant()
    };

    /// <summary>What a per-agent count is counting.</summary>
    private enum Tally
    {
        /// <summary>Messages sent to an agent that it has not collected.</summary>
        Uncollected,

        /// <summary>Open questions an agent has not answered.</summary>
        Owed
    }

    private async Task<Dictionary<string, int>> CountAsync(
        Tally tally, CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();

        // Assigned from a literal on both branches rather than taken as a string parameter. The
        // parameterised shape reads better and is what CA2100 exists to stop: a query built
        // somewhere other than where it is run is one a later caller can build from input.
        if (tally == Tally.Uncollected)
        {
            command.CommandText =
                "SELECT to_agent, count(*) FROM messages WHERE delivered_utc IS NULL " +
                "GROUP BY to_agent;";
        }
        else
        {
            command.CommandText =
                "SELECT owed_by, count(*) FROM exchanges WHERE state = 'OPEN' " +
                "AND owed_by IS NOT NULL GROUP BY owed_by;";
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        var reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        await using var closing = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            counts[reader.GetString(0)] = reader.GetInt32(1);
        }

        return counts;
    }

    private async Task<IReadOnlyList<TrafficView>> TrafficAsync(CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();

        command.CommandText =
            "SELECT id, type, from_agent, to_agent, created_utc, delivered_utc " +
            "FROM messages ORDER BY id DESC LIMIT @Window;";

        command.Parameters.AddWithValue("@Window", TrafficWindow);

        var traffic = new List<TrafficView>();

        var reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        await using var closing = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            traffic.Add(new TrafficView(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return traffic;
    }

    private async Task<IReadOnlyList<ExchangeView>> ExchangesAsync(CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();

        // Everything unconcluded, not only OPEN. An exchange that reached ORPHANED is the single
        // most important row on this board -- it is a question that died undelivered -- and a
        // filter on OPEN would hide exactly the failure the board exists to surface.
        command.CommandText =
            "SELECT id, state, asker, answerer, owed_by, opened_utc " +
            "FROM exchanges WHERE state <> 'CONCLUDED' ORDER BY id DESC;";

        var exchanges = new List<ExchangeView>();

        var reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        await using var closing = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            exchanges.Add(new ExchangeView(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5)));
        }

        return exchanges;
    }
}
