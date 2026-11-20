using Parley.Core;

namespace Parley.Client;

/// <summary>What holding or resuming an agent did in one project.</summary>
/// <param name="Project">The project.</param>
/// <param name="Changed">Whether its state there changed: it was not held before (a hold), or it was held (a resume).</param>
/// <param name="Process">What became of its process, for a hold; null for a resume or when the project could not be reached.</param>
/// <param name="Problem">Why it could not be done there, or null when it could.</param>
public sealed record HeldIn(string Project, bool Changed, HoldProcess? Process, string? Problem);

/// <summary>What holding or resuming one agent did across every project that serves it.</summary>
/// <param name="Agent">The agent.</param>
/// <param name="Projects">One entry per project.</param>
public sealed record HoldOutcome(AgentName Agent, IReadOnlyList<HeldIn> Projects)
{
    /// <summary>Whether it worked in every project that serves the agent.</summary>
    public bool Complete => Projects.Count > 0 && Projects.All(p => p.Problem is null);

    /// <summary>Whether anything changed anywhere.</summary>
    public bool AnyChanged => Projects.Any(p => p.Changed);
}

/// <summary>
/// Holds and resumes an agent in every project that serves it, so one act holds the agent whichever project's work would wake it
/// (SPEC.md §11.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>The surface loops, not the broker.</b> A broker holds one project and a store has one broker, so what is the same agent in
/// several projects is held by asking each project's broker. The agent is <c>role@machine</c> in each, and the one process serves
/// them all (SPEC.md §11.6): holding it in one project only would end the process and let the next project's work start it again.
/// </para>
/// <para>
/// A project that cannot be reached is reported, never skipped: the agent is not held there, and the person is told so that they
/// can hold it when it is back.
/// </para>
/// <para>
/// Shared by the web console and the terminal, as <c>AgentWithdrawal</c> is, so neither calls the broker's operation itself.
/// </para>
/// </remarks>
public static class AgentHolding
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

    /// <summary>Holds an agent in every project that serves it.</summary>
    /// <param name="directory">The projects and their brokers.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="cancellationToken">Cancels the acts.</param>
    /// <returns>What happened in each project.</returns>
    public static Task<HoldOutcome> HoldAsync(
        BrokerDirectory directory, AgentName agent, CancellationToken cancellationToken = default) =>
        ActAsync(directory, agent, hold: true, cancellationToken);

    /// <summary>Resumes an agent in every project where it is held.</summary>
    /// <param name="directory">The projects and their brokers.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="cancellationToken">Cancels the acts.</param>
    /// <returns>What happened in each project.</returns>
    public static Task<HoldOutcome> ResumeAsync(
        BrokerDirectory directory, AgentName agent, CancellationToken cancellationToken = default) =>
        ActAsync(directory, agent, hold: false, cancellationToken);

    private static async Task<HoldOutcome> ActAsync(
        BrokerDirectory directory, AgentName agent, bool hold, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var (serving, unreachable) = await ServedByAsync(directory, agent, cancellationToken).ConfigureAwait(false);
        var results = new List<HeldIn>();

        foreach (var (project, problem) in unreachable)
        {
            results.Add(new HeldIn(project, false, null, problem));
        }

        foreach (var name in serving)
        {
            var broker = new BrokerClient(directory.ClientFor(directory.Find(name)!));

            var done = await BrokerDirectory.ReadAsync(async () => hold
                ? await broker.HoldAsync(agent, cancellationToken).ConfigureAwait(false)
                : new HoldResult(await broker.ResumeAsync(agent, cancellationToken).ConfigureAwait(false), HoldProcess.NotRunning))
                .ConfigureAwait(false);

            results.Add(done.Value is { } result
                ? new HeldIn(name, result.Changed, hold ? result.Process : null, null)
                : new HeldIn(name, false, null, done.Problem));
        }

        return new HoldOutcome(agent, [.. results.OrderBy(r => r.Project, StringComparer.Ordinal)]);
    }

    /// <summary>Holds every agent the project lists, in every project that serves each.</summary>
    /// <param name="directory">The projects and their brokers.</param>
    /// <param name="project">The project whose agents they are.</param>
    /// <param name="hold"><see langword="true"/> to hold them, <see langword="false"/> to resume them.</param>
    /// <param name="cancellationToken">Cancels the acts.</param>
    /// <returns>One outcome per agent, or none when the project is unknown or could not be read.</returns>
    /// <remarks>
    /// The person's seat and the facilitator are left out: neither is started by a node, and storage refuses to hold either.
    /// A retired agent is left out too, since nothing wakes it.
    /// </remarks>
    public static async Task<IReadOnlyList<HoldOutcome>> AllAsync(
        BrokerDirectory directory, string project, bool hold, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var settings = directory.Find(project);

        if (settings is null)
        {
            return [];
        }

        var read = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(directory.ClientFor(settings)).RegistryAsync(cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        var outcomes = new List<HoldOutcome>();

        foreach (var agent in (read.Value ?? [])
            .Where(a => a.Status == AgentStatus.Active && !a.Facilitator && !PersonSeat.Is(a.Name))
            .Where(a => hold ? !a.Held : a.Held)
            .Select(a => a.Name))
        {
            outcomes.Add(await ActAsync(directory, agent, hold, cancellationToken).ConfigureAwait(false));
        }

        return outcomes;
    }

    /// <summary>Says what holding did, in a few sentences a person can act on.</summary>
    /// <param name="outcome">What happened.</param>
    /// <returns>The sentences.</returns>
    public static string Describe(HoldOutcome outcome)
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

    private static string ProcessWords(HoldProcess? process) => process switch
    {
        HoldProcess.Stopping => "; its node was asked to stop it once its turn ends",
        HoldProcess.OpenOutside => "; it is open outside Parley, so its process stays until somebody closes it, but nothing will start it again",
        HoldProcess.NoNode => "; no node is joined to stop it, so the hold stands when one returns",
        _ => string.Empty
    };
}
