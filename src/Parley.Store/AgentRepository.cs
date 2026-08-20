using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// How an agent attaches to the broker, classified by capability rather than by vendor.
/// </summary>
/// <remarks>
/// The broker degrades gracefully across tiers: a question to a pull-only agent is queued
/// rather than pushed, and a question to a headless agent triggers an invocation.
/// </remarks>
public enum AgentTier
{
    /// <summary>
    /// Native tool calls outbound, and inbound messages pushed into a running turn. The agent
    /// can be interrupted by an arriving message.
    /// </summary>
    Full = 0,

    /// <summary>
    /// Native tool calls outbound, but inbound messages only seen when the agent drains its
    /// inbox. It cannot be interrupted.
    /// </summary>
    Pull = 1,

    /// <summary>
    /// No persistent session. The broker invokes the responder with a one-shot prompt and
    /// captures the reply.
    /// </summary>
    /// <remarks>
    /// The responder need not be an agent at all — a plain model API call, a local model, a
    /// script that queries a contracts directory, or a human with a notification all qualify.
    /// Anything that accepts a bounded question and returns text can participate.
    /// </remarks>
    Headless = 2
}

/// <summary>
/// The registration and liveness state of an agent.
/// </summary>
public enum AgentStatus
{
    /// <summary>Known to the registry but never yet connected.</summary>
    Registered = 0,

    /// <summary>Currently attached to the broker and able to receive messages.</summary>
    Live = 1,

    /// <summary>
    /// Previously live, now disconnected. Its open exchanges are orphaned and its pair slots
    /// released.
    /// </summary>
    Offline = 2,

    /// <summary>
    /// Spawning was attempted and failed repeatedly, so the agent is not worth routing to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not a worse <see cref="Offline"/>. Offline is the ordinary resting state
    /// of a lazily spawned agent between questions, and must stay routable — refusing traffic
    /// to an offline agent would refuse the very traffic meant to wake it. This says something
    /// else: the broker tried, and there was nothing there.
    /// </para>
    /// <para>
    /// The usual cause is a repository that was deleted, moved, or renamed while its agent was
    /// still registered. Questions to such an agent are refused at admission, because
    /// accepting them costs a full TTL each and tells the asker nothing. Resolving what is
    /// already outstanding is the operator's decision, not the broker's: a deleted repository
    /// is often temporary, and force-closing on the broker's initiative would discard real
    /// questions to save a human a judgement they are better placed to make.
    /// </para>
    /// </remarks>
    Unreachable = 3,

    /// <summary>
    /// Retired by an operator: the repository is gone for good.
    /// </summary>
    /// <remarks>
    /// The only status the broker never sets on its own evidence. <see cref="Unreachable"/> is
    /// the broker reporting that it tried and failed, which a re-clone or a restored backup can
    /// invalidate at any moment; this is a person saying it will not come back. Start-up clears
    /// every other status as stale liveness and leaves this one, because it is a judgement
    /// rather than an observation.
    /// </remarks>
    Retired
}

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
    AgentStatus Status);

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
                       last_seen_utc  AS LastSeenUtc,
                       status         AS Status
                FROM agents
                ORDER BY name;
                """,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
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
    /// used to compute its change summary. Use <see cref="MarkLiveAsync"/> and
    /// <see cref="MarkAllOfflineAsync"/> for those.
    /// </remarks>
    public async Task<int> UpsertAsync(AgentRecord agent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return await _connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO agents (name, adapter, tier, workspace, session_ref, adapter_config,
                                    last_seen_utc, status)
                VALUES (@Name, @Adapter, @Tier, @Workspace, @SessionRef, @AdapterConfig,
                        @LastSeenUtc, @Status)
                ON CONFLICT(name) DO UPDATE SET
                    adapter        = excluded.adapter,
                    tier           = excluded.tier,
                    workspace      = excluded.workspace,
                    adapter_config = excluded.adapter_config;
                """,
                new
                {
                    Name = agent.Name.Value,
                    agent.Adapter,
                    Tier = FormatTier(agent.Tier),
                    agent.Workspace,
                    agent.SessionRef,
                    agent.AdapterConfig,
                    LastSeenUtc = agent.LastSeenUtc is { } t ? ExchangeRow.FormatTimestamp(t) : null,
                    Status = FormatStatus(agent.Status)
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks every agent offline.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The number of agents updated.</returns>
    /// <remarks>
    /// <para>
    /// Called once during broker start-up. No adapter can still be attached across a process
    /// restart, so recorded liveness from the previous run is stale by definition and must be
    /// cleared before any of it is trusted.
    /// </para>
    /// <para>
    /// This deliberately resets <see cref="AgentStatus.Unreachable"/> too, and that is what
    /// makes the state recoverable rather than a one-way door. Nothing spawns an agent the
    /// broker refuses to route to, so an unreachable agent can never demonstrate that it is
    /// back; clearing the mark at start-up gives it that chance. Carrying the mark across a
    /// restart would preserve a conclusion the restart may have invalidated, since re-cloning
    /// a repository is exactly the kind of thing that happens between runs.
    /// </para>
    /// </remarks>
    public async Task<int> MarkAllOfflineAsync(CancellationToken cancellationToken = default) =>
        await _connection.ExecuteAsync(
            new CommandDefinition(
                // Retired is excluded because it is not stale liveness: it is an operator's
                // judgement that a repository is gone for good, and a restart is no evidence
                // against it. Every other status is what the previous run observed, which the
                // restart itself may well have invalidated.
                "UPDATE agents SET status = 'offline' WHERE status <> 'retired';",
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
                 WHERE name = @Name;
                """,
                new
                {
                    Name = name.Value,
                    SessionRef = sessionRef,
                    Now = ExchangeRow.FormatTimestamp(nowUtc)
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

    /// <summary>
    /// Records that an agent could not be spawned and should not be routed to.
    /// </summary>
    /// <param name="name">The agent to mark.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The number of rows affected: one on success, zero if the agent is unknown.</returns>
    /// <remarks>
    /// <para>
    /// Called by the broker after a threshold of consecutive failed spawns, not after the
    /// first: a single failure is as likely to be a slow clone or a transient lock as a
    /// deleted repository. How many failures constitute a threshold is broker policy, and the
    /// count itself is deliberately not stored — a restart should retry, because a restart is
    /// often precisely when the situation has changed.
    /// </para>
    /// <para>
    /// This does not resolve the agent's outstanding exchanges. It stops new ones being
    /// admitted and makes the state visible; deciding what becomes of the questions already
    /// asked is the operator's, through retirement. See SPEC.md §14.
    /// </para>
    /// <para>
    /// The session handle is left intact, so an agent that reappears can resume rather than
    /// start cold.
    /// </para>
    /// <para>
    /// This is a routing kill-switch and belongs to the broker alone. §10.1 exposes nothing
    /// like it, and nothing like it may be added: an agent able to mark a peer unreachable
    /// could silence it — every question to that peer refused, indefinitely, on one agent's
    /// say-so. No agent has authority over another, and this is one of the places that would
    /// be easy to give away by accident.
    /// </para>
    /// </remarks>
    public async Task<int> MarkUnreachableAsync(
        AgentName name,
        CancellationToken cancellationToken = default) =>
        await _connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE agents SET status = 'unreachable' WHERE name = @Name;",
                new { Name = name.Value },
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
                "UPDATE agents SET status = 'retired' WHERE name = @Name;",
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
        AgentStatus.Registered => "registered",
        AgentStatus.Live => "live",
        AgentStatus.Offline => "offline",
        AgentStatus.Unreachable => "unreachable",
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

        public string? LastSeenUtc { get; init; }

        public string Status { get; init; } = "";

        public AgentRecord ToDomain() => new(
            AgentName.Create(Name),
            Adapter,
            Tier switch
            {
                "full" => AgentTier.Full,
                "pull" => AgentTier.Pull,
                "headless" => AgentTier.Headless,
                _ => throw new InvalidOperationException($"Unrecognised tier '{Tier}'.")
            },
            Workspace,
            SessionRef,
            AdapterConfig,
            LastSeenUtc is null ? null : ExchangeRow.ParseTimestamp(LastSeenUtc),
            Status switch
            {
                "registered" => AgentStatus.Registered,
                "live" => AgentStatus.Live,
                "offline" => AgentStatus.Offline,
                "unreachable" => AgentStatus.Unreachable,
                "retired" => AgentStatus.Retired,
                _ => throw new InvalidOperationException($"Unrecognised status '{Status}'.")
            });
    }
}
