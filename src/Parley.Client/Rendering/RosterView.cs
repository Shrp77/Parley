using Parley.Core;

namespace Parley.Client.Rendering;

/// <summary>One agent in a roster: who it is, and what the registry says it is doing.</summary>
/// <param name="Name">The agent.</param>
/// <param name="State">
/// Its state in one word, or a sentence saying the registry does not hold it.
/// </param>
public sealed record RosterEntry(AgentName Name, string State);

/// <summary>
/// The roster column, wherever it appears.
/// </summary>
/// <param name="Project">The project it belongs to, so each name can link to its own page.</param>
/// <param name="Heading">What to call the list here.</param>
/// <param name="Entries">Who is in it.</param>
/// <param name="Problem">Why the registry could not be read, when it could not.</param>
/// <remarks>
/// <para>
/// One shape and one partial, because two copies of a roster are two rosters that drift: the
/// project's grew a link into each agent and the conversation's did not, so the same list did
/// different things depending on which page a reader had arrived from. That is the kind of
/// difference nobody decides on — it just happens, and then it is the behaviour.
/// </para>
/// <para>
/// The state is passed in rather than derived here, because the two callers know different
/// things. A project reads the registry and every row is in it; a conversation derives its
/// participants from the traffic (§5.0), so a name that spoke may have been retired or purged
/// since and the registry may hold nothing for it. Saying so is the honest answer, and inventing
/// a state would be the alternative.
/// </para>
/// </remarks>
public sealed record RosterView(
    string? Project,
    string Heading,
    IReadOnlyList<RosterEntry> Entries,
    string? Problem);
