using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// A registered participant: its identity, how to reach it, and whether it is currently
/// attached.
/// </summary>
/// <param name="Name">The agent's stable logical name.</param>
/// <param name="Adapter">
/// The adapter that attaches this agent, identifying which integration the broker should use
/// to reach it.
/// </param>
/// <param name="Tier">The adapter's capability tier.</param>
/// <param name="Workspace">
/// The directory the agent operates in, or <see langword="null"/> for agents with no
/// filesystem scope such as a model-API responder.
/// </param>
/// <param name="SessionRef">
/// An adapter-specific handle for resuming a previous session, or <see langword="null"/> if
/// none exists yet. Mutable: reassigned whenever the agent is spawned or resumed.
/// </param>
/// <param name="AdapterConfig">
/// Whatever the named adapter needs to invoke this agent — a model identifier, an endpoint, a
/// timeout, a notification channel — as a JSON document, or <see langword="null"/> if it needs
/// nothing. Opaque to the broker by design: it is stored and handed back at invocation, and
/// only the adapter interprets it. Credentials do not belong here; see
/// <see cref="AgentRepository.SetCredentialAsync"/>.
/// </param>
/// <param name="LastSeenUtc">
/// When the agent last communicated, or <see langword="null"/> if it never has. Used to
/// bound the change summary injected on resume, so a returning agent is not reasoning about
/// code that moved while it was away.
/// </param>
/// <param name="Status">The agent's current registration and liveness state.</param>
/// <param name="Owns">
/// One line saying what this agent owns, or <see langword="null"/> if its launch did not say.
/// Unlike the rest of this record it is written for peers to read: see
/// <see cref="OwnershipStatement"/>, and <see cref="AgentRepository.RefreshOwnershipAsync"/>
/// for why it is refreshed on every launch while the configuration around it is not.
/// </param>
/// <remarks>
/// Credentials are deliberately not a field here, and should not become one. This record is
/// what <see cref="AgentRepository.UpsertAsync"/> writes from a manifest reload, and anything
/// carried on it is overwritten by that reload. Credentials are registered once and must
/// survive it — folding them in would mean a reload silently dropped every agent's
/// references and broke invocation on the next start. They are read and written separately,
/// through <see cref="AgentRepository.GetCredentialsAsync"/> and
/// <see cref="AgentRepository.SetCredentialAsync"/>, so that the two lifecycles cannot be
/// confused by accident.
/// </remarks>
public sealed record AgentRecord(
    AgentName Name,
    string Adapter,
    AgentTier Tier,
    string? Workspace,
    string? SessionRef,
    string? AdapterConfig,
    DateTimeOffset? LastSeenUtc,
    AgentStatus Status,
    string? Owns = null);

/// <summary>
/// One credential an adapter needs, as a reference rather than a value.
/// </summary>
/// <param name="Name">
/// The adapter's name for it — <c>api_key</c>, <c>org_token</c> — matching what its
/// descriptor declared. Not the secret's name in whatever store holds it.
/// </param>
/// <param name="Reference">
/// Where the secret lives: <c>env:</c>, <c>file:</c>, or <c>keychain:</c>. The scheme is part
/// of the stored value, so resolution follows what was recorded rather than probing whatever
/// the machine happens to offer today.
/// </param>
/// <remarks>
/// The grammar accepts <c>keychain:</c>, but nothing resolves it yet — see SPEC.md §12.2.
/// It is stored so a deployment needing it is not blocked behind a schema change; a broker
/// asked to resolve one should fail rather than guess. Most agents need no credential at all,
/// because the CLI behind them authenticates out of band and keeps its own token.
/// </remarks>
public sealed record AgentCredential(string Name, string Reference);

/// <summary>
/// Reads and writes the agent registry.
/// </summary>
/// <remarks>
/// Instances are cheap and hold no state beyond the supplied connection. They are not
/// thread-safe, because a <see cref="SqliteConnection"/> is not.
/// </remarks>
/// <param name="connection">
/// An open connection to the Parley database, already migrated by
/// <see cref="SchemaMigrator"/>.
/// </param>
public sealed class AgentRepository(SqliteConnection connection)
{
    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>
    /// Loads every registered agent, ordered by name.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>All agents in the registry, whatever their status.</returns>
    /// <remarks>
    /// <b>Never reachable from an agent.</b> This returns the stored record whole — workspace
    /// path, session handle, and the adapter configuration §12.1 makes free-form so it can carry
    /// endpoints — and <see cref="ListRoutableAsync"/> exists precisely so none of that crosses
    /// to a peer. The two sit next to each other, differ by one word at the call site, and only
    /// one of them is safe to answer <c>agents()</c> with; <c>SurfaceBoundaryTests</c> fails if a
    /// broker operation ever returns this type, so the distinction is checked rather than
    /// remembered.
    /// <para>
    /// It has no production caller yet and is kept deliberately: it is the read side of the
    /// registry's full record, and a manifest loader needs to see current configuration in order
    /// to diff against it. Without it those columns would be write-only.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<AgentRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<AgentRow>(
            new CommandDefinition(
                """
                SELECT name           AS Name,
                       adapter        AS Adapter,
                       tier           AS Tier,
                       workspace      AS Workspace,
                       session_ref    AS SessionRef,
                       adapter_config AS AdapterConfig,
                       owns           AS Owns,
                       last_seen_utc  AS LastSeenUtc,
                       status         AS Status
                FROM agents
                ORDER BY name;
                """,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    /// <summary>
    /// Records that an agent has detached cleanly.
    /// </summary>
    /// <param name="name">The agent that is stopping.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>One if the agent was marked offline, zero if it is retired.</returns>
    /// <remarks>
    /// <para>
    /// The counterpart to <see cref="MarkLiveAsync"/>, and the only thing that produces
    /// <see cref="AgentStatus.Offline"/> now that the attach sequence of SPEC.md §14 is scoped
    /// to one agent. The broker-wide "mark everything offline" it replaced could not survive
    /// that scoping: no process speaks for every agent any more, so no process is entitled to
    /// say they have all stopped.
    /// </para>
    /// <para>
    /// Best-effort by construction. An adapter calls it on a clean shutdown; a crashed process
    /// leaves a stale <c>live</c> until that agent next attaches. That is affordable because
    /// the mark is cosmetic — <c>offline</c> does not gate routing, deliberately, since it is
    /// the ordinary resting state of a lazily spawned agent between questions and refusing
    /// traffic to one would refuse the very traffic meant to wake it.
    /// </para>
    /// <para>
    /// Retired agents are left alone, the same as on the way in. A person withdrew them; a
    /// process stopping is not a statement about that.
    /// </para>
    /// </remarks>
    public async Task<int> MarkOfflineAsync(
        AgentName name,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE agents SET status = 'offline' " +
                "WHERE name = @Name AND status <> 'retired';",
                new { Name = name.Value },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

    /// <summary>
    /// Reports whether an agent is registered at all.
    /// </summary>
    /// <param name="agent">The name to look for.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns><see langword="true"/> if the registry holds a row for it, in any status.</returns>
    /// <remarks>
    /// Distinct from <see cref="IsUnroutableAsync"/>, which folds an unknown name in with the
    /// statuses that refuse traffic. This one answers the different question the broker asks
    /// about the agent that is <em>acting</em>: an adapter configured with a name nobody
    /// registered is a deployment fault, not a peer being unavailable.
    /// </remarks>
    public async Task<bool> IsRegisteredAsync(
        AgentName agent,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                "SELECT count(*) FROM agents WHERE name = @Name;",
                new { Name = agent.Value },
                cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;

    /// <summary>
    /// Lists what one agent may learn about the others, per SPEC.md §10.1's <c>agents()</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Every registered agent except those an operator has retired.</returns>
    /// <remarks>
    /// <para>
    /// Projects rather than filters, and the columns it does not select are the reason it exists
    /// beside <see cref="GetAllAsync"/>. Workspace, session handle and adapter configuration are
    /// all on the stored record and none may cross to a peer: the first is a path on somebody
    /// else's machine, the second an internal identifier, and §12.1 makes the third free-form
    /// precisely so it can hold endpoints the broker never interprets. Handing those to an agent
    /// would breach rule 3 through the one operation whose job is to describe agents.
    /// </para>
    /// <para>
    /// Retired agents are omitted, per §14 — "not routed to, not offered in <c>agents()</c>".
    /// Unreachable ones are listed, also per §14, because a peer that can see the mark stops
    /// asking instead of inferring it from silence.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<AgentSummary>> ListRoutableAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<SummaryRow>(
            new CommandDefinition(
                """
                SELECT name AS Name, adapter AS Adapter, tier AS Tier, status AS Status,
                       last_seen_utc AS LastSeenUtc, owns AS Owns
                  FROM agents
                 WHERE status <> 'retired'
                 ORDER BY name;
                """,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(r => new AgentSummary(
            AgentName.Create(r.Name),
            r.Adapter,
            AgentRow.ParseTier(r.Tier),
            AgentRow.ParseStatus(r.Status),
            r.LastSeenUtc is null ? null : ExchangeRow.ParseTimestamp(r.LastSeenUtc),
            r.Owns)).ToList();
    }

    /// <summary>
    /// Reports whether an agent carries a status that stops the broker routing to it.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <param name="nowUtc">The current time, against which silence is measured.</param>
    /// <param name="transaction">A transaction to read within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns><see langword="true"/> if it is retired, or silent past the limit.</returns>
    /// <remarks>
    /// <para>
    /// The pair of statuses admission already refuses a question to (§14). Stated once here so a
    /// second caller cannot get the set subtly wrong — an omitted <c>retired</c> is what made
    /// retirement a control that did not hold.
    /// </para>
    /// <para>
    /// A name that is not registered at all counts too, and that was found rather than designed.
    /// Without it a message addressed to an unknown agent reached the <c>INSERT</c> and came back
    /// as a foreign-key violation — a raw storage exception for an ordinary mistake like a typo
    /// or a name remembered from before a re-registration, where §7.3 asks for a refusal the
    /// asker can act on. It also made the two distinguishable: a retired name refused and an
    /// unknown one thrown, which hands back exactly the state §14 keeps out of <c>agents()</c>.
    /// Both now mean the same thing, which is what they are — a name you cannot address, and
    /// <c>agents()</c> is the answer to both.
    /// </para>
    /// </remarks>
    public async Task<bool> IsUnroutableAsync(
        AgentName agent,
        DateTimeOffset nowUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                // Retirement alone, and silence deliberately not. This changed with the move to
                // derived liveness and the reasoning is worth keeping, because the obvious wiring
                // is wrong in a way that disables the protocol.
                //
                // The old 'unreachable' meant "spawning was tried and failed repeatedly", which
                // is evidence that starting this agent does not work and therefore a fair reason
                // to refuse a question. The derived one means only that nobody has the repository
                // open at the moment -- which is the ordinary resting state of a lazily spawned
                // agent, and the state most agents are in most of the time. Refusing traffic to
                // it would refuse the very traffic meant to wake it, exactly as the schema has
                // always said of Offline, and a supervisor would then never see a debt to act on
                // because no debt could ever be created.
                //
                // So silence is reported and never routed on. Retirement is a judgement that the
                // agent will not come back, and only it refuses.
                "SELECT count(*) FROM agents WHERE name = @Name AND status <> 'retired';",
                new { Name = agent.Value },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false) == 0;

    /// <summary>
    /// Reports whether an operator has retired an agent.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <param name="transaction">A transaction to read within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns><see langword="true"/> if it is <c>retired</c>.</returns>
    /// <remarks>
    /// Narrower than <see cref="IsUnroutableAsync"/> and used where the question is about the
    /// agent that is <em>acting</em> rather than the one being addressed. <c>unreachable</c>
    /// describes a peer nobody could spawn, which says nothing about an agent plainly running
    /// well enough to be sending something.
    /// </remarks>
    public async Task<bool> IsRetiredAsync(
        AgentName agent,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                "SELECT count(*) FROM agents WHERE name = @Name AND status = 'retired';",
                new { Name = agent.Value },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;

    /// <summary>
    /// Says whether an agent has been heard from since a given instant.
    /// </summary>
    /// <param name="agent">The agent to test.</param>
    /// <param name="sinceUtc">The oldest contact that still counts.</param>
    /// <param name="transaction">The transaction to read within, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="true"/> if a session is believed to be attached right now.</returns>
    /// <remarks>
    /// <para>
    /// Read by the attach sequence, which releases a debt on the grounds that its worker
    /// vanished. This is the only evidence the registry holds that one has <em>not</em>, and it
    /// is a lease rather than a flag for two reasons a flag cannot cover.
    /// </para>
    /// <para>
    /// A flag cannot be cleared by a process that stopped existing, so a crashed session leaves
    /// <c>live</c> set for ever — the registry lies, and every later attach declines to release
    /// debts whose worker is demonstrably gone. And a flag cannot count: an agent may have two
    /// sessions open, and the second one closing marks the agent offline while the first is
    /// still mid-answer, so the next attach releases work somebody is doing. A lease expires on
    /// its own and is renewed by whichever sessions are actually running, which answers both.
    /// </para>
    /// <para>
    /// Status is deliberately not consulted. <c>offline</c> written by one session's clean
    /// shutdown says nothing about another session's, and it is precisely that disagreement the
    /// timestamp resolves — <c>last_seen_utc</c> is renewed by every heartbeat, so the surviving
    /// session keeps it current whatever its departing sibling wrote. Retirement is checked
    /// where retirement matters, in <see cref="MarkLiveAsync"/> and at admission.
    /// </para>
    /// </remarks>
    public async Task<bool> WasSeenSinceAsync(
        AgentName agent,
        DateTimeOffset sinceUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                """
                SELECT count(*) FROM agents
                 WHERE name = @Name
                   AND last_seen_utc IS NOT NULL
                   AND last_seen_utc >= @Since;
                """,
                new { Name = agent.Value, Since = ExchangeRow.FormatTimestamp(sinceUtc) },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;

    private sealed class SummaryRow
    {
        public string Name { get; init; } = "";
        public string Adapter { get; init; } = "";
        public string Tier { get; init; } = "";
        public string Status { get; init; } = "";
        public string? LastSeenUtc { get; init; }
        public string? Owns { get; init; }
    }

    /// <summary>
    /// Registers an agent, or updates its configuration if already registered.
    /// </summary>
    /// <param name="agent">The agent to register.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The number of rows affected.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="agent"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// On conflict this updates configuration only — adapter, tier, workspace, and credential
    /// configuration. Session handle, liveness, and last-seen timestamp are deliberately left
    /// alone, so reloading a manifest cannot sever a live agent's session or reset the window
    /// used to compute its change summary. Use <see cref="MarkLiveAsync"/> for those.
    /// </remarks>
    public async Task<int> UpsertAsync(AgentRecord agent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO agents (name, adapter, tier, workspace, session_ref, adapter_config,
                                    owns, last_seen_utc, status)
                VALUES (@Name, @Adapter, @Tier, @Workspace, @SessionRef, @AdapterConfig,
                        @Owns, @LastSeenUtc, @Status)
                ON CONFLICT(name) DO UPDATE SET
                    adapter        = excluded.adapter,
                    tier           = excluded.tier,
                    workspace      = excluded.workspace,
                    adapter_config = excluded.adapter_config,
                    owns           = excluded.owns;
                """,
                new
                {
                    Name = agent.Name.Value,
                    agent.Adapter,
                    Tier = FormatTier(agent.Tier),
                    agent.Workspace,
                    agent.SessionRef,
                    agent.AdapterConfig,
                    agent.Owns,
                    LastSeenUtc = agent.LastSeenUtc is { } t ? ExchangeRow.FormatTimestamp(t) : null,
                    Status = FormatStatus(agent.Status)
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }


    /// <summary>
    /// Registers an agent only if the registry does not already hold one by that name.
    /// </summary>
    /// <param name="agent">The agent to register.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The number of rows inserted: one if it was new, zero if it already existed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="agent"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// The self-registration path of SPEC.md §12.3, and the distinction from
    /// <see cref="UpsertAsync"/> is the whole reason it exists. An adapter registers itself on
    /// every launch, so an upsert there would overwrite adapter, tier, workspace and
    /// configuration each time somebody opened that repository — and for an agent an operator
    /// had registered from a manifest, that silently destroys the entry.
    /// </para>
    /// <para>
    /// Concretely: <c>billing</c> registered as Headless with a model and an endpoint is what
    /// lets the broker invoke it when nobody has its session open. One person opening billing's
    /// editor once would rewrite it as a Pull agent with no configuration, and the broker would
    /// no longer know how to reach it at all. Nothing would report this; the next question to
    /// billing would simply never be delivered while its session was closed.
    /// </para>
    /// <para>
    /// So the rule §12.3 states is enforced here rather than trusted: an operator's registration
    /// wins, and an adapter's own is a fallback for an agent nobody has described. Reconciling a
    /// tier that disagrees with how the agent actually attached is the operator's decision, and
    /// they are better placed to make it than a process that happened to start.
    /// </para>
    /// <para>
    /// <c>DO NOTHING</c> rather than a read followed by a write, so two sessions launching at
    /// once cannot both find the registry empty and race to fill it.
    /// </para>
    /// </remarks>
    public async Task<int> RegisterIfAbsentAsync(
        AgentRecord agent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO agents (name, adapter, tier, workspace, session_ref, adapter_config,
                                    owns, last_seen_utc, status)
                VALUES (@Name, @Adapter, @Tier, @Workspace, @SessionRef, @AdapterConfig,
                        @Owns, @LastSeenUtc, @Status)
                ON CONFLICT(name) DO NOTHING;
                """,
                new
                {
                    Name = agent.Name.Value,
                    agent.Adapter,
                    Tier = FormatTier(agent.Tier),
                    agent.Workspace,
                    agent.SessionRef,
                    agent.AdapterConfig,
                    agent.Owns,
                    LastSeenUtc = agent.LastSeenUtc is { } t ? ExchangeRow.FormatTimestamp(t) : null,
                    Status = FormatStatus(agent.Status)
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Records what a launching agent's configuration says it owns, replacing what was there.
    /// </summary>
    /// <param name="name">The agent whose server is starting.</param>
    /// <param name="owns">What its launch says it owns, or <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>One if the registration was updated, zero if the agent is unknown or retired.</returns>
    /// <remarks>
    /// <para>
    /// <see cref="RegisterIfAbsentAsync"/> writes nothing when the name is already known, and
    /// that is right for what it protects: a second launch must not revive a retirement by the
    /// side door, nor rewrite an agent an operator described in a manifest as Headless, with the
    /// endpoint the broker needs to invoke it, into a configuration-free Pull agent because
    /// somebody opened that repository once.
    /// </para>
    /// <para>
    /// It is wrong for this one column. Left to registration alone, a launch could state what an
    /// agent owns exactly once — on its very first start ever — and never correct it. A
    /// statement that cannot be corrected is worse than no statement, because every peer routes
    /// by it and nothing says it is stale.
    /// </para>
    /// <para>
    /// So this column belongs to the launch: refreshed on every start, cleared when the launch
    /// stops saying. Clearing matters as much as setting — an operator who deletes the variable
    /// because the statement was wrong has withdrawn it, and a coalescing update would keep the
    /// wrong one for ever with no way to remove it.
    /// </para>
    /// <para>
    /// It touches nothing else. No status, so it cannot resurrect a retirement, and retired rows
    /// are skipped outright; no tier and no adapter configuration, so it cannot flatten a
    /// manifest. <b>The workspace is deliberately not refreshed here</b>, and that asymmetry is
    /// real rather than an oversight: a manifest names where the broker should look for an agent
    /// it may have to invoke, and a launch that says nothing about a workspace must not erase
    /// that. The consequence is that a moved repository keeps its old recorded path until an
    /// operator corrects it, which is a known and separate problem.
    /// </para>
    /// </remarks>
    public async Task<int> RefreshOwnershipAsync(
        AgentName name,
        string? owns,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE agents
                   SET owns = @Owns
                 WHERE name = @Name
                   AND status <> 'retired';
                """,
                new { Name = name.Value, Owns = owns },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

    /// <summary>
    /// Records whether this agent may be started when it owes an answer and nobody is running it.
    /// </summary>
    /// <param name="name">The agent.</param>
    /// <param name="invokeCommand">
    /// How to start this agent, or <see langword="null"/> if it has not consented.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Rows updated.</returns>
    /// <remarks>
    /// <para>
    /// Refreshed on every launch and from the launch environment only, exactly as
    /// <see cref="RefreshOwnershipAsync"/> is and for the same reason: it is a permission the
    /// person who configured the repository grants, not a property an agent decides about itself.
    /// No operation accepts it, so an agent cannot volunteer to be started any more than it can
    /// choose its own name.
    /// </para>
    /// <para>
    /// The consent and the command are one argument because they are one fact. A supervisor
    /// reads this row to start an agent it does not host, so consent without a command is a
    /// candidate that fails on every pass, and a command without consent is a permission nobody
    /// granted. Storage refuses the first of those outright; passing them together is what makes
    /// the second unrepresentable.
    /// </para>
    /// <para>
    /// Cleared when a launch stops supplying it, which is what makes withdrawal work: removing
    /// the variable from a launch configuration and opening the repository once is enough, and
    /// nobody has to remember a second step in a place they will not look.
    /// </para>
    /// <para>
    /// Skips a retired agent, as ownership does. Retirement is a person saying the agent will not
    /// come back; a launch quietly re-granting permission to start it would overrule that.
    /// </para>
    /// </remarks>
    public async Task<int> RefreshInvocationConsentAsync(
        AgentName name,
        string? invokeCommand,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE agents
                   SET invoke_when_idle = @Invoke,
                       invoke_command   = @Command
                 WHERE name = @Name
                   AND status <> 'retired';
                """,
                new
                {
                    Name = name.Value,
                    Invoke = invokeCommand is null ? 0 : 1,
                    Command = invokeCommand
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

    /// <summary>
    /// Marks an agent live and records that it has just communicated.
    /// </summary>
    /// <param name="name">The agent to mark.</param>
    /// <param name="sessionRef">
    /// A new session handle to store, or <see langword="null"/> to keep the existing one.
    /// </param>
    /// <param name="nowUtc">The current instant, in UTC.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The number of rows affected: one on success, zero if the agent is unknown.</returns>
    /// <remarks>
    /// Passing <see langword="null"/> for <paramref name="sessionRef"/> preserves the stored
    /// handle rather than clearing it, so a routine liveness update cannot destroy the value
    /// that makes session resumption possible.
    /// </remarks>
    /// <remarks>
    /// A retired agent is not revived, and the guard is in the <c>WHERE</c> clause rather than
    /// in a caller. SPEC.md §14: "Retirement is cleared only by an explicit operator action,
    /// never by registration" — and an agent attaching is the closest thing to registration
    /// there is, since it is the agent announcing itself. Without this, an operator who retired
    /// an agent would find it live again the moment somebody opened that repository's session,
    /// which is precisely the control-that-does-not-hold §14 describes.
    /// <para>
    /// The zero this returns is meaningful rather than a silent no-op: it says the registry
    /// refused, which is how <c>StartResult.AgentMarkedLive</c> tells an adapter that its agent
    /// has been withdrawn.
    /// </para>
    /// </remarks>
    public async Task<int> MarkLiveAsync(
        AgentName name,
        string? sessionRef,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE agents
                   SET status        = 'live',
                       session_ref   = COALESCE(@SessionRef, session_ref),
                       last_seen_utc = @Now
                 WHERE name = @Name AND status <> 'retired';
                """,
                new
                {
                    Name = name.Value,
                    SessionRef = sessionRef,
                    Now = ExchangeRow.FormatTimestamp(nowUtc)
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);


    /// <summary>
    /// Retires an agent whose repository is gone for good.
    /// </summary>
    /// <param name="name">The agent to retire.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The number of rows changed: one if the agent existed, zero otherwise.</returns>
    /// <remarks>
    /// <para>
    /// The operator operation from SPEC.md section 10.2, and the only status the broker never
    /// sets on its own evidence. <see cref="AgentStatus.Unreachable"/> is the broker reporting
    /// that it tried and failed, which a re-clone or a restored backup can invalidate at any
    /// moment; this is a person saying it will not come back.
    /// </para>
    /// <para>
    /// It marks rather than deletes, and section 10.2 previously said "remove". The schema does
    /// not permit removal and should not: <c>exchanges</c> and <c>messages</c> both reference
    /// <c>agents(name)</c>, and section 9.4 keeps exchange rows after pruning their message text
    /// precisely so that telemetry outlives the traffic. Deleting the agent would cascade to
    /// that skeleton and take every counterparty history of dealing with it along too, so the
    /// pair-level questions section 15 is built on would be answerable for every agent except
    /// the ones that had gone wrong.
    /// </para>
    /// <para>
    /// Nothing here touches the outstanding exchanges. They take the ordinary path: their
    /// windows elapse, they orphan, and the grace period ghosts them. Retiring neither waits
    /// for that nor accelerates it.
    /// </para>
    /// </remarks>
    public async Task<int> RetireAsync(
        AgentName name,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteAsync(
            new CommandDefinition(
                // Guarded on the current status, like every other transition here, so the row
                // count is a truthful answer to "did this change anything". Without it, retiring
                // an already-retired agent writes the same value and reports a change — which is
                // what an operator's exit code is built on.
                "UPDATE agents SET status = 'retired' " +
                "WHERE name = @Name AND status <> 'retired';",
                new { Name = name.Value },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

    /// <summary>
    /// Withdraws a retirement, returning the agent to service.
    /// </summary>
    /// <param name="name">The agent to reinstate.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The number of rows changed: one if the agent was retired, zero otherwise.</returns>
    /// <remarks>
    /// The counterpart to <see cref="RetireAsync"/>, and deliberately not something
    /// <see cref="UpsertAsync"/> does. The registry is reloaded from the manifest at every
    /// start-up, so clearing retirement on registration would undo it on the next restart and
    /// every restart after — an operator who retires an agent and finds it live again the
    /// following morning has been given a control that does not hold.
    ///
    /// It returns the agent to <see cref="AgentStatus.Offline"/> rather than
    /// <see cref="AgentStatus.Live"/>, because offline is the ordinary resting state of a
    /// lazily spawned agent and nothing has yet demonstrated that this one can be reached.
    /// </remarks>
    public async Task<int> ReinstateAsync(
        AgentName name,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE agents SET status = 'offline' " +
                "WHERE name = @Name AND status = 'retired';",
                new { Name = name.Value },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

    /// <summary>
    /// Deletes a registration that never traded, freeing its name.
    /// </summary>
    /// <param name="name">The agent to purge.</param>
    /// <param name="transaction">A transaction to join, or <see langword="null"/> to own one.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>
    /// <see cref="PurgeOutcome.Gone"/>, <see cref="PurgeOutcome.NotRegistered"/>, or a held
    /// outcome naming every reference that stopped it.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The operator operation from SPEC.md section 10.2, and the narrow complement to
    /// <see cref="RetireAsync"/> rather than a replacement for it. Retirement pays a real price
    /// — the name is never registrable again and the row outlives the repository — and section
    /// 14 is right that the price buys something: the record of what the agent said, and every
    /// counterparty history of dealing with it. An agent that never traded has no such record,
    /// so for that agent alone the price buys nothing. This deletes exactly those, and nothing
    /// else: a mistyped name, an experiment, a manifest reload that registered a repository
    /// somebody was only looking at.
    /// </para>
    /// <para>
    /// What counts as "never traded" is read from the schema rather than listed here, and that
    /// is the load-bearing decision in this method. A hand-written list of tables is correct
    /// only until the next migration adds a reference to <c>agents(name)</c> and nobody thinks
    /// to update it — at which point a purge would delete rows the new table needed, or trip a
    /// foreign key and surface as an unhandled <see cref="SqliteException"/> where an operator
    /// expected an explanation. Asking the database which tables point at it cannot drift.
    /// </para>
    /// <para>
    /// Cascading references are deliberately excluded from that scan. <c>agent_credentials</c>
    /// and <c>size_refusals</c> are declared <c>ON DELETE CASCADE</c>, which is the schema
    /// already saying they belong to the agent rather than to a counterparty, and they go with
    /// it. A credential reference for an agent that no longer exists is not history anybody can
    /// use.
    /// </para>
    /// <para>
    /// The check and the delete share one <c>IMMEDIATE</c> transaction, so an exchange opened
    /// between them cannot be orphaned by a purge that had already decided there was none. The
    /// foreign keys stay as the backstop underneath that: if this scan were ever wrong, the
    /// delete fails rather than silently taking the traffic with it.
    /// </para>
    /// </remarks>
    public async Task<PurgeOutcome> PurgeAsync(
        AgentName name,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = WriteScope.Begin(_connection, transaction);

        var registered = await _connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                "SELECT count(*) FROM agents WHERE name = @Name;",
                new { Name = name.Value },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;

        if (!registered)
        {
            scope.Complete();
            return PurgeOutcome.NotRegistered;
        }

        var holds = new List<PurgeHold>();

        foreach (var reference in await ReferencesToAgentsAsync(scope.Transaction, cancellationToken)
            .ConfigureAwait(false))
        {
            var rows = await _connection.ExecuteScalarAsync<long>(
                new CommandDefinition(
                    // Interpolated because a table name cannot be a parameter. Both halves come
                    // from this connection's own schema and never from a caller, so there is
                    // nothing here for a name to inject into -- and an AgentName could not carry
                    // it anyway, being validated on construction.
                    $"""SELECT count(*) FROM "{reference.TableName}" WHERE "{reference.ColumnName}" = @Name;""",
                    new { Name = name.Value },
                    scope.Transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (rows > 0)
            {
                holds.Add(new PurgeHold(reference.TableName, reference.ColumnName, (int)rows));
            }
        }

        if (holds.Count > 0)
        {
            scope.Complete();
            return PurgeOutcome.HeldBy(holds);
        }

        await _connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM agents WHERE name = @Name;",
                new { Name = name.Value },
                scope.Transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        scope.Complete();
        return PurgeOutcome.Gone;
    }

    /// <summary>
    /// Asks the schema which columns reference <c>agents(name)</c> without cascading.
    /// </summary>
    /// <param name="transaction">The transaction the scan runs in.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Every non-cascading reference, ordered by table then column.</returns>
    private async Task<IReadOnlyList<AgentReference>> ReferencesToAgentsAsync(
        SqliteTransaction transaction,
        CancellationToken cancellationToken) =>
        (await _connection.QueryAsync<AgentReference>(
            new CommandDefinition(
                """
                SELECT m.name AS TableName, f."from" AS ColumnName
                  FROM sqlite_master AS m
                  JOIN pragma_foreign_key_list(m.name) AS f
                 WHERE m.type = 'table'
                   AND f."table" = 'agents'
                   AND f."on_delete" <> 'CASCADE'
                 ORDER BY m.name, f."from";
                """,
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

    /// <summary>
    /// A column somewhere in the schema that names an agent.
    /// </summary>
    /// <param name="TableName">The referencing table.</param>
    /// <param name="ColumnName">The referencing column.</param>
    private sealed record AgentReference(string TableName, string ColumnName);

    /// <summary>
    /// Records where one of an agent's secrets lives, replacing any reference of that name.
    /// </summary>
    /// <param name="agent">The agent the credential belongs to.</param>
    /// <param name="credential">The credential's name and reference.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The number of rows affected.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="credential"/> is <see langword="null"/>.</exception>
    /// <exception cref="SqliteException">
    /// The reference is not one of the recognised schemes, or the agent is not registered.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Takes a reference, never a secret, and there is deliberately no overload that takes
    /// one. The broker resolves references at invocation and neither persists nor logs what
    /// it resolved; a method here accepting a value would make SPEC.md §12's claim — that a
    /// manifest is safe to commit — depend on nobody calling it.
    /// </para>
    /// <para>
    /// Which store a reference points at is decided once, when the agent is registered, and
    /// recorded in the scheme. It is not re-decided at invocation: a store appearing on the
    /// machine later does not silently take over, and one disappearing produces a resolution
    /// failure rather than a quiet fallback to a stale copy somewhere else.
    /// </para>
    /// </remarks>
    public async Task<int> SetCredentialAsync(
        AgentName agent,
        AgentCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        return await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO agent_credentials (agent, name, reference)
                VALUES (@Agent, @Name, @Reference)
                ON CONFLICT(agent, name) DO UPDATE SET reference = excluded.reference;
                """,
                new { Agent = agent.Value, credential.Name, credential.Reference },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads every credential reference registered for an agent, ordered by name.
    /// </summary>
    /// <param name="agent">The agent to load.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The references, which the caller resolves. Empty if the agent needs none.</returns>
    /// <remarks>
    /// Returns references, so this is safe to log and safe to show. Resolving them is the
    /// broker's job at invocation, and what comes back from that resolution is not.
    /// </remarks>
    public async Task<IReadOnlyList<AgentCredential>> GetCredentialsAsync(
        AgentName agent,
        CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<AgentCredential>(
            new CommandDefinition(
                """
                SELECT name AS Name, reference AS Reference
                FROM agent_credentials
                WHERE agent = @Agent
                ORDER BY name;
                """,
                new { Agent = agent.Value },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows];
    }

    /// <summary>
    /// Removes one of an agent's credential references.
    /// </summary>
    /// <param name="agent">The agent the credential belongs to.</param>
    /// <param name="name">The adapter's name for the credential.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// <see langword="true"/> if a reference was removed; <see langword="false"/> if there
    /// was none by that name.
    /// </returns>
    /// <remarks>
    /// Removes the pointer, not the secret. Whatever store holds the value is untouched,
    /// because the broker did not put it there and deleting from someone's keychain on the
    /// strength of a registry edit is not a decision this layer gets to make.
    /// </remarks>
    public async Task<bool> RemoveCredentialAsync(
        AgentName agent,
        string name,
        CancellationToken cancellationToken = default)
    {
        var affected = await _connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM agent_credentials WHERE agent = @Agent AND name = @Name;",
                new { Agent = agent.Value, Name = name },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected == 1;
    }

    private static string FormatTier(AgentTier tier) => tier switch
    {
        AgentTier.Full => "full",
        AgentTier.Pull => "pull",
        AgentTier.Headless => "headless",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, null)
    };

    private static string FormatStatus(AgentStatus status) => status switch
    {
        AgentStatus.Offline => "offline",
        AgentStatus.Live => "live",
        AgentStatus.Retired => "retired",

        // NotResponding and Unreachable are readings of last_seen_utc rather than values, so
        // there is nothing to write for them and a caller trying is a caller that has confused
        // a derivation for a fact. See AgentLiveness.
        AgentStatus.NotResponding or AgentStatus.Unreachable =>
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Liveness is derived from last_seen_utc and cannot be stored."),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    /// <summary>Flat projection of a row in the <c>agents</c> table.</summary>
    private sealed class AgentRow
    {
        public string Name { get; init; } = "";

        public string Adapter { get; init; } = "";

        public string Tier { get; init; } = "";

        public string? Workspace { get; init; }

        public string? SessionRef { get; init; }

        public string? AdapterConfig { get; init; }

        public string? Owns { get; init; }

        public string? LastSeenUtc { get; init; }

        public string Status { get; init; } = "";

        /// <summary>Parses a stored tier token.</summary>
        /// <param name="value">The token read from storage.</param>
        /// <returns>The corresponding tier.</returns>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="value"/> is not a recognised token, indicating a corrupted or
        /// externally modified database.
        /// </exception>
        /// <remarks>
        /// Named rather than inlined into <see cref="ToDomain"/>, because a second reader now
        /// needs it — the summary projection §10.1 returns — and two copies of a mapping are how
        /// the two come to disagree.
        /// </remarks>
        public static AgentTier ParseTier(string value) => value switch
        {
            "full" => AgentTier.Full,
            "pull" => AgentTier.Pull,
            "headless" => AgentTier.Headless,
            _ => throw new InvalidOperationException($"Unrecognised tier '{value}'.")
        };

        /// <summary>Parses a stored status token.</summary>
        /// <param name="value">The token read from storage.</param>
        /// <returns>The corresponding status.</returns>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="value"/> is not a recognised token, indicating a corrupted or
        /// externally modified database.
        /// </exception>
        public static AgentStatus ParseStatus(string value) => value switch
        {
            "offline" => AgentStatus.Offline,
            "live" => AgentStatus.Live,
            "retired" => AgentStatus.Retired,

            // Accepted for the length of one upgrade. Migration 008 rewrites both to 'offline',
            // so a row carrying either is one written by a build older than this reader against
            // a database newer than it -- which happens while a fleet is being upgraded one
            // repository at a time, and should not crash the reader that notices.
            "registered" or "unreachable" => AgentStatus.Offline,
            _ => throw new InvalidOperationException($"Unrecognised status '{value}'.")
        };

        public AgentRecord ToDomain() => new(
            AgentName.Create(Name),
            Adapter,
            ParseTier(Tier),
            Workspace,
            SessionRef,
            AdapterConfig,
            LastSeenUtc is null ? null : ExchangeRow.ParseTimestamp(LastSeenUtc),
            ParseStatus(Status),
            Owns);
    }
}
