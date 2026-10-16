namespace Parley.Core;

/// <summary>What an agent's node has said about the turns it gives it (SPEC.md §11.6).</summary>
/// <param name="Reported">
/// Whether anybody reports this agent's turns at all. False for an agent attached from somebody's editor, which no
/// node gives a turn: such an agent is unknown, never stopped.
/// </param>
/// <param name="RunningSince">When the turn running now began, or null when none is.</param>
/// <param name="LastEnded">When the most recent turn ended, or null before the first.</param>
/// <param name="LastClean">Whether that turn finished cleanly.</param>
/// <param name="LastPermissionDenials">How many tool calls that turn had refused for want of permission.</param>
/// <remarks>
/// <para>
/// <b>Why it exists</b>: an agent that tried a command nobody was there to approve ends its turn without a
/// word, and "picked up and not answered" is all a surface could otherwise see, which an agent mid-task and an
/// agent that has stopped both look like. The node knows the difference, so it says.
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
