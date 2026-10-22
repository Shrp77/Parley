using Parley.Core;

namespace Parley.Client.Rendering;

/// <summary>
/// Who a transcript says spoke, and to whom (SPEC.md §5, principle 2.11).
/// </summary>
/// <remarks>
/// <para>
/// <b>Parley's words read as Parley's, and a person's words read as a person's, even though both travel under
/// Parley's registry name.</b> The facilitator is the sender of its question and of a reply a person wrote to an agent
/// that asked it something back; what tells the two apart is the attribution, which only the broker writes
/// (<see cref="Facilitation.Attribution"/>, <see cref="Facilitation.PersonAttribution"/>). A transcript that showed the
/// registry name for both would put a person's words in Parley's mouth.
/// </para>
/// <para>
/// <b>Read from the attribution, which no adapter may claim</b> (§11.3), and never from anything in a payload.
/// </para>
/// </remarks>
public static class Speaker
{
    /// <summary>What a transcript calls a reply a person sent through Parley.</summary>
    public const string PersonThroughParley = "a person, through Parley";

    /// <summary>Who wrote a message, as a reader should be told.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The name to draw.</returns>
    public static string From(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return message.ResponderModel switch
        {
            Facilitation.Attribution => Facilitation.DisplayName,
            Facilitation.PersonAttribution => PersonThroughParley,
            _ => message.From.Value
        };
    }

    /// <summary>Who a message was for, as a reader should be told.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The name to draw.</returns>
    public static string To(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return Facilitation.Is(message.To) ? Facilitation.DisplayName : message.To.Value;
    }
}
