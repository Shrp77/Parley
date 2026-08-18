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
    Offline = 2
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
/// <param name="CredentialRef">
/// A pointer such as <c>env:VENDOR_API_KEY</c> or <c>keychain:parley/billing</c>, resolved at
/// invocation time and passed into the child process environment. Never a secret value, and
/// never logged or persisted after resolution — which is what makes the registry manifest
/// safe to commit.
/// </param>
/// <param name="LastSeenUtc">
/// When the agent last communicated, or <see langword="null"/> if it never has. Used to
/// bound the change summary injected on resume, so a returning agent is not reasoning about
/// code that moved while it was away.
/// </param>
/// <param name="Status">The agent's current registration and liveness state.</param>
public sealed record AgentRecord(
    AgentName Name,
    string Adapter,
    AgentTier Tier,
    string? Workspace,
    string? SessionRef,
    string? CredentialRef,
    DateTimeOffset? LastSeenUtc,
    AgentStatus Status);

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
    /// <returns>All agents in the registry, whatever their status.</returns>
    public async Task<IReadOnlyList<AgentRecord>> GetAllAsync()
    {
        var rows = await _connection.QueryAsync<AgentRow>(
            """
            SELECT name           AS Name,
                   adapter        AS Adapter,
                   tier           AS Tier,
                   workspace      AS Workspace,
                   session_ref    AS SessionRef,
                   credential_ref AS CredentialRef,
                   last_seen_utc  AS LastSeenUtc,
                   status         AS Status
            FROM agents
            ORDER BY name;
            """).ConfigureAwait(false);

        return [.. rows.Select(r => r.ToDomain())];
    }

    /// <summary>
    /// Registers an agent, or updates its configuration if already registered.
    /// </summary>
    /// <param name="agent">The agent to register.</param>
    /// <returns>The number of rows affected.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="agent"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// On conflict this updates configuration only — adapter, tier, workspace, and credential
    /// reference. Session handle, liveness, and last-seen timestamp are deliberately left
    /// alone, so reloading a manifest cannot sever a live agent's session or reset the window
    /// used to compute its change summary. Use <see cref="MarkLiveAsync"/> and
    /// <see cref="MarkAllOfflineAsync"/> for those.
    /// </remarks>
    public async Task<int> UpsertAsync(AgentRecord agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return await _connection.ExecuteAsync(
            """
            INSERT INTO agents (name, adapter, tier, workspace, session_ref, credential_ref,
                                last_seen_utc, status)
            VALUES (@Name, @Adapter, @Tier, @Workspace, @SessionRef, @CredentialRef,
                    @LastSeenUtc, @Status)
            ON CONFLICT(name) DO UPDATE SET
                adapter        = excluded.adapter,
                tier           = excluded.tier,
                workspace      = excluded.workspace,
                credential_ref = excluded.credential_ref;
            """,
            new
            {
                Name = agent.Name.Value,
                agent.Adapter,
                Tier = FormatTier(agent.Tier),
                agent.Workspace,
                agent.SessionRef,
                agent.CredentialRef,
                LastSeenUtc = agent.LastSeenUtc is { } t ? ExchangeRow.FormatTimestamp(t) : null,
                Status = FormatStatus(agent.Status)
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks every agent offline.
    /// </summary>
    /// <returns>The number of agents updated.</returns>
    /// <remarks>
    /// Called once during broker start-up. No adapter can still be attached across a process
    /// restart, so recorded liveness from the previous run is stale by definition and must be
    /// cleared before any of it is trusted.
    /// </remarks>
    public async Task<int> MarkAllOfflineAsync() =>
        await _connection.ExecuteAsync("UPDATE agents SET status = 'offline';").ConfigureAwait(false);

    /// <summary>
    /// Marks an agent live and records that it has just communicated.
    /// </summary>
    /// <param name="name">The agent to mark.</param>
    /// <param name="sessionRef">
    /// A new session handle to store, or <see langword="null"/> to keep the existing one.
    /// </param>
    /// <param name="nowUtc">The current instant, in UTC.</param>
    /// <returns>The number of rows affected: one on success, zero if the agent is unknown.</returns>
    /// <remarks>
    /// Passing <see langword="null"/> for <paramref name="sessionRef"/> preserves the stored
    /// handle rather than clearing it, so a routine liveness update cannot destroy the value
    /// that makes session resumption possible.
    /// </remarks>
    public async Task<int> MarkLiveAsync(AgentName name, string? sessionRef, DateTimeOffset nowUtc) =>
        await _connection.ExecuteAsync(
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
            }).ConfigureAwait(false);

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

        public string? CredentialRef { get; init; }

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
            CredentialRef,
            LastSeenUtc is null ? null : ExchangeRow.ParseTimestamp(LastSeenUtc),
            Status switch
            {
                "registered" => AgentStatus.Registered,
                "live" => AgentStatus.Live,
                "offline" => AgentStatus.Offline,
                _ => throw new InvalidOperationException($"Unrecognised status '{Status}'.")
            });
    }
}
