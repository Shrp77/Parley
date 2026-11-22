using Parley.Core;

namespace Parley.Client;

/// <summary>What freezing or unfreezing an agent did in one project.</summary>
/// <param name="Project">The project.</param>
/// <param name="Changed">Whether its state there changed: it was not frozen before (a freeze), or it was frozen (an unfreeze).</param>
/// <param name="Process">What became of its process, for a freeze; null for an unfreeze or when the project could not be reached.</param>
/// <param name="Problem">Why it could not be done there, or null when it could.</param>
public sealed record FrozenIn(string Project, bool Changed, FreezeProcess? Process, string? Problem);

/// <summary>What freezing or unfreezing one agent did across every project that serves it.</summary>
/// <param name="Agent">The agent.</param>
/// <param name="Projects">One entry per project.</param>
public sealed record FreezeOutcome(AgentName Agent, IReadOnlyList<FrozenIn> Projects)
{
    /// <summary>Whether it worked in every project that serves the agent.</summary>
    public bool Complete => Projects.Count > 0 && Projects.All(p => p.Problem is null);

    /// <summary>Whether anything changed anywhere.</summary>
    public bool AnyChanged => Projects.Any(p => p.Changed);
}

/// <summary>What freezing or unfreezing every agent of a project did.</summary>
/// <param name="Agents">One outcome per agent that was acted on.</param>
/// <param name="Problem">Why the project's agents could not be read, so nothing was done, or null when they were read.</param>
/// <remarks>
/// The problem is kept apart from an empty list because they mean opposite things to a person about to restart the machine:
/// "nobody needed freezing" and "nobody was frozen".
/// </remarks>
public sealed record FreezeAllOutcome(IReadOnlyList<FreezeOutcome> Agents, string? Problem)
{
    /// <summary>How many agents it worked for in every project that serves them.</summary>
    public int Done => Agents.Count(agent => agent.Complete);
}

/// <summary>
/// Freezes and unfreezes an agent in every project that serves it, so one act freezes the agent whichever project's work would wake it
/// (SPEC.md §14).
/// </summary>
/// <remarks>
/// <para>
/// <b>The surface loops, not the broker.</b> A broker freezes one project and a store has one broker, so what is the same agent in
/// several projects is frozen by asking each project's broker. The agent is <c>role@machine</c> in each, and the one process serves
/// them all (SPEC.md §11.6): freezing it in one project only would end the process and let the next project's work start it again.
/// </para>
/// <para>
/// A project that cannot be reached is reported, never skipped: the agent is not frozen there, and the person is told so that they
/// can freeze it when it is back.
/// </para>
/// <para>
/// Shared by the web console and the terminal, as <c>AgentWithdrawal</c> is, so neither calls the broker's operation itself.
/// </para>
/// </remarks>
public static class AgentFreezing
{
    /// <summary>The projects whose registry lists the agent.</summary>
    /// <param name="directory">The projects and their brokers.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The project names, and those that could not be read, with why.</returns>
    public static async Task<(IReadOnlyList<string> Serving, IReadOnlyList<(string Project, string Problem)> Unreachable)> ServedByAsync(
        BrokerDirectory directory, AgentName agent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var serving = new List<string>();
        var unreachable = new List<(string, string)>();

        foreach (var project in directory.Projects)
        {
            var broker = new BrokerClient(directory.ClientFor(project));

            var read = await BrokerDirectory.ReadAsync(async () =>
                await broker.RegistryAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

            if (read.Value is null)
            {
                unreachable.Add((project.Name.Value, read.Problem ?? "it did not answer"));
            }
            else if (read.Value.Any(a => a.Name == agent && a.Status == AgentStatus.Active))
            {
                serving.Add(project.Name.Value);
            }
        }

        return (serving, unreachable);
    }

    /// <summary>Says which projects a freeze or an unfreeze of an agent reaches, for the person to read before agreeing (SPEC.md §14).</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="serving">The projects that list it.</param>
    /// <param name="unreachable">The projects that could not be asked.</param>
    /// <returns>The sentences, the same on every surface.</returns>
    public static string Reach(
        AgentName agent, IReadOnlyList<string> serving, IReadOnlyList<(string Project, string Problem)> unreachable)
    {
        ArgumentNullException.ThrowIfNull(serving);
        ArgumentNullException.ThrowIfNull(unreachable);

        return serving.Count == 0
            ? $"No project that can be reached lists '{agent.Value}'."
            : $"This reaches {string.Join(", ", serving)}."
              + (unreachable.Count > 0
                  ? $" It cannot be reached in {string.Join(", ", unreachable.Select(u => u.Project))}, so it is not done there."
                  : string.Empty);
    }

    /// <summary>Freezes an agent in every project that serves it.</summary>
    /// <param name="directory">The projects and their brokers.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="keepFrozen">Whether the freeze outlasts a start of the broker, so a logon does not lift it.</param>
    /// <param name="cancellationToken">Cancels the acts.</param>
    /// <returns>What happened in each project.</returns>
    public static Task<FreezeOutcome> FreezeAsync(
        BrokerDirectory directory, AgentName agent, bool keepFrozen = false, CancellationToken cancellationToken = default) =>
        ActAsync(directory, agent, freeze: true, keepFrozen, cancellationToken);

    /// <summary>Unfreezes an agent in every project where it is frozen.</summary>
    /// <param name="directory">The projects and their brokers.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="cancellationToken">Cancels the acts.</param>
    /// <returns>What happened in each project.</returns>
    public static Task<FreezeOutcome> UnfreezeAsync(
        BrokerDirectory directory, AgentName agent, CancellationToken cancellationToken = default) =>
        ActAsync(directory, agent, freeze: false, keepFrozen: false, cancellationToken);

    private static async Task<FreezeOutcome> ActAsync(
        BrokerDirectory directory, AgentName agent, bool freeze, bool keepFrozen, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var (serving, unreachable) = await ServedByAsync(directory, agent, cancellationToken).ConfigureAwait(false);
        var results = new List<FrozenIn>();

        foreach (var (project, problem) in unreachable)
        {
            results.Add(new FrozenIn(project, false, null, problem));
        }

        foreach (var name in serving)
        {
            var broker = new BrokerClient(directory.ClientFor(directory.Find(name)!));

            var done = await BrokerDirectory.ReadAsync(async () => freeze
                ? await broker.FreezeAsync(agent, keepFrozen, cancellationToken).ConfigureAwait(false)
                : new FreezeResult(await broker.UnfreezeAsync(agent, cancellationToken).ConfigureAwait(false), FreezeProcess.NotRunning))
                .ConfigureAwait(false);

            results.Add(done.Value is { } result
                ? new FrozenIn(name, result.Changed, freeze ? result.Process : null, null)
                : new FrozenIn(name, false, null, done.Problem));
        }

        return new FreezeOutcome(agent, [.. results.OrderBy(r => r.Project, StringComparer.Ordinal)]);
    }

    /// <summary>Freezes every agent the project lists, in every project that serves each.</summary>
    /// <param name="directory">The projects and their brokers.</param>
    /// <param name="project">The project whose agents they are.</param>
    /// <param name="freeze"><see langword="true"/> to freeze them, <see langword="false"/> to unfreeze them.</param>
    /// <param name="keepFrozen">For a freeze, whether it outlasts a start of the broker.</param>
    /// <param name="cancellationToken">Cancels the acts.</param>
    /// <returns>One outcome per agent, and why the project could not be read when it could not.</returns>
    /// <remarks>
    /// The person's seat and the facilitator are left out: neither is started by a node, and storage refuses to freeze either.
    /// A retired agent is left out too, since nothing wakes it.
    /// </remarks>
    public static async Task<FreezeAllOutcome> AllAsync(
        BrokerDirectory directory, string project, bool freeze, bool keepFrozen = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var settings = directory.Find(project);

        if (settings is null)
        {
            return new FreezeAllOutcome([], $"This console has no project named '{project}'.");
        }

        var read = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(directory.ClientFor(settings)).RegistryAsync(cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        if (read.Value is not { } registry)
        {
            return new FreezeAllOutcome([], read.Problem ?? "This project's broker did not answer.");
        }

        var outcomes = new List<FreezeOutcome>();

        foreach (var agent in registry
            .Where(a => a.Status == AgentStatus.Active && !a.Facilitator && !PersonSeat.Is(a.Name))
            .Where(a => freeze ? !a.Frozen : a.Frozen)
            .Select(a => a.Name))
        {
            outcomes.Add(await ActAsync(directory, agent, freeze, keepFrozen, cancellationToken).ConfigureAwait(false));
        }

        return new FreezeAllOutcome(outcomes, null);
    }

    /// <summary>Names, for a freeze or unfreeze of every agent in one project, each agent it also changed in another project.</summary>
    /// <param name="all">What the act did.</param>
    /// <param name="project">The project the person acted in.</param>
    /// <param name="freeze">Whether it froze rather than unfroze.</param>
    /// <returns>A sentence per agent, each starting with a space, or empty.</returns>
    /// <remarks>
    /// One process serves every project an agent is in, so the act reaches them all; said by name, so a person who unfreezes
    /// one project learns an agent frozen for another project's sake was released there too (review, lens 2 run B on 44c8e1e6).
    /// </remarks>
    public static string Elsewhere(FreezeAllOutcome all, string project, bool freeze)
    {
        ArgumentNullException.ThrowIfNull(all);

        return string.Concat(all.Agents
            .Select(o => (o.Agent, Others: o.Projects
                .Where(p => p.Problem is null && p.Changed && !string.Equals(p.Project, project, StringComparison.Ordinal))
                .Select(p => p.Project)
                .ToList()))
            .Where(o => o.Others.Count > 0)
            .Select(o => $" '{o.Agent.Value}' was {(freeze ? "frozen" : "unfrozen")} in {string.Join(", ", o.Others.Select(p => $"'{p}'"))} too."));
    }

    /// <summary>Says what freezing did, in a few sentences a person can act on.</summary>
    /// <param name="outcome">What happened.</param>
    /// <returns>The sentences.</returns>
    public static string Describe(FreezeOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (outcome.Projects.Count == 0)
        {
            return $"No project this console can reach lists '{outcome.Agent.Value}', so there was nothing to do.";
        }

        var lines = outcome.Projects.Select(p => p.Problem is { } problem
            ? $"{p.Project}: not done. {problem}"
            : $"{p.Project}: {(p.Changed ? "changed" : "already so")}{ProcessWords(p.Process)}.");

        return string.Join(' ', lines);
    }

    private static string ProcessWords(FreezeProcess? process) => process switch
    {
        FreezeProcess.Stopping => "; its node was asked to stop it once its turn ends",
        FreezeProcess.OpenOutside => "; it is open outside Parley, so its process stays until somebody closes it, but nothing will start it again",
        FreezeProcess.LeftRunning => "; a node that is gone left its process running, so it runs until you end it with Kill, but nothing will start it again",
        FreezeProcess.NoNode => "; no node is joined to stop it, so the freeze stands when one returns",
        _ => string.Empty
    };
}
