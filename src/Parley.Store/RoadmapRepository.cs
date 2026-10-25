using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// Roadmap proposals, roadmaps and the notices an agent is told about its own proposals (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>A proposal is stored apart from messages and is not a roadmap.</b> Nothing here opens an exchange or writes a
/// message, so a proposal can appear in no transcript, and nothing becomes a roadmap until
/// <see cref="ConfirmAsync"/> is called with a person's final content. No agent-facing operation reaches any method
/// here except <see cref="ProposeAsync"/> and <see cref="DrainNoticesAsync"/> (principle 2.12).
/// </para>
/// <para>
/// <b>Where an item stands is never written here.</b> <see cref="GetAsync"/> reads what the store holds about each
/// live item's conversation and returns it as <see cref="ItemFacts"/>; deriving a state from those facts is the
/// surface's single shared function, so that no column can disagree with it.
/// </para>
/// <para>
/// <b>Content is judged whole.</b> Storage holds the invariants that survive a writer that forgets, but a trigger on
/// insert cannot see a reorder, so confirming and editing apply <see cref="RoadmapRules"/> to the person's final
/// list and also check every intended agent is registered. A removed item is never the dependency of a live one
/// because the final content cannot name one.
/// </para>
/// </remarks>
public sealed class RoadmapRepository(SqliteConnection connection)
{
    /// <summary>Added to the positions of items being rearranged, so the unique index never sees two at once.</summary>
    private const int Shift = 1_000_000;

    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>Stores an agent's proposal.</summary>
    /// <param name="proposer">The agent proposing, which is the attached caller and never a parameter of the call.</param>
    /// <param name="sourceConversation">Where it came from, or <see langword="null"/>.</param>
    /// <param name="targetTitle">The title of a roadmap it would revise, or <see langword="null"/>.</param>
    /// <param name="content">The title and items as proposed.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The proposal's identifier, or a sentence saying why it was refused.</returns>
    /// <remarks>
    /// The proposer's refusal for the person seat and Parley is storage's (<c>tr_a_proposer_is_an_agent</c>), and a
    /// caller that asks first gets a sentence rather than a constraint's text. No length is checked: SPEC.md §16.
    /// </remarks>
    public async Task<(long? Id, string? Problem)> ProposeAsync(
        AgentName proposer,
        string? sourceConversation,
        string? targetTitle,
        RoadmapContent content,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (RoadmapRules.Problem(content) is { } problem)
        {
            return (null, problem);
        }

        // A proposal never names an existing item: it has none, and a number in it would be a claim about a
        // roadmap the proposer has no way of reading.
        if (content.Items.Any(i => i.Number is not null))
        {
            return (null, "A proposal's items are new. Name a roadmap to revise by its title instead.");
        }

        if (targetTitle is not null && !RoadmapText.IsValid(targetTitle, "The title of the roadmap to revise", out var targetProblem))
        {
            return (null, targetProblem);
        }

        using var scope = WriteScope.Begin(_connection, transaction);

        // An intended agent the project does not have is refused here with a sentence, as it is when confirming,
        // because the foreign key would refuse it with a constraint's text an agent cannot act on.
        if (await ContentProblemAsync(content, scope.Transaction, cancellationToken).ConfigureAwait(false) is { } unknown)
        {
            return (null, unknown);
        }

        // Every character of it, each need as its digits and a separator: a proposal of needs alone wrote a million
        // rows while its size read as its handful of words, and the size is the evidence a bound is chosen from
        // (SPEC.md §16).
        var size = (long)content.Title.Length + (targetTitle?.Length ?? 0)
            + content.Items.Sum(i => (long)i.Text.Length + i.Needs.Sum(need => (long)Digits(need) + 1));

        var id = await _connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                """
                INSERT INTO roadmap_proposals
                    (proposer, source_conversation, target_title, title, size, state, created_utc)
                VALUES (@Proposer, @Source, @Target, @Title, @Size, 'waiting', @When);
                SELECT last_insert_rowid();
                """,
                new
                {
                    Proposer = proposer.Value,
                    Source = sourceConversation,
                    Target = targetTitle,
                    content.Title,
                    Size = size,
                    When = ExchangeRow.FormatTimestamp(nowUtc),
                },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        for (var at = 0; at < content.Items.Count; at++)
        {
            var item = content.Items[at];

            await _connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO roadmap_proposal_items (proposal_id, position, text, intended_agent)
                    VALUES (@Id, @Position, @Text, @Agent);
                    """,
                    new { Id = id, Position = at + 1, item.Text, Agent = item.IntendedAgent?.Value },
                    scope.Transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        for (var at = 0; at < content.Items.Count; at++)
        {
            foreach (var needed in content.Items[at].Needs)
            {
                await _connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO roadmap_proposal_needs (proposal_id, position, needs_position)
                        VALUES (@Id, @Position, @Needs);
                        """,
                        new { Id = id, Position = at + 1, Needs = needed },
                        scope.Transaction,
                        cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }

        scope.Complete();

        return (id, null);
    }

    /// <summary>Reads the proposals that are in a state, or all of them.</summary>
    /// <param name="state">The state, or <see langword="null"/> for every one.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The proposals, newest first, each with its items in order.</returns>
    public async Task<IReadOnlyList<RoadmapProposal>> ListProposalsAsync(
        ProposalState? state, CancellationToken cancellationToken = default)
    {
        var rows = (await _connection.QueryAsync<ProposalRow>(
            new CommandDefinition(
                """
                SELECT id AS Id, proposer AS Proposer, source_conversation AS SourceConversation,
                       target_title AS TargetTitle, title AS Title, size AS Size, state AS State,
                       created_utc AS CreatedUtc, decided_utc AS DecidedUtc
                  FROM roadmap_proposals
                 WHERE @State IS NULL OR state = @State
                 ORDER BY id DESC;
                """,
                new { State = state is null ? null : ProposalStateText(state.Value) },
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var proposals = new List<RoadmapProposal>(rows.Count);

        foreach (var row in rows)
        {
            proposals.Add(await ToProposalAsync(row, null, cancellationToken).ConfigureAwait(false));
        }

        return proposals;
    }

    /// <summary>Counts the proposals in a state, or all of them.</summary>
    /// <param name="state">The state, or <see langword="null"/> for every one.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>How many there are.</returns>
    /// <remarks>A count reads no proposal at all, so it costs the same however many an agent made.</remarks>
    public async Task<int> CountProposalsAsync(ProposalState? state, CancellationToken cancellationToken = default) =>
        await _connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM roadmap_proposals WHERE @State IS NULL OR state = @State;",
                new { State = state is null ? null : ProposalStateText(state.Value) },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

    /// <summary>Reads the headings of the newest proposals in a state, or of all of them, and none of their items.</summary>
    /// <param name="state">The state, or <see langword="null"/> for every one.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// At most <see cref="RoadmapProposal.HeadingLimit"/> proposals, newest first, each titled to
    /// <see cref="RoadmapProposal.HeadingLength"/> with its item count.
    /// </returns>
    /// <remarks>
    /// The item text is never read and the number of rows is bounded, so a list costs the same whatever an agent wrote
    /// and however often it proposed (SPEC.md §10.2 <c>proposals</c>). <see cref="CountProposalsAsync"/> says how many
    /// there are in all.
    /// </remarks>
    public async Task<IReadOnlyList<RoadmapProposal>> ListProposalHeadingsAsync(
        ProposalState? state, CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<ProposalHeadingRow>(
            new CommandDefinition(
                """
                SELECT p.id AS Id, p.proposer AS Proposer, p.source_conversation AS SourceConversation,
                       substr(p.target_title, 1, @Length) AS TargetTitle, substr(p.title, 1, @Length) AS Title,
                       p.size AS Size, p.state AS State, p.created_utc AS CreatedUtc, p.decided_utc AS DecidedUtc,
                       (SELECT COUNT(*) FROM roadmap_proposal_items i WHERE i.proposal_id = p.id) AS Items
                  FROM roadmap_proposals p
                 WHERE @State IS NULL OR p.state = @State
                 ORDER BY p.id DESC
                 LIMIT @Limit;
                """,
                new
                {
                    State = state is null ? null : ProposalStateText(state.Value),
                    Length = RoadmapProposal.HeadingLength,
                    Limit = RoadmapProposal.HeadingLimit,
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(row => new RoadmapProposal(
            row.Id,
            AgentName.Create(row.Proposer),
            row.SourceConversation,
            row.TargetTitle,
            new RoadmapContent(row.Title, []),
            row.Size,
            ParseState(row.State),
            ExchangeRow.ParseTimestamp(row.CreatedUtc),
            row.DecidedUtc is null ? null : ExchangeRow.ParseTimestamp(row.DecidedUtc))
        {
            ItemCount = (int)row.Items,
        })];
    }

    /// <summary>Reads one proposal.</summary>
    /// <param name="id">The proposal.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The proposal, or <see langword="null"/>.</returns>
    public async Task<RoadmapProposal?> GetProposalAsync(long id, CancellationToken cancellationToken = default) =>
        await GetProposalCoreAsync(id, null, cancellationToken).ConfigureAwait(false);

    /// <summary>Turns a waiting proposal into a roadmap, or into a revision of one.</summary>
    /// <param name="id">The proposal.</param>
    /// <param name="final">The person's final content, which may be the proposal's own.</param>
    /// <param name="targetRoadmap">The roadmap it revises, or <see langword="null"/> for a new one.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What it came to.</returns>
    /// <remarks>
    /// <para>
    /// <b>Provenance is computed here and never passed in.</b> The origin is <c>proposed</c> only when the final
    /// content is the stored proposal's own words, in order, with the same agents and needs, and it is a new
    /// roadmap. A flag set by the caller would be right every time somebody remembered to set it.
    /// </para>
    /// <para>
    /// <b>One transaction.</b> The roadmap, its reserved item conversation names, the proposal's decision, the
    /// proposals this supersedes and every notice commit together or not at all, so a proposer is never told of a
    /// roadmap that was not written.
    /// </para>
    /// </remarks>
    public async Task<RoadmapChange> ConfirmAsync(
        long id,
        RoadmapContent final,
        long? targetRoadmap,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(final);

        using var scope = WriteScope.Begin(_connection, transaction);

        var proposal = await GetProposalCoreAsync(id, scope.Transaction, cancellationToken).ConfigureAwait(false);

        if (proposal is null)
        {
            return Refused("There is no such proposal.");
        }

        if (proposal.State != ProposalState.Waiting)
        {
            return Refused($"That proposal is already {ProposalStateText(proposal.State)}.");
        }

        var problem = await ContentProblemAsync(final, scope.Transaction, cancellationToken).ConfigureAwait(false);

        if (problem is not null)
        {
            return Refused(problem);
        }

        var now = ExchangeRow.FormatTimestamp(nowUtc);
        long roadmapId;
        int revision;
        RoadmapOrigin origin;

        // The title a waiting proposal's hint must name to be superseded: the revised roadmap's, as it stood before this
        // change. A new roadmap revises nothing, so it supersedes nothing (SPEC.md §5.7).
        string? revisedTitle = null;

        if (targetRoadmap is null)
        {
            if (final.Items.Any(i => i.Number is not null))
            {
                return Refused("A new roadmap has no existing items to carry across.");
            }

            origin = SameWords(proposal.Content, final) ? RoadmapOrigin.Proposed : RoadmapOrigin.Edited;

            var number = await _connection.ExecuteScalarAsync<int>(
                new CommandDefinition(
                    "SELECT COALESCE(MAX(number), 0) + 1 FROM roadmaps;",
                    transaction: scope.Transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

            roadmapId = await _connection.ExecuteScalarAsync<long>(
                new CommandDefinition(
                    """
                    INSERT INTO roadmaps (number, title, origin, proposer, origin_proposal, revision, created_utc, updated_utc)
                    VALUES (@Number, @Title, @Origin, @Proposer, @Proposal, 1, @When, @When);
                    SELECT last_insert_rowid();
                    """,
                    new
                    {
                        Number = number,
                        final.Title,
                        Origin = OriginText(origin),
                        Proposer = proposal.Proposer.Value,
                        Proposal = id,
                        When = now,
                    },
                    scope.Transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

            var written = await WriteContentAsync(
                roadmapId, number, 0, final, origin, now, scope.Transaction, cancellationToken).ConfigureAwait(false);

            if (written is not null)
            {
                return Refused(written);
            }

            revision = 1;
        }
        else
        {
            var existing = await _connection.QuerySingleOrDefaultAsync<NumberAndRevision>(
                new CommandDefinition(
                    "SELECT number AS Number, revision AS Revision FROM roadmaps WHERE id = @Id;",
                    new { Id = targetRoadmap.Value },
                    scope.Transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (existing is null)
            {
                return Refused("There is no such roadmap to revise.");
            }

            origin = RoadmapOrigin.Edited;
            roadmapId = targetRoadmap.Value;

            revisedTitle = await _connection.ExecuteScalarAsync<string>(
                new CommandDefinition(
                    "SELECT title FROM roadmaps WHERE id = @Id;",
                    new { Id = roadmapId },
                    scope.Transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

            var written = await WriteContentAsync(
                roadmapId, (int)existing.Number, (int)existing.Revision, final, origin, now, scope.Transaction,
                cancellationToken).ConfigureAwait(false);

            if (written is not null)
            {
                return Refused(written);
            }

            revision = (int)existing.Revision + 1;
        }

        await DecideAsync(proposal, ProposalState.Confirmed, now, scope.Transaction, cancellationToken)
            .ConfigureAwait(false);

        // Any other proposal that hints at the roadmap just revised is out of date. Matched on the title hint and nothing
        // else, because nothing else is known: an agent never reads a roadmap (SPEC.md §5.7). Not the confirmed title,
        // which a proposer chooses: that would let one agent's proposal decide another's aimed at a different roadmap.
        // Compared here, not in SQL: SQLite's lower() folds ASCII only, and the surfaces show a hint as naming a roadmap
        // with this same comparison (ProposalText.Matching), so the two agree on which proposals a revision decides.
        // Narrowed in SQL first, to hints as long as the title and to their ids: a hint has no bound, and reading every
        // waiting proposal whole inside this write held hundreds of megabytes to supersede nothing. A match ignoring case
        // is always the same length, so the narrowing loses none.
        var matching = revisedTitle is null ? [] : (await _connection.QueryAsync<IdAndHint>(
            new CommandDefinition(
                """
                SELECT id AS Id, target_title AS Hint
                  FROM roadmap_proposals
                 WHERE state = 'waiting' AND id <> @Id AND target_title IS NOT NULL
                   AND length(target_title) = length(@Title);
                """,
                new { Id = id, Title = revisedTitle },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false))
            .Where(row => string.Equals(row.Hint, revisedTitle, StringComparison.OrdinalIgnoreCase))
            .Select(row => row.Id)
            .ToList();

        // Read one at a time as each is decided, not with every id in one statement: an agent can make more proposals
        // than SQLite takes parameters, and the revision would then fail for good. One row at a time also holds one
        // proposal, whatever each one's size.
        foreach (var match in matching)
        {
            var rival = await _connection.QuerySingleAsync<ProposalRow>(
                new CommandDefinition(
                    """
                    SELECT id AS Id, proposer AS Proposer, source_conversation AS SourceConversation,
                           target_title AS TargetTitle, title AS Title, size AS Size, state AS State,
                           created_utc AS CreatedUtc, decided_utc AS DecidedUtc
                      FROM roadmap_proposals
                     WHERE id = @Id;
                    """,
                    new { Id = match },
                    scope.Transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

            var other = await ToProposalAsync(rival, scope.Transaction, cancellationToken).ConfigureAwait(false);

            await DecideAsync(other, ProposalState.Superseded, now, scope.Transaction, cancellationToken)
                .ConfigureAwait(false);
        }

        scope.Complete();

        return new RoadmapChange(true, null, roadmapId, revision, origin);
    }

    /// <summary>Declines a waiting proposal.</summary>
    /// <param name="id">The proposal.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What it came to.</returns>
    public async Task<RoadmapChange> DeclineAsync(
        long id,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = WriteScope.Begin(_connection, transaction);

        var proposal = await GetProposalCoreAsync(id, scope.Transaction, cancellationToken).ConfigureAwait(false);

        if (proposal is null)
        {
            return Refused("There is no such proposal.");
        }

        if (proposal.State != ProposalState.Waiting)
        {
            return Refused($"That proposal is already {ProposalStateText(proposal.State)}.");
        }

        await DecideAsync(proposal, ProposalState.Declined, ExchangeRow.FormatTimestamp(nowUtc), scope.Transaction,
            cancellationToken).ConfigureAwait(false);

        scope.Complete();

        return new RoadmapChange(true, null);
    }

    /// <summary>Changes a confirmed roadmap: rewords, reorders, adds and removes items.</summary>
    /// <param name="id">The roadmap.</param>
    /// <param name="expectedRevision">The revision the caller read.</param>
    /// <param name="final">The final content.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="transaction">A transaction to write within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What it came to.</returns>
    /// <remarks>
    /// Refused when the revision is not current, so two surfaces editing at once cannot overwrite one another. A
    /// removed item's conversation is kept, because items are never deleted (SPEC.md §5.7).
    /// </remarks>
    public async Task<RoadmapChange> EditAsync(
        long id,
        int expectedRevision,
        RoadmapContent final,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(final);

        using var scope = WriteScope.Begin(_connection, transaction);

        var current = await _connection.QuerySingleOrDefaultAsync<NumberAndRevision>(
            new CommandDefinition(
                "SELECT number AS Number, revision AS Revision FROM roadmaps WHERE id = @Id;",
                new { Id = id },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (current is null)
        {
            return Refused("There is no such roadmap.");
        }

        if (current.Revision != expectedRevision)
        {
            return Refused(
                $"The roadmap has changed since you read it: it is at revision {current.Revision} and " +
                $"you edited revision {expectedRevision}. Read it again and make the change on that.");
        }

        var problem = await ContentProblemAsync(final, scope.Transaction, cancellationToken).ConfigureAwait(false);

        if (problem is not null)
        {
            return Refused(problem);
        }

        var written = await WriteContentAsync(
            id, (int)current.Number, (int)current.Revision, final, RoadmapOrigin.Edited,
            ExchangeRow.FormatTimestamp(nowUtc), scope.Transaction, cancellationToken).ConfigureAwait(false);

        if (written is not null)
        {
            return Refused(written);
        }

        scope.Complete();

        return new RoadmapChange(true, null, id, (int)current.Revision + 1, RoadmapOrigin.Edited);
    }

    /// <summary>Lists the project's roadmaps.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One summary per roadmap, in the order they were made.</returns>
    public async Task<IReadOnlyList<RoadmapSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        [.. (await _connection.QueryAsync<SummaryRow>(
            new CommandDefinition(
                """
                SELECT r.id AS Id, r.number AS Number, r.title AS Title, r.revision AS Revision,
                       (SELECT COUNT(*) FROM roadmap_items i
                         WHERE i.roadmap_id = r.id AND i.removed_rev IS NULL) AS LiveItems,
                       (SELECT COUNT(*) FROM roadmap_items i
                          JOIN roadmap_item_conversations c ON c.item_id = i.id
                          JOIN conversations v ON v.id = c.conversation_id
                         WHERE i.roadmap_id = r.id AND i.removed_rev IS NULL) AS StartedItems
                  FROM roadmaps r
                 ORDER BY r.number;
                """,
                cancellationToken: cancellationToken)).ConfigureAwait(false))
            .Select(r => new RoadmapSummary(
                r.Id, (int)r.Number, r.Title, (int)r.Revision, (int)r.LiveItems, (int)r.StartedItems))];

    /// <summary>Reads one roadmap whole, with the facts the store holds about each live item.</summary>
    /// <param name="id">The roadmap.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The roadmap, or <see langword="null"/>.</returns>
    public async Task<Roadmap?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        var row = await _connection.QuerySingleOrDefaultAsync<RoadmapRow>(
            new CommandDefinition(
                """
                SELECT id AS Id, number AS Number, title AS Title, origin AS Origin, proposer AS Proposer,
                       revision AS Revision
                  FROM roadmaps WHERE id = @Id;
                """,
                new { Id = id },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        var items = (await _connection.QueryAsync<ItemRow>(
            new CommandDefinition(
                """
                SELECT i.id AS Id, i.number AS Number, i.position AS Position, i.text AS Text,
                       i.intended_agent AS IntendedAgent, i.removed_rev AS RemovedRev,
                       c.conversation_id AS Conversation, i.edited_utc AS EditedUtc
                  FROM roadmap_items i
                  LEFT JOIN roadmap_item_conversations c ON c.item_id = i.id
                 WHERE i.roadmap_id = @Id
                 ORDER BY i.removed_rev IS NOT NULL, i.position, i.number;
                """,
                new { Id = id },
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var needs = (await _connection.QueryAsync<NeedRow>(
            new CommandDefinition(
                """
                SELECT n.item_id AS Item, m.number AS Needs
                  FROM roadmap_item_needs n
                  JOIN roadmap_items m ON m.id = n.needs_item_id
                  JOIN roadmap_items i ON i.id = n.item_id
                 WHERE i.roadmap_id = @Id
                 ORDER BY m.number;
                """,
                new { Id = id },
                cancellationToken: cancellationToken)).ConfigureAwait(false))
            .ToLookup(n => n.Item, n => (int)n.Needs);

        var result = new List<RoadmapItem>(items.Count);

        foreach (var item in items)
        {
            var name = item.Conversation ?? ItemConversation.Name((int)row.Number, (int)item.Number);
            var facts = item.RemovedRev is null
                ? await FactsAsync(name, item.EditedUtc, cancellationToken).ConfigureAwait(false)
                : null;

            result.Add(new RoadmapItem(
                item.Id,
                (int)item.Number,
                (int)item.Position,
                item.Text,
                item.IntendedAgent is null ? null : AgentName.Create(item.IntendedAgent),
                [.. needs[item.Id]],
                name,
                item.RemovedRev is null ? null : (int)item.RemovedRev,
                facts));
        }

        return new Roadmap(
            row.Id,
            (int)row.Number,
            row.Title,
            string.Equals(row.Origin, "proposed", StringComparison.Ordinal) ? RoadmapOrigin.Proposed : RoadmapOrigin.Edited,
            row.Proposer,
            (int)row.Revision,
            result);
    }

    /// <summary>Says which roadmap and item a conversation belongs to, by the reservation.</summary>
    /// <param name="conversationId">A conversation identifier.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The roadmap's identifier, number and title, the item's number, and whether the item has since been removed,
    /// or <see langword="null"/> when the conversation is not reserved by any item.
    /// </returns>
    /// <remarks>
    /// The reservation recognises an item's conversation and nothing in a payload or a name does (SPEC.md §5.7).
    /// </remarks>
    public async Task<(long RoadmapId, int RoadmapNumber, string Title, int ItemNumber, bool Removed)?> ReservationOfAsync(
        string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);

        var row = await _connection.QuerySingleOrDefaultAsync<ReservationRow>(
            new CommandDefinition(
                """
                SELECT r.id AS RoadmapId, r.number AS RoadmapNumber, r.title AS Title, i.number AS ItemNumber,
                       i.removed_rev IS NOT NULL AS Removed
                  FROM roadmap_item_conversations c
                  JOIN roadmap_items i ON i.id = c.item_id
                  JOIN roadmaps r ON r.id = i.roadmap_id
                 WHERE c.conversation_id = @Id;
                """,
                new { Id = conversationId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row is null
            ? null
            : (row.RoadmapId, (int)row.RoadmapNumber, row.Title, (int)row.ItemNumber, row.Removed != 0);
    }

    /// <summary>Collects what became of an agent's proposals, marking each delivered.</summary>
    /// <param name="agent">The agent draining its inbox.</param>
    /// <param name="limit">The most to return.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="transaction">The transaction the drain runs in, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The notices, oldest first.</returns>
    /// <remarks>Never wakes anybody: a notice is collected when the agent next looks, as a file's change notice is.</remarks>
    public async Task<IReadOnlyList<RoadmapNotice>> DrainNoticesAsync(
        AgentName agent,
        int limit,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        using var scope = WriteScope.Begin(_connection, transaction);

        var rows = (await _connection.QueryAsync<NoticeRow>(
            new CommandDefinition(
                """
                SELECT id AS Id, proposal_id AS ProposalId, title AS Title, outcome AS Outcome,
                       created_utc AS CreatedUtc
                  FROM roadmap_notices
                 WHERE agent = @Agent AND delivered_utc IS NULL
                 ORDER BY id
                 LIMIT @Limit;
                """,
                new { Agent = agent.Value, Limit = limit },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        foreach (var row in rows)
        {
            await _connection.ExecuteAsync(
                new CommandDefinition(
                    "UPDATE roadmap_notices SET delivered_utc = @When WHERE id = @Id;",
                    new { Id = row.Id, When = ExchangeRow.FormatTimestamp(nowUtc) },
                    scope.Transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        scope.Complete();

        return
        [
            .. rows.Select(r => new RoadmapNotice(
                r.ProposalId,
                r.Title,
                ParseState(r.Outcome),
                ExchangeRow.ParseTimestamp(r.CreatedUtc))),
        ];
    }

    /// <summary>Counts the notices waiting for an agent, marking nothing.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>How many are undelivered.</returns>
    public async Task<int> CountNoticesAsync(AgentName agent, CancellationToken cancellationToken = default) =>
        await _connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM roadmap_notices WHERE agent = @Agent AND delivered_utc IS NULL;",
                new { Agent = agent.Value },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

    // ---- writing a roadmap's content

    /// <summary>
    /// Writes a roadmap's items to be exactly <paramref name="content"/>, carrying existing ones across by number.
    /// </summary>
    /// <returns>A sentence saying why not, or <see langword="null"/> when it was written.</returns>
    /// <remarks>
    /// The one path for a new roadmap (old revision zero), an edit and a confirmed revision, so the three cannot
    /// disagree about what a reorder or a removal does. Positions are first moved far out of the way because the
    /// unique index over live positions would otherwise see two items in one place for an instant, and the
    /// dependencies are rewritten last because the trigger that holds them compares final positions.
    /// </remarks>
    private async Task<string?> WriteContentAsync(
        long roadmapId,
        int roadmapNumber,
        int oldRevision,
        RoadmapContent content,
        RoadmapOrigin origin,
        string now,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var newRevision = oldRevision + 1;

        var live = (await _connection.QueryAsync<LiveItem>(
            new CommandDefinition(
                """
                SELECT id AS Id, number AS Number, text AS Text
                  FROM roadmap_items WHERE roadmap_id = @Roadmap AND removed_rev IS NULL;
                """,
                new { Roadmap = roadmapId },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToDictionary(i => (int)i.Number);

        foreach (var item in content.Items)
        {
            if (item.Number is { } number && !live.ContainsKey(number))
            {
                return $"Item number {number} is not a live item of this roadmap.";
            }
        }

        var kept = content.Items.Where(i => i.Number is not null).Select(i => i.Number!.Value).ToHashSet();

        // Removed rather than deleted: its conversation stays and says what it was (SPEC.md §5.7).
        foreach (var gone in live.Values.Where(i => !kept.Contains((int)i.Number)))
        {
            await _connection.ExecuteAsync(
                new CommandDefinition(
                    "UPDATE roadmap_items SET removed_rev = @Revision WHERE id = @Id;",
                    new { Revision = newRevision, gone.Id },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                DELETE FROM roadmap_item_needs
                 WHERE item_id IN (SELECT id FROM roadmap_items WHERE roadmap_id = @Roadmap);
                UPDATE roadmap_items SET position = position + @Shift
                 WHERE roadmap_id = @Roadmap AND removed_rev IS NULL;
                """,
                new { Roadmap = roadmapId, Shift },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var next = await _connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COALESCE(MAX(number), 0) FROM roadmap_items WHERE roadmap_id = @Roadmap;",
                new { Roadmap = roadmapId },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var ids = new long[content.Items.Count];

        for (var at = 0; at < content.Items.Count; at++)
        {
            var item = content.Items[at];

            if (item.Number is { } number)
            {
                var existing = live[number];
                ids[at] = existing.Id;

                await _connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE roadmap_items
                           SET text = @Text, intended_agent = @Agent, position = @Position,
                               edited_utc = CASE WHEN text <> @Text THEN @Now ELSE edited_utc END
                         WHERE id = @Id;
                        """,
                        new { item.Text, Agent = item.IntendedAgent?.Value, Position = at + 1, Now = now, existing.Id },
                        transaction,
                        cancellationToken: cancellationToken)).ConfigureAwait(false);

                continue;
            }

            next++;

            ids[at] = await _connection.ExecuteScalarAsync<long>(
                new CommandDefinition(
                    """
                    INSERT INTO roadmap_items (roadmap_id, number, position, text, intended_agent, added_rev)
                    VALUES (@Roadmap, @Number, @Position, @Text, @Agent, @Revision);
                    SELECT last_insert_rowid();
                    """,
                    new
                    {
                        Roadmap = roadmapId,
                        Number = next,
                        Position = at + 1,
                        item.Text,
                        Agent = item.IntendedAgent?.Value,
                        Revision = newRevision,
                    },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

            await _connection.ExecuteAsync(
                new CommandDefinition(
                    "INSERT INTO roadmap_item_conversations (item_id, conversation_id) VALUES (@Id, @Name);",
                    new
                    {
                        Id = ids[at],
                        Name = await FreeItemNameAsync(ItemConversation.Name(roadmapNumber, next), transaction, cancellationToken)
                            .ConfigureAwait(false),
                    },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        for (var at = 0; at < content.Items.Count; at++)
        {
            foreach (var needed in content.Items[at].Needs)
            {
                await _connection.ExecuteAsync(
                    new CommandDefinition(
                        "INSERT INTO roadmap_item_needs (item_id, needs_item_id) VALUES (@Item, @Needs);",
                        new { Item = ids[at], Needs = ids[needed - 1] },
                        transaction,
                        cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }

        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE roadmaps
                   SET title = @Title, revision = @Revision, updated_utc = @Now, origin = @Origin
                 WHERE id = @Id;
                """,
                new { content.Title, Revision = newRevision, Now = now, Origin = OriginText(origin), Id = roadmapId },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return null;
    }

    private async Task<string?> ContentProblemAsync(
        RoadmapContent content, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        if (RoadmapRules.Problem(content) is { } problem)
        {
            return problem;
        }

        foreach (var agent in content.Items.Select(i => i.IntendedAgent).Where(a => a is not null).Distinct())
        {
            var known = await _connection.ExecuteScalarAsync<int>(
                new CommandDefinition(
                    "SELECT COUNT(*) FROM agents WHERE name = @Name AND status <> 'retired';",
                    new { Name = agent!.Value.Value },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (known == 0)
            {
                return $"{agent.Value} is not an agent in this project, so an item cannot be meant for it.";
            }
        }

        return null;
    }

    private static bool SameWords(RoadmapContent proposed, RoadmapContent final)
    {
        if (!string.Equals(proposed.Title, final.Title, StringComparison.Ordinal)
            || proposed.Items.Count != final.Items.Count)
        {
            return false;
        }

        for (var at = 0; at < final.Items.Count; at++)
        {
            var a = proposed.Items[at];
            var b = final.Items[at];

            if (!string.Equals(a.Text, b.Text, StringComparison.Ordinal)
                || a.IntendedAgent != b.IntendedAgent
                || !a.Needs.Order().SequenceEqual(b.Needs.Order()))
            {
                return false;
            }
        }

        return true;
    }

    private async Task DecideAsync(
        RoadmapProposal proposal,
        ProposalState state,
        string now,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await _connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE roadmap_proposals SET state = @State, decided_utc = @Now WHERE id = @Id;",
                new { State = ProposalStateText(state), Now = now, proposal.Id },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        // The proposer is told, in its own title. A purged proposer has no row for the notice to reference, and
        // its proposals went with it, so this is reached only for an agent that exists.
        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO roadmap_notices (agent, proposal_id, title, outcome, created_utc)
                VALUES (@Agent, @Id, substr(@Title, 1, @Length), @Outcome, @Now);
                """,
                new
                {
                    Agent = proposal.Proposer.Value,
                    proposal.Id,
                    proposal.Content.Title,

                    // Cut as a heading is, by characters: the title has no bound, and fifty whole ones drained at once
                    // exceeded what a reply may carry, so the collection failed after marking everything in it delivered.
                    Length = RoadmapProposal.HeadingLength,
                    Outcome = ProposalStateText(state),
                    Now = now,
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    // ---- reading

    private async Task<RoadmapProposal?> GetProposalCoreAsync(
        long id, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        var row = await _connection.QuerySingleOrDefaultAsync<ProposalRow>(
            new CommandDefinition(
                """
                SELECT id AS Id, proposer AS Proposer, source_conversation AS SourceConversation,
                       target_title AS TargetTitle, title AS Title, size AS Size, state AS State,
                       created_utc AS CreatedUtc, decided_utc AS DecidedUtc
                  FROM roadmap_proposals WHERE id = @Id;
                """,
                new { Id = id },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row is null ? null : await ToProposalAsync(row, transaction, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RoadmapProposal> ToProposalAsync(
        ProposalRow row, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        var items = (await _connection.QueryAsync<ProposalItemRow>(
            new CommandDefinition(
                """
                SELECT position AS Position, text AS Text, intended_agent AS IntendedAgent
                  FROM roadmap_proposal_items WHERE proposal_id = @Id ORDER BY position;
                """,
                new { row.Id },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var needs = (await _connection.QueryAsync<PositionNeed>(
            new CommandDefinition(
                """
                SELECT position AS Position, needs_position AS Needs
                  FROM roadmap_proposal_needs WHERE proposal_id = @Id ORDER BY needs_position;
                """,
                new { row.Id },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false))
            .ToLookup(n => (int)n.Position, n => (int)n.Needs);

        var drafts = items
            .Select(i => new RoadmapItemDraft(
                null,
                i.Text,
                i.IntendedAgent is null ? null : AgentName.Create(i.IntendedAgent),
                [.. needs[(int)i.Position]]))
            .ToList();

        return new RoadmapProposal(
            row.Id,
            AgentName.Create(row.Proposer),
            row.SourceConversation,
            row.TargetTitle,
            new RoadmapContent(row.Title, drafts),
            row.Size,
            ParseState(row.State),
            ExchangeRow.ParseTimestamp(row.CreatedUtc),
            row.DecidedUtc is null ? null : ExchangeRow.ParseTimestamp(row.DecidedUtc));
    }

    /// <summary>The name an item's conversation is reserved under: the derived one, or the first free one after it.</summary>
    /// <param name="derived">The name derived from the roadmap and item numbers.</param>
    /// <param name="transaction">The transaction the reservation is written in.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A name no conversation, destroyed conversation or reservation holds.</returns>
    /// <remarks>
    /// <b>A name already in use is never reserved.</b> The derived name is predictable, so an agent can open a conversation
    /// under it before the item exists; reserving that name would make the agent's conversation the item's, already begun
    /// with the agent's words in it (SPEC.md §5.7). The item takes the next free name instead, and is recognised by its
    /// reservation as every item is.
    /// </remarks>
    private async Task<string> FreeItemNameAsync(string derived, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        for (var suffix = 1; ; suffix++)
        {
            var name = suffix == 1 ? derived : string.Create(CultureInfo.InvariantCulture, $"{derived}-{suffix}");

            var taken = await _connection.ExecuteScalarAsync<long>(
                new CommandDefinition(
                    """
                    SELECT (SELECT COUNT(*) FROM conversations WHERE id = @Name)
                         + (SELECT COUNT(*) FROM destroyed_conversations WHERE id = @Name)
                         + (SELECT COUNT(*) FROM roadmap_item_conversations WHERE conversation_id = @Name);
                    """,
                    new { Name = name },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (taken == 0)
            {
                return name;
            }
        }
    }

    /// <summary>What the store holds about one reserved conversation, as facts.</summary>
    /// <remarks>
    /// Whether the item was reworded since it began is read from the item's own edit time against the first request's,
    /// never by comparing words: the person may change the item's words before sending them.
    /// </remarks>
    private async Task<ItemFacts> FactsAsync(string conversation, string? editedUtc, CancellationToken cancellationToken)
    {
        var row = await _connection.QuerySingleAsync<FactsRow>(
            new CommandDefinition(
                """
                SELECT (SELECT COUNT(*) FROM conversations WHERE id = @C)                              AS Exists_,
                       (SELECT COUNT(*) FROM destroyed_conversations WHERE id = @C)                     AS Destroyed,
                       (SELECT COUNT(*) FROM exchanges WHERE conversation_id = @C AND state = 'OPEN')   AS OpenExchanges,
                       (SELECT COUNT(*) FROM exchanges WHERE conversation_id = @C AND state = 'OPEN'
                           AND delivered_utc IS NULL)                                                   AS Uncollected,
                       (SELECT COUNT(*) FROM exchanges WHERE conversation_id = @C
                           AND outcome IN ('complete', 'settled'))                                      AS Complete,
                       -- One when the latest closed exchange is the blocked one, otherwise zero.
                       (SELECT COUNT(*) FROM exchanges e WHERE e.conversation_id = @C
                           AND e.outcome = 'blocked'
                           AND e.id = (SELECT MAX(l.id) FROM exchanges l
                                        WHERE l.conversation_id = @C AND l.state = 'CLOSED'))           AS Blocked,
                       (SELECT COUNT(*) FROM exchanges WHERE conversation_id = @C
                           AND outcome = 'closed')                                                      AS Closed,
                       (SELECT ri.blocker FROM result_items ri JOIN messages m ON m.id = ri.message_id
                         WHERE m.conversation_id = @C AND ri.blocker IS NOT NULL
                         ORDER BY m.id DESC LIMIT 1)                                                    AS Blocker,
                       (SELECT closed_by FROM conversations WHERE id = @C)                              AS ClosedBy,
                       -- One when the item's text was edited after a request had been sent into its conversation.
                       (SELECT COUNT(*) FROM messages WHERE conversation_id = @C AND type = 'request'
                           AND @Edited IS NOT NULL AND julianday(created_utc) < julianday(@Edited)
                         LIMIT 1)                                                                       AS EditedSince;
                """,
                new { C = conversation, Edited = editedUtc },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var open = (await _connection.QueryAsync<long>(
            new CommandDefinition(
                "SELECT id FROM exchanges WHERE conversation_id = @C AND state = 'OPEN' ORDER BY id;",
                new { C = conversation },
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        return new ItemFacts(
            row.Exists_ > 0,
            (int)row.OpenExchanges,
            row.OpenExchanges > 0 && row.Uncollected == 0,
            (int)row.Complete,
            (int)row.Blocked,
            (int)row.Closed,
            row.Blocker,
            row.ClosedBy,
            row.Destroyed > 0,
            row.EditedSince > 0,
            open);
    }

    // ---- small helpers

    private static RoadmapChange Refused(string problem) => new(false, problem);

    private static string OriginText(RoadmapOrigin origin) =>
        origin == RoadmapOrigin.Proposed ? "proposed" : "edited";

    private static string ProposalStateText(ProposalState state) => state switch
    {
        ProposalState.Waiting => "waiting",
        ProposalState.Confirmed => "confirmed",
        ProposalState.Declined => "declined",
        ProposalState.Superseded => "superseded",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static ProposalState ParseState(string text) => text switch
    {
        "waiting" => ProposalState.Waiting,
        "confirmed" => ProposalState.Confirmed,
        "declined" => ProposalState.Declined,
        "superseded" => ProposalState.Superseded,
        _ => throw new InvalidDataException(
            string.Create(CultureInfo.InvariantCulture, $"The store holds a proposal state this build does not know: {text}.")),
    };

    private sealed record ProposalRow(
        long Id, string Proposer, string? SourceConversation, string? TargetTitle, string Title, long Size,
        string State, string CreatedUtc, string? DecidedUtc);

    // A class with setters, as FactsRow is, because computed columns carry no declared type for a record to match.
    private sealed class ProposalHeadingRow
    {
        public long Id { get; set; }

        public string Proposer { get; set; } = string.Empty;

        public string? SourceConversation { get; set; }

        public string? TargetTitle { get; set; }

        public string Title { get; set; } = string.Empty;

        public long Size { get; set; }

        public string State { get; set; } = string.Empty;

        public string CreatedUtc { get; set; } = string.Empty;

        public string? DecidedUtc { get; set; }

        public long Items { get; set; }
    }

    // SQLite hands every integer back as 64 bits, and Dapper matches a constructor by exact type, so every
    // integer in a row type is a long and is narrowed where it is used.
    private sealed record ProposalItemRow(long Position, string Text, string? IntendedAgent);

    private sealed record RoadmapRow(long Id, long Number, string Title, string Origin, string? Proposer, long Revision);

    private sealed record ItemRow(
        long Id, long Number, long Position, string Text, string? IntendedAgent, long? RemovedRev, string? Conversation,
        string? EditedUtc);

    private sealed record NumberAndRevision(long Number, long Revision);

    private sealed record IdAndHint(long Id, string Hint);

    private static int Digits(int number) => number.ToString(CultureInfo.InvariantCulture).Length;

    private sealed record LiveItem(long Id, long Number, string Text);

    private sealed record NeedRow(long Item, long Needs);

    private sealed record PositionNeed(long Position, long Needs);

    // A class with setters, because two of its columns are counting subselects: over an empty table SQLite reports no
    // type for them and Dapper has nothing to match a constructor against.
    private sealed class SummaryRow
    {
        public long Id { get; set; }

        public long Number { get; set; }

        public string Title { get; set; } = string.Empty;

        public long Revision { get; set; }

        public long LiveItems { get; set; }

        public long StartedItems { get; set; }
    }

    private sealed record ReservationRow(long RoadmapId, long RoadmapNumber, string Title, long ItemNumber, long Removed);

    private sealed record NoticeRow(long Id, long ProposalId, string Title, string Outcome, string CreatedUtc);

    // A class with setters rather than a record: the subselects here return NULL for a conversation that has
    // none of something, and a NULL column carries no type for Dapper to match a constructor against.
    private sealed class FactsRow
    {
        public long Exists_ { get; set; }

        public long Destroyed { get; set; }

        public long OpenExchanges { get; set; }

        public long Uncollected { get; set; }

        public long Complete { get; set; }

        public long Blocked { get; set; }

        public long Closed { get; set; }

        public string? Blocker { get; set; }

        public string? ClosedBy { get; set; }

        public long EditedSince { get; set; }
    }
}
