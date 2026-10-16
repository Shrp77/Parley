using System.Globalization;
using Parley.Core;
using Wire = Parley.Transport;

namespace Parley.Cli;

/// <summary>
/// The one operator operation the command line still performs, rendered for a terminal.
/// </summary>
/// <remarks>
/// <para>
/// Separated from <see cref="Program"/> so the behaviour can be tested without a process. What
/// is left in the entry point is argument shape and exit codes; what is here is what the command
/// does and what it prints, which is the part worth pinning.
/// </para>
/// <para>
/// It writes to a supplied writer rather than to the console for the same reason.
/// </para>
/// </remarks>
public sealed class OperatorCommands
{
    private readonly Wire.Operators.OperatorsClient _console;
    private readonly TextWriter _output;

    /// <summary>Creates the commands over an open database.</summary>
    /// <param name="console">The operator console to drive.</param>
    /// <param name="output">Where to write.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public OperatorCommands(Wire.Operators.OperatorsClient console, TextWriter output)
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
    public async Task<int> RegistryAsync(CancellationToken cancellationToken)
    {
        // Read through `BrokerClient` rather than off the wire directly, which is what puts the pending
        // mark here: the rule for "accepted but not yet live" is a comparison of two observations
        // (`AgentRecord.OwnsPending`), and spelling that comparison a second time here is how the three
        // surfaces come to disagree about the state of one agent. Every surface showing the line says
        // when an accepted line is not yet live, and this is the listing somebody runs to check that
        // an acceptance worked.
        // Parley is left out: it is the project's own participant rather than an agent, owns nothing and is started by
        // nothing, so a row for it here would read as an agent nobody can reach (SPEC.md §14). Every store holds it, so
        // counting it would also make "no agents registered" impossible to say.
        var agents = (await new Parley.Client.BrokerClient(_console)
            .RegistryAsync(cancellationToken)
            .ConfigureAwait(false))
            .Where(a => !a.Facilitator)
            .ToList();

        // The person seat is listed (every listing shows the person) but is not an agent, so it does not count
        // towards "no agents registered" — every store holds one from the moment it is served, and counting it
        // would make that sentence impossible to say, exactly as counting Parley would.
        if (!agents.Any(a => !PersonSeat.Is(a.Name)))
        {
            await _output.WriteLineAsync(
                "No agents registered. One appears the first time its server starts.")
                .ConfigureAwait(false);
        }

        foreach (var agent in agents.OrderBy(a => a.Name.Value, StringComparer.Ordinal))
        {
            await _output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                // Two columns rather than one, because they answer different questions and an
                // operator needs both. Status is a judgement somebody recorded -- active, or
                // retired. Presence is what the connections say right now, and nothing about it
                // gates routing: an agent that is `startable` is simply one nobody has open,
                // which is where most agents are most of the time.
                $"{agent.Name,-20} {Lower(agent.Status),-9} {Lower(agent.Presence),-10} " +
                $"{Lower(agent.Tier),-9} {agent.Adapter,-12} " +
                $"{agent.Workspace}")).ConfigureAwait(false);

            // The statement its peers route by, on its own line because it is prose rather than
            // a column. Its absence is printed rather than skipped: the field is optional and
            // the feature is inert without it, so this listing is where the person who can set
            // it finds out that nobody has. That is the whole of the nudge — a note on every
            // start-up would land in whichever log the client keeps for a server that is
            // misbehaving, which nobody reads while nothing is wrong.
            //
            // The mark is `Roster.PendingMark`, which is the one sentence the other two surfaces
            // append, rather than a wording of its own: three spellings of one state is three
            // chances for a reader to think they mean different things.
            //
            // Not for the person, who owns no repository and whom storage refuses a line: telling somebody
            // to set PARLEY_OWNS for themselves would send them to do something that cannot be done.
            if (PersonSeat.Is(agent.Name))
            {
                continue;
            }

            await _output.WriteLineAsync(
                $"  owns: {(agent.Owns is { Length: > 0 } owns ? owns : $"(unset — set {DeploymentEnvironment.OwnsVariable} where that agent's server is launched)")}"
                + Parley.Client.Rendering.Roster.PendingMark(agent))
                .ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>Renders an enum member the way the registry stores it.</summary>
    /// <param name="value">The member.</param>
    /// <returns>Its lowercase name.</returns>
    private static string Lower(object value) =>
        value.ToString()!.ToLowerInvariant();
}
