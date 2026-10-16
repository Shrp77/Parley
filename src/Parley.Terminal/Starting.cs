using Parley.Client;
using Parley.Core;

namespace Parley.Terminal;

/// <summary>What became of starting one agent.</summary>
/// <param name="Registered">Whether the registry held it afterwards.</param>
/// <param name="Launched">Whether the node said it started anything at all.</param>
/// <param name="Says">What to tell the person, in a few words.</param>
/// <remarks>
/// <b>Two flags rather than one, because "it did not start" and "it started and I did not see it
/// register" are different facts and lead somewhere different.</b> A broker that could not be reached must
/// never be reported under a sentence beginning <i>Started</i>.
/// </remarks>
internal sealed record Started(bool Registered, bool Launched, string Says);

/// <summary>
/// Starting agents so that they register themselves, and saying what actually happened.
/// </summary>
/// <remarks>
/// <para>
/// <b>Enrolment is not finished until the agent has attached once</b>, and until then §14 counts
/// its name unroutable and refuses every message type to it. So "added" and "addressable" are two
/// different facts, and the surface <em>looks in the registry</em> before it says anything: the node
/// reports only that it launched something, and the registry is the only thing that knows whether the
/// adapter attached, reached this broker and registered.
/// </para>
/// <para>
/// <b>It does the set, not just the one.</b> An agent that never registered cannot be reached by any
/// message, so a set of them cannot be fixed by asking them anything.
/// </para>
/// <para>
/// <b>A timeout says "I do not know", never "it did not happen"</b> — the rule <c>Registering.NotSeen</c>
/// is worded from. An agent that registers a second after this stopped looking is working; a report that
/// called that a failure would send somebody to fix it.
/// </para>
/// </remarks>
internal static class Starting
{
    /// <summary>How often to look in the registry while waiting.</summary>
    private static readonly TimeSpan Glance = TimeSpan.FromMilliseconds(500);

    /// <summary>How long to keep looking for a started agent in the registry.</summary>
    /// <remarks>
    /// <c>Registering.Patience</c>'s figure and its reasoning: a cold start is a process launching,
    /// a server connecting and a stream being accepted, which thirty seconds is generous for — and
    /// short enough that a set of repositories in a row is not an afternoon. The two surfaces wait
    /// the same length because it is one claim about how long a start takes.
    /// </remarks>
    internal static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>How long to keep asking a node that does not know an agent it was just handed.</summary>
    /// <remarks>
    /// <c>Registering.CatchUp</c>'s figure and its reasoning: the catalogue is written and the start asked
    /// in the same breath, while the node reads its catalogue on a poll, so the first answer is reliably
    /// "not something I can start". One claim about how long a node takes to notice, so the same length.
    /// </remarks>
    internal static readonly TimeSpan CatchUp = TimeSpan.FromSeconds(15);

    /// <summary>What is said when the start took and the registration followed.</summary>
    internal const string Registered = "registered";

    /// <summary>What is said when the start took and nothing had registered in time.</summary>
    /// <remarks>
    /// <c>Registering.NotSeen</c>'s sentence, in this surface's own words because it is reported
    /// per row here rather than as a line of a command's output. The claim is deliberately weaker
    /// than "it failed": nothing here knows that.
    /// </remarks>
    internal const string NotSeen = "started, but had not registered before this stopped looking";

    /// <summary>What is said when the node would not start it.</summary>
    internal const string NotStarted = "this machine's node did not start it";

    /// <summary>Asks for a start and then looks to see whether the agent registered.</summary>
    /// <param name="agent">Who to start.</param>
    /// <param name="start">Asks the broker, and says whether the node did anything.</param>
    /// <param name="registered">Says whether the registry holds the agent now.</param>
    /// <param name="patience">How long to keep looking. Zero looks exactly once.</param>
    /// <param name="clock">Measures the wait, so a test does not have to spend it.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <param name="catchUp">
    /// How long to keep asking a node that says it cannot start the agent. Zero asks once. A caller
    /// that has just written the agent into the catalogue passes <see cref="CatchUp"/>, because the node
    /// has not read it yet.
    /// </param>
    /// <returns>Whether it registered, and what to say about it.</returns>
    /// <remarks>
    /// Polled rather than awaited, for the reason <c>Registering.WaitAsync</c> gives: registration
    /// happens inside the broker when a stream is accepted, and no operator operation reports it.
    /// </remarks>
    internal static async Task<Started> OnceAsync(
        AgentName agent,
        Func<AgentName, CancellationToken, Task<ProjectRead<bool>>> start,
        Func<AgentName, CancellationToken, Task<ProjectRead<bool>>> registered,
        TimeSpan patience,
        TimeProvider clock,
        CancellationToken cancellationToken,
        TimeSpan catchUp = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(registered);
        ArgumentNullException.ThrowIfNull(clock);

        var giveUp = clock.GetUtcNow() + catchUp;

        while (true)
        {
            var asked = await start(agent, cancellationToken).ConfigureAwait(false);

            if (!asked.Reachable)
            {
                return new Started(false, false, asked.Problem ?? NotStarted);
            }

            if (asked.Value)
            {
                break;
            }

            if (clock.GetUtcNow() >= giveUp)
            {
                return new Started(false, false, NotStarted);
            }

            await Task.Delay(Glance, cancellationToken).ConfigureAwait(false);
        }

        var deadline = clock.GetUtcNow() + patience;

        while (true)
        {
            var seen = await registered(agent, cancellationToken).ConfigureAwait(false);

            if (!seen.Reachable)
            {
                // The broker answered the start and has stopped answering. Unknown rather than
                // failed, for the same reason the timeout is.
                return new Started(false, true, seen.Problem ?? NotSeen);
            }

            if (seen.Value)
            {
                return new Started(true, true, Registered);
            }

            if (clock.GetUtcNow() >= deadline)
            {
                return new Started(false, true, NotSeen);
            }

            await Task.Delay(Glance, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Starts every agent given, and then waits once for the set.</summary>
    /// <param name="agents">Who to start, in the order the roster shows them.</param>
    /// <param name="start">Asks the broker, and says whether the node did anything.</param>
    /// <param name="registered">Says whether the registry holds an agent now.</param>
    /// <param name="patience">How long to keep looking, for the set. Zero looks exactly once.</param>
    /// <param name="clock">Measures the wait.</param>
    /// <param name="saying">Told what is happening while it happens, or null.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    /// <returns>One line, in the shape a broadcast's report has.</returns>
    /// <remarks>
    /// <para>
    /// <b>Every start first, then one wait for all of them — not a wait each.</b> Thirty seconds
    /// per agent is nine and a half minutes for nineteen, spent by a surface with a serialised work
    /// queue: the screen would be alive and every other action behind it would not. The starts are
    /// concurrent in effect because they all begin before anything is waited on, so the window is
    /// one window however many agents there are.
    /// </para>
    /// <para>
    /// <b>The launches are still one after another.</b> Each is a process arriving on this machine,
    /// and nineteen launched inside a millisecond of each other is nineteen editors' worth of
    /// memory landing together on the machine somebody is working at.
    /// </para>
    /// <para>
    /// <b>Grouped by what happened, and the names abbreviated</b>, the shape <c>SeatVoice</c>'s broadcast
    /// report has: counts first, reason next, names last.
    /// </para>
    /// </remarks>
    internal static async Task<string> EveryAsync(
        IReadOnlyList<AgentName> agents,
        Func<AgentName, CancellationToken, Task<ProjectRead<bool>>> start,
        Func<AgentName, CancellationToken, Task<ProjectRead<bool>>> registered,
        TimeSpan patience,
        TimeProvider clock,
        Action<string>? saying,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(registered);
        ArgumentNullException.ThrowIfNull(clock);

        if (agents.Count == 0)
        {
            return Nobody;
        }

        var outcomes = new List<(AgentName Agent, Started Was)>();
        var launched = new List<AgentName>();
        var at = 0;

        foreach (var agent in agents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            saying?.Invoke($"Starting {++at} of {agents.Count}: {agent.Value}…");

            var asked = await start(agent, cancellationToken).ConfigureAwait(false);

            if (!asked.Reachable)
            {
                outcomes.Add((agent, new Started(false, false, asked.Problem ?? NotStarted)));
            }
            else if (!asked.Value)
            {
                outcomes.Add((agent, new Started(false, false, NotStarted)));
            }
            else
            {
                launched.Add(agent);
            }
        }

        if (launched.Count > 0)
        {
            saying?.Invoke(
                $"Started {launched.Count}. Waiting for them to register…");

            var deadline = clock.GetUtcNow() + patience;

            while (launched.Count > 0)
            {
                foreach (var agent in launched.ToList())
                {
                    var seen = await registered(agent, cancellationToken).ConfigureAwait(false);

                    if (!seen.Reachable)
                    {
                        outcomes.Add((agent, new Started(false, true, seen.Problem ?? NotSeen)));
                        _ = launched.Remove(agent);
                    }
                    else if (seen.Value)
                    {
                        outcomes.Add((agent, new Started(true, true, Registered)));
                        _ = launched.Remove(agent);
                    }
                }

                if (launched.Count == 0 || clock.GetUtcNow() >= deadline)
                {
                    break;
                }

                await Task.Delay(Glance, cancellationToken).ConfigureAwait(false);
            }

            // Whatever is still out is unknown rather than failed, which is what `NotSeen` says.
            outcomes.AddRange(launched.Select(agent => (agent, new Started(false, true, NotSeen))));
        }

        var registeredNow = outcomes.Count(outcome => outcome.Was.Registered);
        var said = new System.Text.StringBuilder();

        _ = said.Append("Registered ").Append(registeredNow).Append(" of ").Append(agents.Count)
            .Append('.');

        foreach (var group in outcomes
                     .Where(outcome => !outcome.Was.Registered)
                     .GroupBy(outcome => outcome.Was.Says, StringComparer.Ordinal)
                     .OrderByDescending(group => group.Count()))
        {
            _ = said.Append(' ').Append(group.Count()).Append(": ")
                .Append(Safe.Text(group.Key))
                .Append(" (")
                .Append(Some([.. group.Select(outcome => outcome.Agent.Value)]))
                .Append(").");
        }

        return said.ToString();
    }

    /// <summary>What is said when every agent in the project has already registered.</summary>
    internal const string Nobody =
        "Every agent here has registered at least once, so there is nothing to start.";

    /// <summary>How many names to print before counting the rest.</summary>
    /// <remarks>
    /// Four, as the broadcast report settled on, and for the same reason: a status line shows one
    /// line, and nineteen names is a line that runs off the end of it.
    /// </remarks>
    private const int Named = 4;

    /// <summary>Some of a list of names, and a count of whatever is left.</summary>
    /// <param name="names">The names.</param>
    /// <returns>The phrase.</returns>
    internal static string Some(IReadOnlyList<string> names) =>
        names.Count <= Named
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(Named)) + $" and {names.Count - Named} more";
}
