using Parley.Client;
using Parley.Client.Rendering;
using Parley.Core;
using Spectre.Console;

namespace Parley.Cli.Top;

/// <summary>
/// Who is registered in a project, and whether each is there.
/// </summary>
/// <remarks>
/// <para>
/// The state column comes from <see cref="Roster.State"/> rather than from a word chosen here.
/// Two surfaces deriving presence separately is how they come to disagree about who was available
/// to ask, which SC-003 forbids — and the shared derivation is most of the reason
/// <c>Parley.Client</c> exists at all.
/// </para>
/// <para>
/// <b>Startable is not absent.</b> An agent nobody is running, on a machine whose node is
/// connected, can be started by that node when a question arrives; an agent whose machine is
/// unreachable cannot. Both look like "not here" and only one of them means a peer cannot be
/// asked, so the words are kept apart.
/// </para>
/// </remarks>
internal static class AgentsView
{
    /// <summary>Reads the registry and draws it.</summary>
    /// <param name="console">Where to draw.</param>
    /// <param name="directory">The clients.</param>
    /// <param name="project">The project to read.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>When it has been drawn.</returns>
    internal static async Task ShowAsync(
        IAnsiConsole console,
        BrokerDirectory directory,
        ProjectSettings project,
        CancellationToken cancellationToken)
    {
        var read = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(directory.ClientFor(project))
                .RegistryAsync(cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        if (!read.Reachable)
        {
            console.MarkupLine($"[red]{Safe.Text(read.Problem!)}[/]");

            return;
        }

        var agents = read.Value ?? [];

        if (agents.Count == 0)
        {
            console.MarkupLine(
                "[yellow]No agent is registered in this project.[/] A repository registers "
                + "itself the first time its adapter attaches.");

            return;
        }

        console.Write(Table(agents));
    }

    /// <summary>The registry as a table.</summary>
    /// <param name="agents">What the broker said.</param>
    /// <returns>The table.</returns>
    internal static Table Table(IReadOnlyList<AgentRecord> agents)
    {
        var table = new Table().Border(TableBorder.Rounded);

        table.AddColumn("agent");
        table.AddColumn("state");
        table.AddColumn("tier");
        table.AddColumn("owns");
        table.AddColumn("workspace");

        foreach (var agent in agents.OrderBy(a => a.Name.Value, StringComparer.Ordinal))
        {
            table.AddRow(
                Safe.Text(agent.Name.Value),
                State(agent),
                Safe.Text(agent.Tier.ToString().ToLowerInvariant()),
                Owns(agent),
                $"[dim]{Safe.Text(agent.Workspace ?? "—")}[/]");
        }

        return table;
    }

    /// <summary>An agent's state, as a word first and a colour second.</summary>
    /// <param name="agent">The agent, as the registry described it.</param>
    /// <returns>Markup carrying the state in text.</returns>
    /// <remarks>
    /// The word is <see cref="Roster.State"/>'s and the colour only repeats it, so nothing here
    /// is conveyed by colour alone (FR-005, SC-007). A reader on a monochrome terminal, or one
    /// who cannot distinguish the colours, reads exactly the same facts.
    /// </remarks>
    internal static string State(AgentRecord agent)
    {
        var state = Roster.State(agent);

        var colour = state switch
        {
            "running" => "green",
            "startable" => "yellow",
            "retired" => "dim",
            _ => "red",
        };

        return $"[{colour}]{Safe.Text(state)}[/]";
    }

    /// <summary>What the agent says it is for.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>Its claim, or a mark saying it made none.</returns>
    /// <remarks>
    /// An absent line is a fact and never a gap to fill. It is the agent's own claim about its
    /// scope (§10.1), so substituting a description — from the workspace path, from the name,
    /// from anything — would put words in an agent's mouth in the one place a reader would take
    /// them as the agent's own.
    /// </remarks>
    internal static string Owns(AgentRecord agent) =>
        Roster.HasStatedWhatItOwns(agent)
            ? Safe.Text(agent.Owns!)
            : "[dim]not stated[/]";
}
