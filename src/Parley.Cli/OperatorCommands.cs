using System.Globalization;
using System.Text;
using Parley.Broker;
using Parley.Core;

namespace Parley.Cli;

/// <summary>
/// The operator operations of SPEC.md §10.2, rendered for a terminal.
/// </summary>
/// <remarks>
/// <para>
/// Separated from <see cref="Program"/> so the behaviour can be tested without a process. What
/// is left in the entry point is argument shape and exit codes; what is here is what each
/// command does and what it prints, which is the part worth pinning.
/// </para>
/// <para>
/// Every command writes to a supplied writer rather than to the console for the same reason.
/// </para>
/// </remarks>
public sealed class OperatorCommands
{
    private readonly OperatorConsole _console;
    private readonly TextWriter _output;

    /// <summary>Creates the commands over an open database.</summary>
    /// <param name="console">The operator console to drive.</param>
    /// <param name="output">Where to write.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public OperatorCommands(OperatorConsole console, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(output);

        _console = console;
        _output = output;
    }

    /// <summary>Lists every registered agent, whole.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Zero.</returns>
    /// <remarks>
    /// Shows the workspace, which the agent-facing listing deliberately does not (§10.1 rule 3).
    /// An operator is looking at their own deployment; a peer is not.
    /// </remarks>
    public async Task<int> AgentsAsync(CancellationToken cancellationToken)
    {
        var agents = await _console.DescribeAgentsAsync(cancellationToken).ConfigureAwait(false);

        if (agents.Count == 0)
        {
            await _output.WriteLineAsync(
                "No agents registered. One appears the first time its server starts.")
                .ConfigureAwait(false);

            return 0;
        }

        foreach (var agent in agents.OrderBy(a => a.Name.Value, StringComparer.Ordinal))
        {
            await _output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"{agent.Name.Value,-20} {Lower(agent.Status),-12} {Lower(agent.Tier),-9} " +
                $"{agent.Adapter,-12} {agent.Workspace ?? ""}")).ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>Withdraws an agent's registration.</summary>
    /// <param name="agent">The agent to retire.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Zero if the registry changed, one if it did not.</returns>
    /// <remarks>
    /// Reports what already-retired means rather than treating it as failure, because running
    /// the command twice is not a mistake worth an error — but it exits non-zero so a script
    /// that expected to change something can tell.
    /// </remarks>
    public async Task<int> RetireAsync(AgentName agent, CancellationToken cancellationToken)
    {
        if (!await _console.RetireAsync(agent, cancellationToken).ConfigureAwait(false))
        {
            await _output.WriteLineAsync(
                $"'{agent.Value}' is not registered, or was already retired.").ConfigureAwait(false);

            return 1;
        }

        await _output.WriteLineAsync(
            $"'{agent.Value}' is retired. It will not be routed to or listed, and starting its " +
            "server will be refused. What it already owed is not resolved here: those exchanges " +
            "run out of time on the ordinary schedule and each asker is told nobody answered.")
            .ConfigureAwait(false);

        return 0;
    }

    /// <summary>Withdraws a retirement.</summary>
    /// <param name="agent">The agent to reinstate.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Zero if the registry changed, one if it did not.</returns>
    public async Task<int> ReinstateAsync(AgentName agent, CancellationToken cancellationToken)
    {
        if (!await _console.ReinstateAsync(agent, cancellationToken).ConfigureAwait(false))
        {
            await _output.WriteLineAsync(
                $"'{agent.Value}' is not registered, or was not retired.").ConfigureAwait(false);

            return 1;
        }

        await _output.WriteLineAsync(
            $"'{agent.Value}' is reinstated. It is routable again once its server attaches.")
            .ConfigureAwait(false);

        return 0;
    }

    /// <summary>Emits every message in a conversation.</summary>
    /// <param name="conversationId">The discussion to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Zero if anything was found, one if not.</returns>
    /// <remarks>
    /// Payloads are printed as stored, not wrapped in the envelope of §13. An envelope marks a
    /// peer's words as data inside another agent's context, and an operator reading their own
    /// log is not being asked to obey it — wrapping would also make the export unusable for the
    /// purpose §10.2 gives it, which is comparing how model pairings converge.
    /// </remarks>
    public async Task<int> ExportAsync(string conversationId, CancellationToken cancellationToken)
    {
        var messages = await _console.ExportAsync(conversationId, cancellationToken)
            .ConfigureAwait(false);

        if (messages.Count == 0)
        {
            await _output.WriteLineAsync(
                $"No messages in '{conversationId}'. Conversations exist only once an agent has " +
                "named one on a question or an event.").ConfigureAwait(false);

            return 1;
        }

        foreach (var message in messages)
        {
            var text = new StringBuilder()
                .Append(message.CreatedUtc.ToString("O", CultureInfo.InvariantCulture))
                .Append("  ")
                // Nine, not eight: "question" is exactly eight characters, so padding to its
                // own length runs the type into the sender. Found by reading the output.
                .Append(MessageIdentity.FormatType(message.Type).PadRight(9))
                .Append(message.From.Value)
                .Append(" -> ")
                .Append(message.To.Value)
                .Append("  (")
                .Append(message.ResponderModel)
                .Append(')');

            await _output.WriteLineAsync(text.ToString()).ConfigureAwait(false);
            await _output.WriteLineAsync("    " + message.PayloadJson).ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>Closes a conversation over its participants' votes.</summary>
    /// <param name="conversationId">The discussion to close.</param>
    /// <param name="nowUtc">The instant to record as the close.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Zero if it closed, one if it did not.</returns>
    /// <remarks>
    /// Named <c>force-conclude</c> rather than <c>conclude</c>, following §10.2. It exists
    /// because a decline blocks closure indefinitely and the agent holding it may simply be
    /// wrong — a deliberate asymmetry rather than a hole in principle 2.6: no agent may conclude
    /// over another's objection, and the person running the broker is not an agent.
    /// </remarks>
    public async Task<int> ForceConcludeAsync(
        string conversationId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        if (!await _console.ForceConcludeAsync(conversationId, nowUtc, cancellationToken)
                .ConfigureAwait(false))
        {
            await _output.WriteLineAsync(
                $"'{conversationId}' is not open, or does not exist.").ConfigureAwait(false);

            return 1;
        }

        await _output.WriteLineAsync(
            $"'{conversationId}' is closed. A new question naming it will be refused; the same " +
            "question under a new identifier will not.").ConfigureAwait(false);

        return 0;
    }

    /// <summary>Applies the retention policy of SPEC.md §9.4 now.</summary>
    /// <param name="policy">The policy to apply.</param>
    /// <param name="nowUtc">The instant to measure ages against.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>Zero.</returns>
    /// <remarks>
    /// <para>
    /// Prints the policy before the counts, and that is the load-bearing part. The numbers come
    /// from the environment, and an operator's shell is not the environment their MCP client
    /// launched the servers with — so the command that deletes text on a configured schedule
    /// must say which schedule it used. Reporting only what went would let a default policy
    /// remove a year of log from a deployment configured to keep one, silently.
    /// </para>
    /// <para>
    /// Zero even when nothing was removed, unlike <see cref="RetireAsync"/> and
    /// <see cref="ReinstateAsync"/>. Those exit non-zero when the registry did not change
    /// because a script that expected a change can then tell; here "nothing was old enough" is
    /// the ordinary steady state of a healthy deployment, and a cron job that failed most nights
    /// would train whoever reads it to ignore the one night it meant something.
    /// </para>
    /// </remarks>
    public async Task<int> PruneAsync(
        RetentionPolicy policy, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // Every value interpolated below is already a string, so nothing here formats a number
        // against a culture. The two that were numbers are converted invariantly first, which is
        // also why they are converted rather than interpolated: string.Create's handler takes a
        // single interpolated literal and refuses a concatenation, and these lines are too long
        // to be one.
        await _output.WriteLineAsync(
            $"Retention: idle after {Hours(policy.IdlePeriod)}, nothing older than " +
            $"{Hours(policy.MaximumAge)}. Set {RetentionPolicyEnvironment.IdleVariable} and " +
            $"{RetentionPolicyEnvironment.MaximumAgeVariable} to change them, in the same " +
            "environment the agents' servers are launched with.").ConfigureAwait(false);

        var result = await _console.PruneAsync(policy, nowUtc, cancellationToken)
            .ConfigureAwait(false);

        var removed = result.Removed.ToString(CultureInfo.InvariantCulture);

        await _output.WriteLineAsync($"Removed {removed} message(s).").ConfigureAwait(false);

        if (result.HeldByFloor > 0)
        {
            // Said only when it is true, because the explanation is long and an operator who
            // sees it every time stops reading it. A number that stays high across runs is the
            // signal: exchanges are sitting open, or a peer has stopped collecting its inbox.
            var held = result.HeldByFloor.ToString(CultureInfo.InvariantCulture);

            await _output.WriteLineAsync(
                $"{held} more were old enough and were kept: still undelivered, on an exchange " +
                "that has not closed, or inside the deduplication window. That floor is not " +
                "configurable — it is what keeps tightening retention from breaking delivery " +
                "or deduplication as a side effect.").ConfigureAwait(false);
        }

        if (result.Contended > 0)
        {
            // Reported apart from the floor because it means the opposite thing. The floor
            // holding traffic is worth looking into; another sweep having got there first is the
            // system working — every agent's server prunes on its own schedule, and this command
            // is not the only thing applying the policy.
            var contended = result.Contended.ToString(CultureInfo.InvariantCulture);

            await _output.WriteLineAsync(
                $"{contended} were already gone: an agent's server swept them between this " +
                "command deciding and doing. Nothing is wrong — every server applies the same " +
                "policy on its own schedule.").ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>Renders a duration as whole hours.</summary>
    /// <param name="value">The duration.</param>
    /// <returns>Text an operator can compare against what they configured.</returns>
    /// <remarks>
    /// Hours because that is the unit the variables are stated in, and a command that echoes a
    /// setting back in different units than it was written in is one more thing to convert
    /// while deciding whether it is right.
    /// </remarks>
    private static string Hours(TimeSpan value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value.TotalHours:0.##} hours");

    /// <summary>Renders an enum member the way the registry stores it.</summary>
    /// <param name="value">The member.</param>
    /// <returns>Its lowercase name.</returns>
    private static string Lower(object value) =>
        value.ToString()!.ToLowerInvariant();
}
