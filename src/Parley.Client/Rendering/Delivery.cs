using Parley.Core;

namespace Parley.Client.Rendering;

/// <summary>
/// Whether a message has reached the agent it was addressed to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Whether a message was collected is the first thing anybody asks of a transcript.</b>
/// <see cref="Visibility"/>'s note is about the <em>other</em> participants, a different fact.
/// </para>
/// <para>
/// <b>They are genuinely different.</b> §5.0 makes a conversation a set of pairwise exchanges, so
/// who <i>could</i> see a message is fixed the moment it is written and is a statement about the
/// protocol. Whether the recipient has <i>collected</i> it is a fact about this deployment right
/// now: §4.2 has an agent take delivery of its inbox, and until it does, the message sits there
/// with <c>delivered_utc</c> unset. An agent can be attached, running and idle with mail waiting.
/// </para>
/// <para>
/// <b>"Collected" rather than "read" or "seen".</b> Delivery is the broker handing the message
/// over, which is what it knows; what the agent did with it afterwards is not something any row in
/// this store records, and a word implying otherwise would be the transcript claiming to know
/// whether somebody paid attention.
/// </para>
/// </remarks>
public static class Delivery
{
    /// <summary>What is said of a message still sitting in an inbox.</summary>
    public const string Waiting = "not collected yet";

    /// <summary>Says whether one message has been collected, and when.</summary>
    /// <param name="message">The message.</param>
    /// <param name="zone">The zone to write the time in, or null for the machine's own.</param>
    /// <returns>A few words for the header.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <remarks>
    /// In the header rather than on a line of its own: it is one fact about the message, it is
    /// short, and a transcript with a second annotation line per message would be mostly annotation.
    /// </remarks>
    public static string Of(Message message, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(message);

        return message.DeliveredUtc is { } when
            ? $"collected {LocalTime.Write(when, zone)}"
            : Waiting;
    }
}
