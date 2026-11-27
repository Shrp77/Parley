using Parley.Core;

namespace Parley.Client;

/// <summary>What freezing or unfreezing an agent did in one project.</summary>
/// <param name="Project">The project.</param>
/// <param name="Changed">Whether its state there changed: it was not frozen before (a freeze), or it was frozen (an unfreeze).</param>
/// <param name="Process">What became of its process, for a freeze; null for an unfreeze or when the project could not be reached.</param>
/// <param name="Problem">Why it could not be done there, or null when it could.</param>
/// <param name="Named">
/// The agent's name in this project, or null when the project could not be read. It differs from the name the person acted on
/// when the repository carries another name here (SPEC.md §4.1.1).
/// </param>
public sealed record FrozenIn(string Project, bool Changed, FreezeProcess? Process, string? Problem, AgentName? Named = null);

/// <summary>A project that serves an agent, and the agent's name there.</summary>
/// <param name="Project">The project.</param>
/// <param name="Agent">The agent's name in that project, which need not be the name it has where the person acted (SPEC.md §4.1.1).</param>
public sealed record ServedIn(string Project, AgentName Agent);

/// <summary>What freezing or unfreezing one agent did across every project that serves it.</summary>
/// <param name="Agent">The agent, by its name in the project the person acted in.</param>
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
/// several projects is frozen by asking each project's broker. The one process serves them all (SPEC.md §11.6): freezing it in one
/// project only would end the process and let the next project's work start it again.
/// </para>
/// <para>
/// <b>The same agent is the same identity, never the same name.</b> A repository's identifier is the agent and its name is only
/// what a project shows (SPEC.md §4.1.1), so one checkout may be <c>inv@pc</c> in one project and <c>stock@pc</c> in another,
/// and two unrelated repositories may share a name in two projects. Each project's row is found by the identity and the machine
/// of the row where the person acted, and acted on under that project's own name (review, lens 2 run A on 5de89187: matching by
/// name froze an unrelated agent, missed a renamed one, and let an agent that took another's name shed its freeze).
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
    /// <summary>The projects that serve the agent the person acts on in one project, and its name in each.</summary>
    /// <param name="directory">The projects and their brokers.</param>
    /// <param name="project">The project the person acts in, whose registry says which agent this is.</param>
    /// <param name="agent">The agent, by its name in that project.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>
    /// The projects and the agent's name in each, and those that could not be read, with why. When the acting project cannot be
    /// read nothing else is asked: without its row there is no telling which agent elsewhere is this one.
    /// </returns>
    public static async Task<(IReadOnlyList<ServedIn> Serving, IReadOnlyList<(string Project, string Problem)> Unreachable)> ServedByAsync(
        BrokerDirectory directory, string project, AgentName agent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var acting = directory.Find(project);

        if (acting is null)
        {
            return ([], [(project, $"This console has no project named '{project}'.")]);
        }

        var read = await RegistryAsync(directory, acting, cancellationToken).ConfigureAwait(false);

        if (read.Value is not { } registry)
        {
            return ([], [(acting.Name.Value, read.Problem ?? "it did not answer")]);
        }

        // Its row whatever its standing here, so an agent retired in this project is still found where it is active.
        return registry.FirstOrDefault(a => a.Name == agent) is { } row
            ? await ServedByAsync(directory, acting, registry, row, cancellationToken).ConfigureAwait(false)
            : ([], []);
    }

    private static async Task<(IReadOnlyList<ServedIn> Serving, IReadOnlyList<(string Project, string Problem)> Unreachable)> ServedByAsync(
        BrokerDirectory directory,
        ProjectSettings acting,
        IReadOnlyList<AgentRecord> actingRegistry,
        AgentRecord row,
        CancellationToken cancellationToken)
    {
        var serving = new List<ServedIn>();
        var unreachable = new List<(string, string)>();

        foreach (var project in directory.Projects)
        {
            IReadOnlyList<AgentRecord>? registry = string.Equals(project.Name.Value, acting.Name.Value, StringComparison.Ordinal)
                ? actingRegistry
                : null;
            string? problem = null;

            if (registry is null)
            {
                var read = await RegistryAsync(directory, project, cancellationToken).ConfigureAwait(false);

                registry = read.Value;
                problem = read.Problem;
            }

            if (registry is null)
            {
                unreachable.Add((project.Name.Value, problem ?? "it did not answer"));
            }
            else if (registry.FirstOrDefault(a => a.Status == AgentStatus.Active && IsTheSameAgent(row, a)) is { } match)
            {
                serving.Add(new ServedIn(project.Name.Value, match.Name));
            }
        }

        return (serving, unreachable);
    }

    /// <summary>Whether a row in some project is the agent the person acted on.</summary>
    /// <param name="acted">The agent's row in the project the person acted in.</param>
    /// <param name="candidate">A row in some project.</param>
    /// <returns>Whether they are one agent.</returns>
    /// <remarks>
    /// The identity and the machine, because the same identifier on another machine is another agent (SPEC.md §4.1.1). A row
    /// with no identity was registered by an adapter that presented none, which registers by name alone; for that row the name
    /// is all there is to match on, so it is matched by name, knowing that a name may be another repository's elsewhere.
    /// </remarks>
    private static bool IsTheSameAgent(AgentRecord acted, AgentRecord candidate) =>
        acted.Identity.Known
            ? candidate.Identity == acted.Identity
              && string.Equals(candidate.Name.Machine, acted.Name.Machine, StringComparison.Ordinal)
            : candidate.Name == acted.Name;

    private static Task<ProjectRead<IReadOnlyList<AgentRecord>>> RegistryAsync(
        BrokerDirectory directory, ProjectSettings project, CancellationToken cancellationToken) =>
        BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(directory.ClientFor(project)).RegistryAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>Says which projects a freeze or an unfreeze of an agent reaches, for the person to read before agreeing (SPEC.md §14).</summary>
    /// <param name="agent">The agent, by its name where the person acts.</param>
    /// <param name="serving">The projects that serve it, and its name in each.</param>
    /// <param name="unreachable">The projects that could not be asked.</param>
    /// <returns>The sentences, the same on every surface.</returns>
    public static string Reach(
        AgentName agent, IReadOnlyList<ServedIn> serving, IReadOnlyList<(string Project, string Problem)> unreachable)
    {
        ArgumentNullException.ThrowIfNull(serving);
        ArgumentNullException.ThrowIfNull(unreachable);

        return serving.Count == 0
            ? $"No project that can be reached lists '{agent.Value}'."
            : $"This reaches {string.Join(", ", serving.Select(s => s.Project + As(agent, s.Agent)))}."
              + (unreachable.Count > 0
                  ? $" It cannot be reached in {string.Join(", ", unreachable.Select(u => u.Project))}, so it is not done there."
                  : string.Empty);
    }

    /// <summary>Freezes an agent in every project that serves it.</summary>
    /// <param name="directory">The projects and their brokers.</param>
    /// <param name="project">The project the person acts in.</param>
    /// <param name="agent">The agent, by its name in that project.</param>
    /// <param name="keepFrozen">Whether the freeze outlasts a start of the broker, so a logon does not lift it.</param>
    /// <param name="cancellationToken">Cancels the acts.</param>
    /// <returns>What happened in each project.</returns>
    public static Task<FreezeOutcome> FreezeAsync(
        BrokerDirectory directory, string project, AgentName agent, bool keepFrozen = false, CancellationToken cancellationToken = default) =>
        ActAsync(directory, project, agent, freeze: true, keepFrozen, cancellationToken);

    /// <summary>Unfreezes an agent in every project where it is frozen.</summary>
    /// <param name="directory">The projects and their brokers.</param>
    /// <param name="project">The project the person acts in.</param>
    /// <param name="agent">The agent, by its name in that project.</param>
    /// <param name="cancellationToken">Cancels the acts.</param>
    /// <returns>What happened in each project.</returns>
    public static Task<FreezeOutcome> UnfreezeAsync(
        BrokerDirectory directory, string project, AgentName agent, CancellationToken cancellationToken = default) =>
        ActAsync(directory, project, agent, freeze: false, keepFrozen: false, cancellationToken);

    private static async Task<FreezeOutcome> ActAsync(
        BrokerDirectory directory, string project, AgentName agent, bool freeze, bool keepFrozen, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var served = await ServedByAsync(directory, project, agent, cancellationToken).ConfigureAwait(false);

        return await ActAsync(directory, agent, served, freeze, keepFrozen, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<FreezeOutcome> ActAsync(
        BrokerDirectory directory,
        AgentName agent,
        (IReadOnlyList<ServedIn> Serving, IReadOnlyList<(string Project, string Problem)> Unreachable) served,
        bool freeze,
        bool keepFrozen,
        CancellationToken cancellationToken)
    {
        var results = new List<FrozenIn>();

        foreach (var (project, problem) in served.Unreachable)
        {
            results.Add(new FrozenIn(project, false, null, problem));
        }

        foreach (var (name, named) in served.Serving)
        {
            var broker = new BrokerClient(directory.ClientFor(directory.Find(name)!));

            // Under this project's own name for it: a broker knows the agent by nothing else.
            var done = await BrokerDirectory.ReadAsync(async () => freeze
                ? await broker.FreezeAsync(named, keepFrozen, cancellationToken).ConfigureAwait(false)
                : new FreezeResult(await broker.UnfreezeAsync(named, cancellationToken).ConfigureAwait(false), FreezeProcess.NotRunning))
                .ConfigureAwait(false);

            results.Add(done.Value is { } result
                ? new FrozenIn(name, result.Changed, freeze ? result.Process : null, null, named)
                : new FrozenIn(name, false, null, done.Problem, named));
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
    /// A retired agent is left out too, since nothing wakes it. Each agent is found elsewhere by its identity, as one is.
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

        var read = await RegistryAsync(directory, settings, cancellationToken).ConfigureAwait(false);

        if (read.Value is not { } registry)
        {
            return new FreezeAllOutcome([], read.Problem ?? "This project's broker did not answer.");
        }

        var outcomes = new List<FreezeOutcome>();

        foreach (var row in registry
            .Where(a => a.Status == AgentStatus.Active && !a.Facilitator && !PersonSeat.Is(a.Name))
            .Where(a => freeze ? !a.Frozen : a.Frozen))
        {
            var served = await ServedByAsync(directory, settings, registry, row, cancellationToken).ConfigureAwait(false);

            outcomes.Add(await ActAsync(directory, row.Name, served, freeze, keepFrozen, cancellationToken).ConfigureAwait(false));
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
    /// one project learns an agent frozen for another project's sake was released there too (review, lens 2 run B on 44c8e1e6),
    /// and by that project's name for it where it has another.
    /// </remarks>
    public static string Elsewhere(FreezeAllOutcome all, string project, bool freeze)
    {
        ArgumentNullException.ThrowIfNull(all);

        return string.Concat(all.Agents
            .Select(o => (o.Agent, Others: o.Projects
                .Where(p => p.Problem is null && p.Changed && !string.Equals(p.Project, project, StringComparison.Ordinal))
                .Select(p => $"'{p.Project}'{As(o.Agent, p.Named)}")
                .ToList()))
            .Where(o => o.Others.Count > 0)
            .Select(o => $" '{o.Agent.Value}' was {(freeze ? "frozen" : "unfrozen")} in {string.Join(", ", o.Others)} too."));
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
            ? $"{p.Project}{As(outcome.Agent, p.Named)}: not done. {problem}"
            : $"{p.Project}{As(outcome.Agent, p.Named)}: {(p.Changed ? "changed" : "already so")}{ProcessWords(p.Process)}.");

        return string.Join(' ', lines);
    }

    // The agent's name in a project, said only where it differs from the name the person acted on.
    private static string As(AgentName acted, AgentName? named) =>
        named is { } there && there != acted ? $" (as '{there.Value}')" : string.Empty;

    private static string ProcessWords(FreezeProcess? process) => process switch
    {
        FreezeProcess.Stopping => "; its node was asked to stop it once its turn ends",
        FreezeProcess.OpenOutside => "; it is open outside Parley, so its process stays until somebody closes it, but nothing will start it again",
        FreezeProcess.LeftRunning => "; a node that is gone left its process running, so it runs until you end it with Kill, but nothing will start it again",
        FreezeProcess.NoNode => "; no node is joined to stop it, so the freeze stands when one returns",
        _ => string.Empty
    };
}
