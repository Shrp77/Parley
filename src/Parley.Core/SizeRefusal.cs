namespace Parley.Core;

/// <summary>
/// What the size cap of SPEC.md rule 8.5 refused, for one agent and one message type.
/// </summary>
/// <param name="Agent">The agent whose message was refused.</param>
/// <param name="Type">The message type it was trying to send.</param>
/// <param name="Refusals">How many times.</param>
/// <param name="Worst">The longest payload refused, in characters.</param>
/// <param name="Total">
/// Every refused payload's length added together. Sixty-four bit, and not defensively: a
/// thirty-two bit total passes two billion after roughly fifty thousand refusals at forty
/// thousand characters, which one looping agent produces in an afternoon. Past that the read
/// threw an overflow and the operator's command stopped working for that deployment
/// permanently, with no way back short of editing the database — for a feature whose whole
/// purpose is to still be readable years later. Found by probing rather than by reading.
/// </param>
/// <param name="Cap">The cap in force at the most recent refusal.</param>
/// <param name="LastUtc">When the most recent one happened.</param>
/// <remarks>
/// <see cref="Total"/> is here so an operator can see the mean rather than only the worst. A
/// single refusal at forty thousand characters and forty at four thousand and one are different
/// problems: the first is somebody trying to send a file, the second is a cap set slightly too
/// low, and only the second is an argument for changing the number.
/// </remarks>
public sealed record SizeRefusal(
    AgentName Agent,
    MessageType Type,
    int Refusals,
    int Worst,
    long Total,
    int Cap,
    DateTimeOffset LastUtc);
