using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;


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
                SELECT a.name           AS Name,
                       a.adapter        AS Adapter,
                       a.tier           AS Tier,
                       a.workspace      AS Workspace,
                       a.session_ref    AS SessionRef,
                       a.adapter_config AS AdapterConfig,
                       a.owns           AS Owns,
                       a.connected      AS Connected,
                       COALESCE(n.connected, 0) AS NodeConnected,
                       a.status         AS Status
                FROM agents AS a
                LEFT JOIN nodes AS n ON n.machine = a.machine
                ORDER BY a.name;
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
    /// The counterpart to <see cref="MarkLiveAsync"/>, and the only thing that clears
    /// <c>connected</c> now that the attach sequence of SPEC.md §14 is scoped
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
                // max() rather than a bare decrement: a detach with no matching attach is a
                // bug somewhere else, and a negative count would turn it into an agent that can
                // never read as connected again however many sessions open.
                "UPDATE agents SET connected = max(0, connected - 1) " +
                "WHERE name = @Name AND status <> 'retired';",
                new { Name = name.Value },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

    /// <summary>
    /// Every agent registered under a role, for a question addressed to one (SPEC.md §5.5).
    /// </summary>
    /// <param name="role">The role, without a machine.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The members, ordered by name.</returns>
    /// <remarks>
    /// <para>
    /// Matched on the <c>role</c> column rather than on a prefix of the name, which is the whole
    /// reason that column is stored at all: <c>infra</c> is a prefix of <c>infrastructure</c>,
    /// so a <c>LIKE 'infra%'</c> would quietly ask a question of agents that answer to something
    /// else entirely.
    /// </para>
    /// <para>
    /// <b>Membership is every registered agent, not every reachable one.</b> A machine that is
    /// switched off still has its agents here, so a role question opens an exchange with them
    /// too — which orphans and is ghosted, and tells the asker. The alternative silently omits
    /// the one agent whose answer mattered and looks exactly like success.
    /// </para>
    /// <para>
    /// Retired agents are excluded, because retirement is the one judgement that gates routing
    /// and an exchange opened with a retired agent could never be answered by anybody.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<AgentName>> ListRoleAsync(
        string role,
        CancellationToken cancellationToken = default)
    {
        var names = await _connection.QueryAsync<string>(
            new CommandDefinition(
                "SELECT name FROM agents WHERE role = @Role AND status <> 'retired' " +
                "ORDER BY name;",
                new { Role = role },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. names.Select(AgentName.Create)];
    }

    /// <summary>
    /// The workspace recorded for an agent, if it is registered and one was recorded.
    /// </summary>
    /// <param name="name">The agent.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Its workspace, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Read at launch so a second repository claiming a name already held can be refused rather
    /// than quietly sharing an identity with the first (SPEC.md §4.1.1). The workspace is the
    /// only thing that distinguishes them: two checkouts on one machine agree about the role,
    /// the machine, and therefore the whole name, and differ only in where they are.
    /// </remarks>
    public async Task<string?> WorkspaceOfAsync(
        AgentName name,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                "SELECT workspace FROM agents WHERE name = @Name;",
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
    /// Everything else is listed whatever its presence, because presence does not gate routing:
    /// a question to an agent nobody is running is what causes it to be started, and one to an
    /// agent on a switched-off machine orphans and is ghosted, which tells the asker more than
    /// a refusal at the door would.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<AgentSummary>> ListRoutableAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _connection.QueryAsync<SummaryRow>(
            new CommandDefinition(
                """
                SELECT a.name AS Name, a.adapter AS Adapter, a.tier AS Tier,
                       a.connected AS AgentConnected, COALESCE(n.connected, 0) AS NodeConnected,
                       a.owns AS Owns
                  FROM agents AS a
                  LEFT JOIN nodes AS n ON n.machine = a.machine
                 WHERE a.status <> 'retired'
                 ORDER BY a.name;
                """,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(r => new AgentSummary(
            AgentName.Create(r.Name),
            r.Adapter,
            AgentRow.ParseTier(r.Tier),
            AgentLiveness.Read(r.NodeConnected != 0, r.AgentConnected != 0),
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
    /// This was a lease on a timestamp, and the reasoning for it was sound while the registry
    /// only knew what an agent had last written: a flag cannot be cleared by a process that
    /// stopped existing, so a crashed session would leave <c>live</c> set for ever and every
    /// later attach would decline to release debts whose worker was demonstrably gone.
    /// </para>
    /// <para>
    /// A connection has no such failure. It closes when the process ends, however impolitely,
    /// and the broker is the one that notices — so <c>connected</c> is cleared by the party that
    /// survives rather than by the party that died. See SPEC.md §4.0.
    /// </para>
    /// <para>
    /// <b>One thing the lease covered and this does not, yet.</b> A flag cannot count, and an
    /// agent may have two sessions open: the second closing clears <c>connected</c> while the
    /// first is still mid-answer, and the next attach then releases work somebody is doing. The
    /// lease answered that by being renewed by whichever sessions were actually running. The
    /// answer here is for the broker to hold the connections and clear the column when the last
    /// one goes — which it can, because it holds them — and until the transport lands, the
    /// window is between one session detaching and another attaching.
    /// </para>
    /// <para>
    /// Retirement is deliberately not consulted here. It is checked where retirement matters, in
    /// <see cref="MarkLiveAsync"/> and at admission.
    /// </para>
    /// </remarks>
    public async Task<bool> WasSeenSinceAsync(
        AgentName agent,
        DateTimeOffset sinceUtc,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                "SELECT count(*) FROM agents WHERE name = @Name AND connected = 1;",
                new { Name = agent.Value },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;

    private sealed class SummaryRow
    {
        public string Name { get; init; } = "";
        public string Adapter { get; init; } = "";
        public string Tier { get; init; } = "";
        public long AgentConnected { get; init; }
        public long NodeConnected { get; init; }
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
                INSERT INTO nodes (machine, connected, first_seen_utc)
                VALUES (@Machine, 0, @Now)
                ON CONFLICT(machine) DO NOTHING;

                INSERT INTO agents (name, adapter, tier, workspace, session_ref,
                                    adapter_config, owns, connected, status)
                VALUES (@Name, @Adapter, @Tier, @Workspace, @SessionRef,
                        @AdapterConfig, @Owns, @Connected, @Status)
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
                    agent.Machine,
                    agent.Adapter,
                    Tier = FormatTier(agent.Tier),
                    agent.Workspace,
                    agent.SessionRef,
                    agent.AdapterConfig,
                    agent.Owns,
                    Connected = agent.Connected ? 1 : 0,
                    Now = ExchangeRow.FormatTimestamp(DateTimeOffset.UtcNow),
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
                INSERT INTO nodes (machine, connected, first_seen_utc)
                VALUES (@Machine, 0, @Now)
                ON CONFLICT(machine) DO NOTHING;

                INSERT INTO agents (name, adapter, tier, workspace, session_ref,
                                    adapter_config, owns, connected, status)
                VALUES (@Name, @Adapter, @Tier, @Workspace, @SessionRef,
                        @AdapterConfig, @Owns, @Connected, @Status)
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
                    agent.Machine,
                    Connected = agent.Connected ? 1 : 0,
                    Now = ExchangeRow.FormatTimestamp(DateTimeOffset.UtcNow),
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
    /// Marks every agent disconnected, for a broker that has just started.
    /// </summary>
    /// <param name="nowUtc">The instant.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>How many rows were changed.</returns>
    /// <remarks>
    /// <para>
    /// A connection is evidence (SPEC.md §4.0), and a broker that has just started holds none.
    /// Every non-zero <c>connected</c> is therefore a session belonging to a process that is no
    /// longer there, and believing it has a specific consequence rather than a cosmetic one: an
    /// agent that looks attached is one whose abandoned work the attach sequence of §14 declines
    /// to release, on the grounds that somebody else is holding it. Left uncleared, a broker
    /// restart would strand every exchange that was in flight when it went down.
    /// </para>
    /// <para>
    /// Run at startup rather than at shutdown, because the shutdown a broker most needs this
    /// after is the one where it did not get to run anything.
    /// </para>
    /// <para>
    /// It does not touch <c>status</c>. Retirement is a person's judgement and a broker
    /// restarting is not evidence about it.
    /// </para>
    /// </remarks>
    public async Task<int> DisconnectAllAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE agents
                   SET connected = 0, changed_utc = @Now
                 WHERE connected <> 0;
                """,
                new { Now = ExchangeRow.FormatTimestamp(nowUtc) },
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
        CancellationToken cancellationToken = default)
    {
        // This used to mark the agent's node connected as well, on the grounds that an adapter
        // talking to us is evidence its machine is reachable. That was a stopgap for there being
        // no node process, and it said so; the node process exists now and owns the column, so
        // the stopgap is gone rather than left to fight it.
        //
        // It was also wrong in a way worth recording. `nodes.connected` answers "is there
        // something on that machine that could start an agent", and an attached adapter is not
        // that -- it is one agent, already started, which cannot launch its neighbours. A
        // deployment with no supervisor anywhere read as fully startable, and the first question
        // to an idle agent would sit unanswered until it was ghosted, with the registry having
        // said all along that it could be started.
        return await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE agents
                   SET connected   = connected + 1,
                       session_ref = COALESCE(@SessionRef, session_ref),
                       changed_utc = @Now
                 WHERE name = @Name AND status <> 'retired';
                """,
                new
                {
                    Name = name.Value,
                    SessionRef = sessionRef,
                    Now = ExchangeRow.FormatTimestamp(nowUtc)
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }


    /// <summary>
    /// Retires an agent whose repository is gone for good.
    /// </summary>
    /// <param name="name">The agent to retire.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The number of rows changed: one if the agent existed, zero otherwise.</returns>
    /// <remarks>
    /// <para>
    /// The operator operation from SPEC.md section 10.2, and the one thing about an agent the
    /// broker never decides on its own evidence. Everything else it knows about reachability is
    /// an observation that a re-clone, a restored backup or somebody switching a machine on can
    /// invalidate at any moment; this is a person saying it will not come back.
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
    /// It returns the agent to <see cref="AgentStatus.Active"/> and leaves <c>connected</c>
    /// alone, because reinstatement is a statement about the registration and not a claim that
    /// anybody has started the agent. Whether it is reachable is observed, and observing it is
    /// not this method's job.
    /// </remarks>
    public async Task<int> ReinstateAsync(
        AgentName name,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE agents SET status = 'active' " +
                "WHERE name = @Name AND status = 'retired';",
                new { Name = name.Value },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

    /// <summary>
    /// Deletes an agent and everything it was party to.
    /// </summary>
    /// <param name="name">The agent to purge.</param>
    /// <param name="transaction">A transaction to join, or <see langword="null"/> to own one.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>What was destroyed, or <see cref="PurgeOutcome.NotRegistered"/>.</returns>
    /// <remarks>
    /// <para>
    /// The operator operation from SPEC.md section 10.2, and the destructive one. It removes the
    /// agent, every message it sent or received, every exchange it was party to, and every
    /// conversation it took part in — including the halves of those conversations belonging to
    /// peers who did nothing wrong. It cannot be undone and there is no confirmation here; the
    /// surface that calls it is where a person is asked.
    /// </para>
    /// <para>
    /// <b>The collateral is the feature, not a defect being tolerated.</b> A conversation is a
    /// record of what two agents said to each other, and one with a party deleted out of it is
    /// not a smaller truth — it is a misleading one, an exchange whose asker no longer exists
    /// and whose answers now appear unprompted. If the record is worth keeping, the agent should
    /// be retired instead; retirement exists precisely to keep it. Purge is for when the record
    /// is the thing you want gone.
    /// </para>
    /// <para>
    /// This is a reversal. It used to refuse the moment anything referenced the agent, and the
    /// reasoning was sound as far as it went: deleting an agent takes its counterparties' history
    /// with it. What that reasoning missed is that refusing left no command which did the job, so
    /// an operator who genuinely wanted an agent gone had retirement — which frees nothing and
    /// keeps everything — or nothing at all. A guard whose only effect is that the operator does
    /// the deletion by hand, with a SQL client and no ordering, is not protecting anybody.
    /// </para>
    /// <para>
    /// Everything doomed is identified before anything is deleted, because each delete changes
    /// the answer to the next question: remove the participant rows and the conversations are no
    /// longer discoverable, remove the exchanges and their messages look unrelated. The order of
    /// deletion then follows the foreign keys, deepest first, with the agent row last —
    /// <c>agent_credentials</c> and <c>size_refusals</c> cascade and are not named. The keys stay
    /// the backstop underneath: if this order were ever wrong, the delete fails inside the
    /// transaction rather than half-completing.
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

        // Foreign keys deferred to commit time rather than enforced per statement. Two reasons,
        // and the second is the load-bearing one.
        //
        // It frees the deletes below from having to run in dependency order, which would
        // otherwise have to be hand-maintained against a schema that changes.
        //
        // And it keeps the keys as the backstop while allowing a generic sweep. If anything at
        // all is missed -- a table added by a later migration, a reference nobody here
        // anticipated -- the COMMIT fails and the whole purge rolls back, rather than a partial
        // deletion succeeding and leaving rows pointing at an agent that no longer exists. The
        // pragma is scoped to this transaction and resets itself when it ends.
        await ExecuteAsync("PRAGMA defer_foreign_keys = ON;").ConfigureAwait(false);

        // Everything doomed is worked out before anything is deleted, because each delete
        // changes the answer to the next question. Remove the participant rows first and the
        // conversations this agent was in are no longer discoverable; remove the exchanges and
        // the messages hanging off them look unrelated. Held in temporary tables so every
        // statement below reads the same set.
        await ExecuteAsync(
            """
            CREATE TEMP TABLE doomed_conversations AS
            SELECT conversation_id AS id FROM conversation_participants WHERE agent = @Name
            UNION
            SELECT conversation_id FROM exchanges
             WHERE (asker = @Name OR answerer = @Name OR owed_by = @Name)
               AND conversation_id IS NOT NULL
            UNION
            SELECT conversation_id FROM messages
             WHERE (from_agent = @Name OR to_agent = @Name)
               AND conversation_id IS NOT NULL;
            """).ConfigureAwait(false);

        await ExecuteAsync(
            """
            CREATE TEMP TABLE doomed_exchanges AS
            SELECT id FROM exchanges
             WHERE asker = @Name OR answerer = @Name OR owed_by = @Name
                OR conversation_id IN (SELECT id FROM doomed_conversations);
            """).ConfigureAwait(false);

        var toll = new Dictionary<string, int>(StringComparer.Ordinal);

        // The conversation-shaped part, which no generic scan could work out: these rows do not
        // name the agent at all. They are a peer's messages, and a peer's side of an exchange,
        // in a conversation this agent was party to -- and they go because a conversation with
        // one party deleted out of it is not a smaller record but a misleading one.
        await RazeAsync("message_referrals",
            "DELETE FROM message_referrals WHERE exchange_id IN (SELECT id FROM doomed_exchanges);")
            .ConfigureAwait(false);

        await RazeAsync("messages",
            """
            DELETE FROM messages
             WHERE exchange_id IN (SELECT id FROM doomed_exchanges)
                OR conversation_id IN (SELECT id FROM doomed_conversations);
            """).ConfigureAwait(false);

        await RazeAsync("exchanges",
            "DELETE FROM exchanges WHERE id IN (SELECT id FROM doomed_exchanges);")
            .ConfigureAwait(false);

        await RazeAsync("conversation_participants",
            "DELETE FROM conversation_participants WHERE conversation_id IN " +
            "(SELECT id FROM doomed_conversations);").ConfigureAwait(false);

        await RazeAsync("conversations",
            "DELETE FROM conversations WHERE id IN (SELECT id FROM doomed_conversations);")
            .ConfigureAwait(false);

        // And then everything that names the agent directly, discovered from the schema rather
        // than listed. A hand-written list is correct until the next migration adds a reference
        // to agents(name) and nobody updates it -- at which point the purge fails on a foreign
        // key and surfaces as an unhandled exception where an operator expected an answer. Asking
        // the database which columns point at it cannot drift.
        //
        // Cascading references are excluded because the schema has already said those rows belong
        // to the agent; they go when its row does, without being named here.
        foreach (var reference in await ReferencesToAgentsAsync(scope.Transaction, cancellationToken)
            .ConfigureAwait(false))
        {
            await RazeAsync(
                reference.TableName,
                // Interpolated because a table name cannot be a parameter. Both halves come from
                // this connection's own schema and never from a caller, and an AgentName could
                // not carry an injection anyway, being validated on construction.
                $"""DELETE FROM "{reference.TableName}" WHERE "{reference.ColumnName}" = @Name;""")
                .ConfigureAwait(false);
        }

        // Not tolled. The toll is an account of collateral -- what an operator did not ask for
        // and cannot get back -- and the agent's own row is the thing they did ask for. Counting
        // it would make an empty toll impossible and cost the report its clearest reading: that
        // nothing else went with it.
        await ExecuteAsync("DELETE FROM agents WHERE name = @Name;").ConfigureAwait(false);

        // Dropped inside the transaction that made them. A temporary table outlives a rolled-back
        // statement on some paths, and a second purge on this connection finding the previous
        // doomed set would delete entirely the wrong things.
        await ExecuteAsync("DROP TABLE doomed_exchanges;").ConfigureAwait(false);
        await ExecuteAsync("DROP TABLE doomed_conversations;").ConfigureAwait(false);

        scope.Complete();

        return PurgeOutcome.Razed(
            [.. toll.Where(t => t.Value > 0)
                    .OrderByDescending(t => t.Value)
                    .ThenBy(t => t.Key, StringComparer.Ordinal)
                    .Select(t => new PurgeToll(t.Key, t.Value))]);

        Task<int> ExecuteAsync(string sql) =>
            _connection.ExecuteAsync(
                new CommandDefinition(
                    sql,
                    new { Name = name.Value },
                    scope.Transaction,
                    cancellationToken: cancellationToken));

        // Accumulated per table rather than per statement, because one table is emptied from two
        // directions -- what belonged to a doomed conversation, and what named the agent -- and
        // an operator reading "messages 3" then "messages 2" would have to add them up.
        async Task RazeAsync(string table, string sql)
        {
            var rows = await ExecuteAsync(sql).ConfigureAwait(false);

            toll[table] = toll.GetValueOrDefault(table) + rows;
        }
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
        AgentStatus.Active => "active",
        AgentStatus.Retired => "retired",
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

        public long Connected { get; init; }

        public long NodeConnected { get; init; }

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
            "active" => AgentStatus.Active,
            "retired" => AgentStatus.Retired,

            // No legacy tokens are accepted, and that is a decision rather than an omission.
            // The store was rebuilt from empty when the broker stopped being a file every
            // adapter opened (SPEC.md 4), so no database in existence carries the old spellings
            // -- and quietly mapping 'live' or 'unreachable' onto something would be this
            // reader inventing a meaning for a row it cannot actually interpret.
            _ => throw new InvalidOperationException($"Unrecognised status '{value}'.")
        };

        public AgentRecord ToDomain() => new(
            AgentName.Create(Name),
            Adapter,
            ParseTier(Tier),
            Workspace,
            SessionRef,
            AdapterConfig,
            Connected != 0,
            ParseStatus(Status),
            Owns,
            AgentLiveness.Read(NodeConnected != 0, Connected != 0));
    }
}
