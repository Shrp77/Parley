using System.Text;
using Parley.Core;

namespace Parley.Client.Rendering;

/// <summary>What opens in the box when the person spins a conversation off from a message (SPEC.md §5.0).</summary>
/// <remarks>
/// Only ever a start. The person sees it, cuts it down and adds to it, and what is sent is what is in the box under the line
/// <see cref="Opened"/> adds. It is read
/// from the broker's record of the message, never from anything a link carries, because an agent's message can hold a live link.
/// </remarks>
public static class Quoting
{
    /// <summary>What a surface tells the person before they send, because the line goes out under their name.</summary>
    public const string OpeningIsAdded =
        "Your words go out under a line saying this is a side conversation and which conversation it came from.";

    /// <summary>The first message of a spin-off: a line saying what this is, then what was in the box.</summary>
    /// <param name="child">The new conversation.</param>
    /// <param name="parent">The conversation it came from.</param>
    /// <param name="text">What is in the box.</param>
    /// <returns>The message to send.</returns>
    /// <remarks>
    /// A quote alone tells its receiver nothing: it is handed words it may have written itself, with no way to tell why.
    /// An agent gets no access to the other conversation, so this line is all it knows of where this came from. Added at the
    /// send, because on the web the name is still being typed when the box is filled. It speaks of an entry only when the
    /// box still holds a quoted line, since the person may have cut the quote out.
    /// </remarks>
    public static string Opened(string child, string parent, string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var quotes = text.ReplaceLineEndings("\n").Split('\n').Any(line => line.TrimStart().StartsWith('>'));
        var opening = quotes
            ? $"Side conversation {child} has been stood up to discuss the following conversation entry from {parent}."
            : $"Side conversation {child} has been stood up from {parent}.";

        return opening + "\n\n" + text.TrimStart('\r', '\n');
    }

    /// <summary>Quotes a message, saying who said it and where.</summary>
    /// <param name="message">The message.</param>
    /// <param name="conversation">The conversation it is in.</param>
    /// <returns>The quote, ending in a blank line for the person to write after.</returns>
    public static string Of(Message message, string conversation)
    {
        ArgumentNullException.ThrowIfNull(message);

        // A notice the broker wrote on a person's instruction is never the sender's words (SPEC.md §13), whoever's name is on it.
        var quote = new StringBuilder().AppendLine(string.Equals(message.ResponderModel, Responder.Broker, StringComparison.Ordinal)
            ? $"Parley noted in {conversation}:"
            : $"{message.From.Value} said in {conversation}:");

        foreach (var line in PayloadShape.Spoken(message.PayloadJson).ReplaceLineEndings("\n").Split('\n'))
        {
            quote.Append("> ").AppendLine(line);
        }

        return quote.AppendLine().ToString();
    }
}
