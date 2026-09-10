using Grpc.Core;
using Grpc.Net.Client;
using Parley.Core;
using Wire = Parley.Transport;

namespace Parley.Setup;

/// <summary>What became of trying to leave an enrolled agent registered.</summary>
/// <param name="Registered">Whether the agent was seen in the registry afterwards.</param>
/// <param name="Says">A sentence for whoever ran the command.</param>
/// <remarks>
/// Two fields rather than a sentence the caller parses, for the reason <c>Answered</c> gives: a
/// caller deciding by matching the first word of a message is a coupling that breaks silently the
/// first time somebody rewords it.
/// </remarks>
internal sealed record Registration(bool Registered, string Says);

/// <summary>
/// Starting an agent once, so that it registers itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Enrolment is not finished until the agent has attached at least once.</b> Until then §14
/// counts its name as unroutable, and <em>every</em> message type is refused — a question and a
/// notification alike, both through <c>IsUnroutableAsync</c>. So a repository that has been
/// enrolled and never opened is not a quiet agent; it is one nothing can address at all.
/// </para>
/// <para>
/// <b>Nothing here writes a registration, and nothing may.</b> SPEC.md §12.3 makes registration the
/// adapter's own act on attach and Principle III takes identity from the launch environment, so the
/// only honest way to leave an agent registered is to start it and let it register itself. What
/// this does is ask, and then look.
/// </para>
/// <para>
/// <b>It costs a process launch and no model tokens.</b> The node's start and the turn it dispatches
/// afterwards are separate calls, and only the second spends anything — the adapter attaches, and
/// <c>BrokerRuntime.AttachAsync</c> registers an agent it does not know. That is what makes doing
/// this for twenty repositories affordable.
/// </para>
/// </remarks>
internal static class Registering
{
    /// <summary>How long to wait for a started agent to appear in the registry.</summary>
    /// <remarks>
    /// A cold start is a process launching, a server connecting and a stream being accepted. Thirty
    /// seconds is generous for that and short enough that twenty repositories in a row is not an
    /// afternoon. Running out of it is <em>not</em> a failure — see <see cref="StartAsync"/>.
    /// </remarks>
    internal static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>How often to look while waiting.</summary>
    private static readonly TimeSpan Glance = TimeSpan.FromMilliseconds(500);

    /// <summary>What is said when the wait ran out.</summary>
    /// <remarks>
    /// <b>"I do not know", never "it did not happen".</b> 008 exists because a broker stopped
    /// waiting before a node had finished and then recorded the opposite of what occurred: it waited
    /// five minutes, the node waited twenty, and the real answer was refused on arrival. The same
    /// shape is available here, and a run that reported failure for an agent that registered a
    /// second later would send somebody to fix what was already working.
    /// </remarks>
    internal const string NotSeen =
        "started, but it had not registered before this stopped waiting. Check it shortly; "
        + "re-running enrolment changes nothing if it did.";

    /// <summary>Starts an agent and waits for it to register itself.</summary>
    /// <param name="endpoint">The project's broker.</param>
    /// <param name="agent">The agent to start.</param>
    /// <param name="clock">Measures the wait.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>Whether it registered, and what to say about it.</returns>
    /// <exception cref="ArgumentException">The endpoint is blank.</exception>
    internal static async Task<Registration> StartAsync(
        string endpoint,
        AgentName agent,
        TimeProvider clock,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(clock);

        using var channel = GrpcChannel.ForAddress(endpoint);
        var operators = new Wire.Operators.OperatorsClient(channel);

        try
        {
            var asked = await operators.StartAsync(
                new Wire.StartRequest { Agent = agent.Value, Because = "it was just enrolled" },
                cancellationToken: cancellationToken);

            if (!asked.Changed)
            {
                // The node holds this machine but does not name the role among what it can launch,
                // or there is no node at all. Both mean nothing here will run it, and both are worth
                // saying plainly rather than as a failure: the two files are written and correct.
                return new Registration(
                    false,
                    "written, but this machine's node did not start it. Nothing will register it "
                    + "until its repository is opened.");
            }
        }
        catch (RpcException failure)
        {
            return new Registration(false, $"written, but the broker refused the start: {failure.Status.Detail}");
        }

        return await WaitAsync(operators, agent, clock, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Looks until the agent is in the registry, or until patience runs out.</summary>
    /// <param name="operators">The broker.</param>
    /// <param name="agent">Who to look for.</param>
    /// <param name="clock">Measures the wait.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>Whether it appeared.</returns>
    /// <remarks>
    /// Polled rather than awaited on an event, because there is no event: registration happens
    /// inside the broker when a stream is accepted, and nothing on the operator surface reports it.
    /// A watcher would be a better shape and a larger change.
    /// </remarks>
    private static async Task<Registration> WaitAsync(
        Wire.Operators.OperatorsClient operators,
        AgentName agent,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var deadline = clock.GetUtcNow() + Patience;

        while (true)
        {
            try
            {
                var registry = await operators.DescribeRegistryAsync(
                    new Wire.Empty(), cancellationToken: cancellationToken);

                if (registry.Rows.Any(row =>
                    string.Equals(row.Name, agent.Value, StringComparison.Ordinal)))
                {
                    return new Registration(true, "written and registered.");
                }
            }
            catch (RpcException failure)
            {
                // The broker answered the start and has stopped answering. Reported as unknown for
                // the same reason a timeout is: the agent may well have registered.
                return new Registration(
                    false, $"started, but the broker stopped answering: {failure.Status.Detail}");
            }

            if (clock.GetUtcNow() >= deadline)
            {
                return new Registration(false, NotSeen);
            }

            await Task.Delay(Glance, cancellationToken).ConfigureAwait(false);
        }
    }
}
