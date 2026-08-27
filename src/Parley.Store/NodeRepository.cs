using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// What a machine reported about itself, per SPEC.md §4 and §4.1.1.
/// </summary>
/// <param name="Machine">The machine name, as the node detected it.</param>
/// <param name="Os">The operating system.</param>
/// <param name="OsVersion">Its version.</param>
/// <param name="Address">An address, for a person reading a listing.</param>
/// <param name="Domain">The domain the machine is joined to, if any.</param>
/// <param name="Connected">Whether a node is currently holding a connection for it.</param>
/// <param name="FirstSeenUtc">When the broker first heard of this machine.</param>
/// <remarks>
/// Everything but <paramref name="Machine"/> and <paramref name="Connected"/> is reported for a
/// person and used for nothing. §4 is explicit that a machine is reached through the connection
/// its node already holds and never by an address the broker dials — recording one the broker
/// then connected to would invert the trust direction of §13, letting a registration tell the
/// broker where to send traffic.
/// </remarks>
public sealed record NodeRecord(
    string Machine,
    string? Os,
    string? OsVersion,
    string? Address,
    string? Domain,
    bool Connected,
    DateTimeOffset FirstSeenUtc);

/// <summary>
/// Reads and writes the machines agents live on.
/// </summary>
/// <remarks>
/// <para>
/// A node's row exists as soon as any agent names its machine, because the machine half of a
/// name is a foreign key — so the interesting operation here is not creation but
/// <see cref="JoinAsync"/>, which records that something on that machine is now connected and
/// able to start things.
/// </para>
/// <para>
/// <b>Connected is a flag here and a count on an agent, and the difference is deliberate.</b>
/// SPEC.md §4 gives a machine one node, so a second connection claiming the same machine is a
/// misconfiguration rather than a second session — two supervisors on one box would both try to
/// start the same agents. An agent legitimately has several sessions at once, which is why that
/// one counts.
/// </para>
/// </remarks>
public sealed class NodeRepository
{
    private readonly SqliteConnection _connection;

    /// <summary>Creates a repository over an open connection.</summary>
    /// <param name="connection">The open, migrated connection.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    public NodeRepository(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
    }

    /// <summary>
    /// Records that a node is connected, and what it observed about its machine.
    /// </summary>
    /// <param name="node">What the node reported. Its connected flag is ignored.</param>
    /// <param name="nowUtc">The instant of the join.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the row is written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> is null.</exception>
    /// <remarks>
    /// The facts are overwritten on every join rather than written once, because they are
    /// observations rather than history: a machine that was renamed, moved to another subnet or
    /// upgraded should read as it is now. <c>first_seen_utc</c> is the one thing left alone, and
    /// is the only durable thing here.
    /// </remarks>
    public async Task JoinAsync(
        NodeRecord node,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);

        _ = await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO nodes (machine, os, os_version, address, domain,
                                   connected, changed_utc, first_seen_utc)
                VALUES (@Machine, @Os, @OsVersion, @Address, @Domain, 1, @Now, @Now)
                ON CONFLICT(machine) DO UPDATE SET
                    os          = excluded.os,
                    os_version  = excluded.os_version,
                    address     = excluded.address,
                    domain      = excluded.domain,
                    connected   = 1,
                    changed_utc = excluded.changed_utc;
                """,
                new
                {
                    node.Machine,
                    node.Os,
                    node.OsVersion,
                    node.Address,
                    node.Domain,
                    Now = ExchangeRow.FormatTimestamp(nowUtc)
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Records that a node's connection has ended.
    /// </summary>
    /// <param name="machine">The machine.</param>
    /// <param name="nowUtc">The instant.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the row is written.</returns>
    /// <remarks>
    /// The agents on the machine are left alone, and that is not an oversight. Each holds its own
    /// connection to the broker (§4: a node is not a relay), so each is decremented by its own
    /// stream ending — and a node that was stopped while its agents keep working has not made
    /// them unreachable, only unstartable.
    /// </remarks>
    public async Task LeaveAsync(
        string machine,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(machine);

        _ = await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE nodes
                   SET connected = 0, changed_utc = @Now
                 WHERE machine = @Machine;
                """,
                new { Machine = machine, Now = ExchangeRow.FormatTimestamp(nowUtc) },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Reads one machine.</summary>
    /// <param name="machine">The machine.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The record, or <see langword="null"/> if the broker has never heard of it.</returns>
    public async Task<NodeRecord?> GetAsync(
        string machine,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(machine);

        var row = await _connection.QuerySingleOrDefaultAsync<NodeRow>(
            new CommandDefinition(
                $"{SelectColumns} WHERE machine = @Machine;",
                new { Machine = machine },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row?.ToDomain();
    }

    /// <summary>Reads every machine the broker has heard of.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The machines, by name.</returns>
    public async Task<IReadOnlyList<NodeRecord>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<NodeRow>(
            new CommandDefinition(
                $"{SelectColumns} ORDER BY machine;",
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    /// <summary>Reads the agents registered on a machine.</summary>
    /// <param name="machine">The machine.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Their names, in order.</returns>
    /// <remarks>
    /// Retired agents are excluded. A node asks this to know what it may be told to start, and
    /// retirement is the one judgement that gates routing (§14) — an agent nothing may be sent
    /// to is an agent there is no reason to launch.
    /// </remarks>
    public async Task<IReadOnlyList<AgentName>> AgentsOnAsync(
        string machine,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(machine);

        var names = await _connection.QueryAsync<string>(
            new CommandDefinition(
                """
                SELECT name FROM agents
                 WHERE machine = @Machine AND status <> 'retired'
                 ORDER BY name;
                """,
                new { Machine = machine },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. names.Select(AgentName.Create)];
    }

    /// <summary>
    /// Marks every node disconnected, for a broker that has just started.
    /// </summary>
    /// <param name="nowUtc">The instant.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>How many rows were changed.</returns>
    /// <remarks>
    /// <para>
    /// A connection is evidence (§4.0), and a broker that has just started holds none — so every
    /// <c>connected</c> in the database is a claim left over from the process that died, and
    /// believing it would report a fleet as reachable when nothing is attached to anything.
    /// </para>
    /// <para>
    /// Run at startup rather than at shutdown, because the shutdown a broker most needs this
    /// after is the one where it did not get to run anything.
    /// </para>
    /// </remarks>
    public async Task<int> DisconnectAllAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE nodes
                   SET connected = 0, changed_utc = @Now
                 WHERE connected <> 0;
                """,
                new { Now = ExchangeRow.FormatTimestamp(nowUtc) },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

    private const string SelectColumns =
        """
        SELECT machine        AS Machine,
               os             AS Os,
               os_version     AS OsVersion,
               address        AS Address,
               domain         AS Domain,
               connected      AS Connected,
               first_seen_utc AS FirstSeenUtc
          FROM nodes
        """;

    private sealed record NodeRow(
        string Machine,
        string? Os,
        string? OsVersion,
        string? Address,
        string? Domain,
        long Connected,
        string FirstSeenUtc)
    {
        public NodeRecord ToDomain() =>
            new(Machine, Os, OsVersion, Address, Domain,
                Connected != 0, ExchangeRow.ParseTimestamp(FirstSeenUtc));
    }
}
