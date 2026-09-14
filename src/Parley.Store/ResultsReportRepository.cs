using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// Reads what Results have said across a deployment, for SPEC.md §10.2's <c>results()</c>.
/// </summary>
/// <param name="connection">The open, migrated database.</param>
/// <remarks>
/// <para>
/// Read from <c>result_items</c> and <c>result_sources</c>, which the schema fills on every Result and
/// retention never touches (§9.3), and from the conversations' own record of who closed them — so it
/// answers long after the payloads that carried the outcomes have been pruned.
/// </para>
/// <para>
/// Every query groups, so an empty table yields no rows rather than one row of untyped aggregates, which
/// is the case that forces <see cref="ClarifyReportRepository"/> onto the raw reader.
/// </para>
/// </remarks>
public sealed class ResultsReportRepository(SqliteConnection connection)
{
    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>Reads the whole report.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The report, empty on a deployment nothing has happened in.</returns>
    /// <exception cref="InvalidOperationException">The store holds a word this build does not know.</exception>
    public async Task<ResultsReport> ReadAsync(CancellationToken cancellationToken = default)
    {
        using var snapshot = _connection.BeginTransaction(deferred: true);

        var outcomes = await _connection.QueryAsync<OutcomeRow>(
            new CommandDefinition(
                """
                SELECT from_agent AS Agent, responder_model AS ResponderModel, outcome AS Outcome,
                       count(*) AS Items
                  FROM result_items
                 GROUP BY from_agent, responder_model, outcome
                 ORDER BY from_agent, responder_model, outcome;
                """,
                transaction: snapshot,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var blockers = await _connection.QueryAsync<BlockerRow>(
            new CommandDefinition(
                """
                SELECT from_agent AS Agent, blocker AS Blocker, count(*) AS Items
                  FROM result_items
                 WHERE blocker IS NOT NULL
                 GROUP BY from_agent, blocker
                 ORDER BY from_agent, blocker;
                """,
                transaction: snapshot,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var sources = await _connection.QueryAsync<SourceRow>(
            new CommandDefinition(
                """
                SELECT from_agent AS Agent, source AS Source, count(*) AS Items
                  FROM result_sources
                 GROUP BY from_agent, source
                 ORDER BY count(*) DESC, from_agent, source;
                """,
                transaction: snapshot,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var closures = await _connection.QueryAsync<ClosureRow>(
            new CommandDefinition(
                """
                SELECT p.agent AS Agent,
                       sum(c.closed_by = 'consensus') AS ByConsensus,
                       sum(c.closed_by = 'person') AS ByPerson
                  FROM conversation_participants p
                  JOIN conversations c ON c.id = p.conversation_id
                 WHERE c.state = 'CLOSED' AND c.closed_by IS NOT NULL
                 GROUP BY p.agent
                 ORDER BY p.agent;
                """,
                transaction: snapshot,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new ResultsReport(
            [.. outcomes.Select(r => new OutcomeCount(
                r.Agent, r.ResponderModel,
                OutcomeTokens.TryParse(r.Outcome, out Outcome outcome) ? outcome : throw Unrecognised(r.Outcome),
                r.Items))],
            [.. blockers.Select(r => new BlockerCount(
                r.Agent,
                OutcomeTokens.TryParse(r.Blocker, out BlockerKind kind) ? kind : throw Unrecognised(r.Blocker),
                r.Items))],
            [.. sources.Select(r => new SourceCount(r.Agent, r.Source, r.Items))],
            [.. closures.Select(r => new ClosureCount(r.Agent, r.ByConsensus, r.ByPerson))]);
    }

    private static InvalidOperationException Unrecognised(string word) =>
        new($"The store holds a result word this build does not know: '{word}'.");

    private sealed class OutcomeRow
    {
        public string Agent { get; init; } = "";

        public string ResponderModel { get; init; } = "";

        public string Outcome { get; init; } = "";

        public long Items { get; init; }
    }

    private sealed class BlockerRow
    {
        public string Agent { get; init; } = "";

        public string Blocker { get; init; } = "";

        public long Items { get; init; }
    }

    private sealed class SourceRow
    {
        public string Agent { get; init; } = "";

        public string Source { get; init; } = "";

        public long Items { get; init; }
    }

    private sealed class ClosureRow
    {
        public string Agent { get; init; } = "";

        public long ByConsensus { get; init; }

        public long ByPerson { get; init; }
    }
}
