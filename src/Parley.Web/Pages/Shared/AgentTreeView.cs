using Parley.Core;

namespace Parley.Web.Pages.Shared;

/// <summary>One agent in the tree of agents.</summary>
/// <param name="Name">Who it is.</param>
/// <param name="InIt">Whether it is a party to the conversation being shown; true everywhere there is none.</param>
/// <param name="Ticked">Whether its box is ticked. Only meaningful when the tree is picking.</param>
/// <param name="State">What it is doing, in words.</param>
/// <param name="Messages">How many messages in the conversation being shown were sent by it or to it; null where there is none.</param>
public sealed record AgentTreeEntry(AgentName Name, bool InIt, bool Ticked, string State, int? Messages = null);

/// <summary>What the shared tree of agents is drawn from.</summary>
/// <remarks>
/// One tree for every page that lists agents, so they cannot draw the list two ways. Grouped by machine with the
/// person's own group first, named by role alone since the group says the machine.
/// </remarks>
/// <param name="Project">The project, for the link into each agent.</param>
/// <param name="Entries">The agents.</param>
/// <param name="Pick">Whether each carries a box belonging to the composer's form.</param>
/// <param name="ForDefaults">
/// Whether each carries a box that belongs to no form, for choosing the agents a new conversation is addressed to on the
/// settings page. The names are then plain text inside the box's label, so clicking one ticks it and does not navigate.
/// </param>
public sealed record AgentTreeView(
    string Project, IReadOnlyList<AgentTreeEntry> Entries, bool Pick, bool ForDefaults = false);
