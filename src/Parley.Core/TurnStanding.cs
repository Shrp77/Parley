namespace Parley.Core;

/// <summary>What an agent's node has said about the turns it gives it (SPEC.md §11.6).</summary>
/// <param name="Reported">
/// Whether anybody reports this agent's turns at all. False for an agent whose node predates §11.6, and for one
/// attached from somebody's editor: such an agent is unknown, never stopped.
/// </param>
/// <param name="RunningSince">When the turn running now began, or null when none is.</param>
/// <param name="LastEnded">When the most recent turn ended, or null before the first.</param>
/// <param name="LastClean">Whether that turn finished cleanly.</param>
/// <param name="LastPermissionDenials">How many tool calls that turn had refused for want of permission.</param>
/// <remarks>
/// <para>
/// <b>Why it exists</b> (specs/024): an agent tried a command nobody was there to approve, its turn ended without a word,
/// and the room said <i>working</i> for nearly three hours — because "picked up and not answered" was all a surface could
/// see, and it is what an agent mid-task and an agent that has stopped both look like. The node knew the difference and
/// told nobody.
/// </para>
/// <para>
/// One record rather than five fields on <see cref="AgentRecord"/>, because they are one fact about the agent and travel
/// or do not travel together. Operator-only: the agent roster carries none of it (§10.1).
/// </para>
/// </remarks>
public sealed record TurnStanding(
    bool Reported,
    DateTimeOffset? RunningSince,
    DateTimeOffset? LastEnded,
    bool LastClean,
    int LastPermissionDenials)
{
    /// <summary>Nobody reports this agent's turns.</summary>
    public static TurnStanding Unreported { get; } = new(false, null, null, false, 0);
}
