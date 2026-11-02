namespace Parley.Core;

/// <summary>How long the messages in one group were (SPEC.md §10.2, <c>message_lengths</c>).</summary>
/// <param name="Name">The group: a message type, or a band of positions in a conversation.</param>
/// <param name="Messages">How many.</param>
/// <param name="Median">The middle length, in characters.</param>
/// <param name="Longest">The longest, in characters.</param>
/// <param name="OverSoftLimit">How many passed <see cref="MessageLimits.SoftPayloadCharacters"/>.</param>
public sealed record MessageLengthGroup(string Name, long Messages, int Median, int Longest, long OverSoftLimit);

/// <summary>How long one agent's messages were.</summary>
/// <param name="Agent">The agent.</param>
/// <param name="Sent">How many it sent.</param>
/// <param name="Received">How many it was sent.</param>
/// <param name="Longest">The longest it sent or was sent, in characters.</param>
/// <param name="OverSoftLimit">How many of those passed <see cref="MessageLimits.SoftPayloadCharacters"/>.</param>
public sealed record AgentMessageLengths(AgentName Agent, long Sent, long Received, int Longest, long OverSoftLimit);

/// <summary>How long messages have been, from a record that outlives them (SPEC.md §10.2).</summary>
/// <param name="Overall">Every message.</param>
/// <param name="ByType">By message type.</param>
/// <param name="ByPosition">
/// By position in the conversation, earliest first, so that a drift towards longer messages shows.
/// </param>
/// <param name="ByAgent">By agent, the longest first.</param>
/// <remarks>Lengths only. The record it is read from holds no words, so none can be returned.</remarks>
public sealed record MessageLengthReport(
    MessageLengthGroup Overall,
    IReadOnlyList<MessageLengthGroup> ByType,
    IReadOnlyList<MessageLengthGroup> ByPosition,
    IReadOnlyList<AgentMessageLengths> ByAgent);
