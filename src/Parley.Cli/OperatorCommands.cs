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

    /// <summary>Renders an enum member the way the registry stores it.</summary>
    /// <param name="value">The member.</param>
    /// <returns>Its lowercase name.</returns>
    private static string Lower(object value) =>
        value.ToString()!.ToLowerInvariant();
}
