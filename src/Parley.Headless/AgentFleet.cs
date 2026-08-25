using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;

namespace Parley.Headless;

/// <summary>
/// What the fleet did about one agent on one pass.
/// </summary>
/// <param name="Agent">Who.</param>
/// <param name="Started">Whether this pass is what started them.</param>
/// <param name="Turn">How their turn went, or null if they were not given one.</param>
/// <param name="OwedBefore">What they owed when the pass found them.</param>
/// <param name="OwedAfter">What they owed after their turn.</param>
/// <param name="Skipped">Why they were passed over, or null if they were not.</param>
public sealed record FleetAction(
    AgentName Agent,
    bool Started,
    TurnOutcome? Turn,
    long OwedBefore,
    long OwedAfter,
    string? Skipped)
{
    /// <summary>Gets whether the turn left the agent owing less than it did.</summary>
    public bool MadeProgress => Turn is not null && OwedAfter < OwedBefore;
}

/// <summary>
/// Keeps a set of agents resident and gives them turns when their peers need them.
/// </summary>
/// <remarks>
/// <para>
/// The supervisor for SPEC.md §11.1's Headless tier. Agents are started once and kept alive:
/// each holds one conversation for its whole life, so it accumulates the history of every
/// exchange it has taken part in and arrives at each new turn remembering the last.
/// </para>
/// <para>
/// <b>It supervises agents; it does not direct them.</b> Principle 2.6 says no agent has
/// authority over another, and a fleet is the design most likely to breach that by accident. It
/// does not: what it delegates is a turn, never a judgement. It passes no question, reads no
/// message, and writes nothing to any exchange — an agent given a turn calls
/// <c>parley_inbox</c> through its own server, under the identity §13 takes from its own launch
/// environment, and decides for itself what to do. The fleet cannot tell an agent what to say
/// any more than a person opening a terminal can.
/// </para>
/// <para>
/// Turns are taken one at a time. Each is a whole agent thinking, so running them concurrently
/// would multiply the token cost of a pass by the number of agents with mail, and the work is
/// not urgent — anything missed is picked up on the next pass. A pass that gives three agents a
/// turn in sequence is one an operator can watch happening; three at once is a bill.
/// </para>
/// </remarks>
public sealed class AgentFleet : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _lease;
    private readonly Dictionary<string, Resident> _running = new(StringComparer.Ordinal);

    /// <summary>Creates a fleet against an open database.</summary>
    /// <param name="connection">The open, migrated database.</param>
    /// <param name="clock">The clock turns are measured against.</param>
    /// <param name="lease">
    /// How long an agent stays trusted as somebody else's after it was last heard from.
    /// </param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public AgentFleet(SqliteConnection connection, TimeProvider clock, TimeSpan lease)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(clock);

        _connection = connection;
        _clock = clock;
        _lease = lease;
    }

    /// <summary>Gets the agents currently held open, by name.</summary>
    public IReadOnlyCollection<string> Running => _running.Keys;

    /// <summary>Gets a running agent's session handle, for an operator who wants to read along.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>The runner's session identifier, or null if it is not running or has not said.</returns>
    public string? SessionOf(AgentName agent) =>
        _running.TryGetValue(agent.Value, out var resident) ? resident.Agent.SessionId : null;

    /// <summary>
    /// Runs one pass: stops what has been withdrawn, starts what is needed, and takes turns.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What was done, in the order it was done.</returns>
    public async Task<IReadOnlyList<FleetAction>> TickAsync(
        CancellationToken cancellationToken = default)
    {
        var actions = new List<FleetAction>();
        var repository = new InvocationRepository(_connection);

        await ReapAsync().ConfigureAwait(false);
        await StopWithdrawnAsync(repository, actions, cancellationToken).ConfigureAwait(false);

        var candidates = await repository.FindOwedAsync(cancellationToken).ConfigureAwait(false);
        var now = _clock.GetUtcNow();

        foreach (var candidate in candidates)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            InvocationCommand command;

            try
            {
                command = InvocationCommand.Parse(candidate.InvokeCommand);
            }
            catch (ArgumentException ex)
            {
                actions.Add(Skip(candidate, $"its invoke command is unusable: {ex.Message}"));
                continue;
            }

            var started = false;

            if (!_running.TryGetValue(candidate.Agent.Value, out var resident))
            {
                // Somebody else's session. §14's lease is the only evidence available that a
                // person has this repository open, and starting a second process behind one
                // identity would have two agents draining one inbox -- which the exchange's turn
                // check refuses, after both have spent tokens getting there.
                if (candidate.LastSeenUtc is { } seen && now - seen < _lease)
                {
                    actions.Add(Skip(candidate, "attended: a session is already running it"));
                    continue;
                }

                try
                {
                    var agent = ResidentAgent.Start(
                        candidate.Agent, command, candidate.Workspace, _clock);

                    resident = new Resident(agent, candidate.InvokeCommand);
                    _running[candidate.Agent.Value] = resident;
                    started = true;
                }
                catch (Exception ex) when (
                    ex is InvocationFailedException or DirectoryNotFoundException)
                {
                    // Retried on the next pass rather than backed off. Starting a process that
                    // fails costs no tokens, and the failure is usually something an operator is
                    // in the middle of fixing -- a path, a runner being installed. A cooldown
                    // would mostly delay recovery.
                    actions.Add(Skip(candidate, ex.Message));
                    continue;
                }
            }
            else if (!string.Equals(resident.Command, candidate.InvokeCommand, StringComparison.Ordinal))
            {
                // The launch configuration changed under a running agent. Restarting is the
                // honest reading: the operator changed how this agent should run, and an agent
                // still running the old command line is one the registry describes incorrectly.
                // It costs the agent's accumulated context, which is why it happens only on a
                // real change rather than on every pass.
                await resident.Agent.DisposeAsync().ConfigureAwait(false);
                _running.Remove(candidate.Agent.Value);

                actions.Add(Skip(candidate, "its invoke command changed; restarting next pass"));
                continue;
            }

            TurnOutcome outcome;
            var turnStarted = _clock.GetUtcNow();

            try
            {
                outcome = await resident.Agent
                    .TakeTurnAsync(InvocationCommand.Prompt, command.Timeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var after = await repository
                .CountOwedAsync(candidate.Agent, CancellationToken.None).ConfigureAwait(false);

            await repository.RecordAsync(
                candidate.Agent, candidate.Owed, after, turnStarted, outcome.Elapsed,
                outcome.Succeeded ? 0 : 1, outcome.TimedOut, outcome.CostUsd,
                outcome.ModelTurns, outcome.PermissionDenials,
                CancellationToken.None).ConfigureAwait(false);

            actions.Add(new FleetAction(
                candidate.Agent, started, outcome, candidate.Owed, after, Skipped: null));
        }

        return actions;
    }

    /// <summary>Tells one agent to quit.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="grace">How long to allow before killing it.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns><see langword="true"/> if it was running and has now stopped.</returns>
    public async Task<bool> StopAsync(
        AgentName agent, TimeSpan grace, CancellationToken cancellationToken = default)
    {
        if (!_running.Remove(agent.Value, out var resident))
        {
            return false;
        }

        await resident.Agent.ShutdownAsync(grace, cancellationToken).ConfigureAwait(false);
        await resident.Agent.DisposeAsync().ConfigureAwait(false);

        return true;
    }

    /// <summary>Tells every agent to quit.</summary>
    /// <param name="grace">How long to allow each before killing it.</param>
    /// <param name="cancellationToken">Cancels the wait, after which they are killed.</param>
    /// <returns>How many were stopped.</returns>
    /// <remarks>
    /// Concurrent, unlike taking turns. Shutdown spends no tokens, and a fleet of ten agents each
    /// given its grace period in sequence would take ten times longer to answer a Ctrl-C than a
    /// person will wait before reaching for something less polite.
    /// </remarks>
    public async Task<int> StopAllAsync(
        TimeSpan grace, CancellationToken cancellationToken = default)
    {
        var residents = _running.Values.ToList();
        _running.Clear();

        await Task.WhenAll(residents.Select(async resident =>
        {
            await resident.Agent.ShutdownAsync(grace, cancellationToken).ConfigureAwait(false);
            await resident.Agent.DisposeAsync().ConfigureAwait(false);
        })).ConfigureAwait(false);

        return residents.Count;
    }

    /// <summary>Stops everything the fleet is running.</summary>
    /// <returns>A task that completes when the fleet is empty.</returns>
    public async ValueTask DisposeAsync() =>
        await StopAllAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);

    /// <summary>Forgets agents whose processes have ended on their own.</summary>
    /// <returns>A task that completes when they are released.</returns>
    private async Task ReapAsync()
    {
        var dead = _running
            .Where(pair => !pair.Value.Agent.IsAlive)
            .Select(pair => pair.Key)
            .ToList();

        foreach (var name in dead)
        {
            if (_running.Remove(name, out var resident))
            {
                await resident.Agent.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Stops agents an operator has retired or withdrawn consent for.</summary>
    /// <param name="repository">The registry.</param>
    /// <param name="actions">Where to record what was stopped.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>A task that completes when they have stopped.</returns>
    private async Task StopWithdrawnAsync(
        InvocationRepository repository,
        List<FleetAction> actions,
        CancellationToken cancellationToken)
    {
        if (_running.Count == 0)
        {
            return;
        }

        var withdrawn = await repository
            .FindWithdrawnAsync(cancellationToken).ConfigureAwait(false);

        // Intersected here rather than in the query, for the reason FindWithdrawnAsync gives.
        foreach (var name in withdrawn.Where(_running.ContainsKey).ToList())
        {
            var agent = AgentName.Create(name);

            if (await StopAsync(agent, TimeSpan.FromSeconds(15), cancellationToken)
                .ConfigureAwait(false))
            {
                actions.Add(new FleetAction(
                    agent, Started: false, Turn: null, OwedBefore: 0, OwedAfter: 0,
                    Skipped: "withdrawn by the operator; told to quit"));
            }
        }
    }

    private static FleetAction Skip(InvocationCandidate candidate, string why) =>
        new(candidate.Agent, Started: false, Turn: null, candidate.Owed, candidate.Owed, why);

    /// <summary>A running agent and the command line it was started with.</summary>
    /// <param name="Agent">The process.</param>
    /// <param name="Command">The template it came from, kept so a change can be noticed.</param>
    private sealed record Resident(ResidentAgent Agent, string Command);
}
