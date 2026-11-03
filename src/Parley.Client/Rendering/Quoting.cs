using System.Text;
using Parley.Core;

namespace Parley.Client.Rendering;

/// <summary>What opens in the box when the person spins a conversation off from a message (SPEC.md §5.0).</summary>
/// <remarks>
/// Only ever a start. The person sees it, cuts it down and adds to it, and what is sent is what is in the box. It is read
/// from the broker's record of the message, never from anything a link carries, because an agent's message can hold a live link.
/// </remarks>
public static class Quoting
{
    /// <summary>Quotes a message, saying who said it and where.</summary>
    /// <param name="message">The message.</param>
    /// <param name="conversation">The conversation it is in.</param>
    /// <returns>The quote, ending in a blank line for the person to write after.</returns>
    public static string Of(Message message, string conversation)
    {
        ArgumentNullException.ThrowIfNull(message);

        var quote = new StringBuilder().AppendLine($"{message.From.Value} said in {conversation}:");

        foreach (var line in PayloadShape.Spoken(message.PayloadJson).ReplaceLineEndings("\n").Split('\n'))
        {
            quote.Append("> ").AppendLine(line);
        }

        return quote.AppendLine().ToString();
    }
}
