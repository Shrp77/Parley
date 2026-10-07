using Parley.Core;

namespace Parley.Client.Rendering;

/// <summary>
/// Which of a project's agents a conversation's room offers beside the ones that have taken part (SPEC.md §5.0).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every agent of the project is in the room, and the ones that have spoken are marked apart.</b> A person
/// replying in one conversation should be able to bring somebody else in without leaving it, so the room lists the
/// whole project; ticking an agent who has not taken part and sending addresses them, which is what addressing
/// them already does — it opens a request and pulls them in. Being listed is not being able to read the
/// conversation: §10.1 gives an agent no history operation, so one brought in later knows only what its own request
/// says.
/// </para>
/// <para>
/// <b>One rule for both surfaces</b>, so the terminal and the web console cannot disagree about who is offered. It
/// replaces a copy that lived in the terminal and said nothing to the web page, which listed only those who had
/// spoken.
/// </para>
/// <para>
/// <b>Participants come from the transcript</b>, never the registry (§5.0): a participant is anyone party to an
/// exchange in the conversation, and may since have been retired or purged. This type does not list them; it lists
/// the others.
/// </para>
/// </remarks>
public static class RoomRoster
{
    /// <summary>The mark in front of an agent that has taken part in the conversation. A character, not a colour.</summary>
    public const string ParticipantMark = "◈";

    /// <summary>What <see cref="ParticipantMark"/> means, said once beside it.</summary>
    public const string ParticipantLegend = "◈ has taken part";

    /// <summary>
    /// The project's agents that are not in the conversation, for the room to offer.
    /// </summary>
    /// <param name="registry">The registry as read, or <see langword="null"/> when it could not be.</param>
    /// <param name="participants">Everyone party to the conversation.</param>
    /// <param name="me">The person, when they are known, who is never offered to themselves.</param>
    /// <returns>
    /// Active agents that are neither participants, nor the person, nor a person's seat, nor Parley, by name.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Retired agents are not offered</b>: nothing routes to them (§14), so a line addressed to one would be
    /// refused. One that took part stays a participant and is not listed here.
    /// </para>
    /// <para>
    /// A registry that could not be read yields nobody rather than a guess: offering a name nothing vouches for
    /// would put a recipient on a message the broker may refuse.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<AgentName> NotInIt(
        IReadOnlyList<AgentRecord>? registry,
        IEnumerable<AgentName> participants,
        AgentName? me)
    {
        ArgumentNullException.ThrowIfNull(participants);

        var inIt = participants.ToHashSet();

        return
        [
            .. (registry ?? [])
                .Where(agent => agent.Status == AgentStatus.Active
                    && agent.Name != me
                    && !Facilitation.Is(agent.Name)
                    && !PersonSeat.Is(agent.Name)
                    && !string.Equals(agent.Adapter, PersonSeat.Adapter, StringComparison.Ordinal)
                    && !inIt.Contains(agent.Name))
                .Select(agent => agent.Name)
                .OrderBy(name => name.Value, StringComparer.Ordinal),
        ];
    }
}
