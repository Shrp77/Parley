using System.Text;
using Parley.Client;
using Parley.Client.Rendering;
using Parley.Client.Seating;
using Parley.Core;

namespace Parley.Terminal;

/// <summary>
/// Taking part: what is waiting, sending the Results owed, and sending Requests.
/// </summary>
/// <remarks>
/// <para>
/// The refusals are <see cref="SeatVoice"/>'s and are not copied here. It checks rule 8.1 and rule 8.4
/// before anything goes on the wire and returns the sentence to show, so this collects text and reports
/// what it is told.
/// </para>
/// <para>
/// When the broker refuses anyway, <b>its own words are shown</b>: §7.3 says a refusal returns work rather
/// than a retry instruction, and replacing the broker's sentence would throw away the part a person can act
/// on.
/// </para>
/// <para>
/// There is one way to say something to an agent, and it is answered (principle 2.7). The surface that had
/// "Ask" and "Announce" had two, and one of them expected nothing back. An announcement is a Request asking
/// for an acknowledgement.
/// </para>
/// </remarks>
internal static class Participation
{
    /// <summary>What is said when the connection went while somebody was still writing.</summary>
    /// <remarks>
    /// Not "your seat was released": the seat is the broker's and is never released. What went is this
    /// terminal's stream, and the key that asked has already started taking it back.
    /// </remarks>
    internal const string Gone =
        "You are not connected to this project, so nothing was sent. Send it again once the connection is back.";

    /// <summary>What the dialog that names a conversation is called.</summary>
    internal const string NameTitle = "Name the conversation";

    /// <summary>What the peer picker asks.</summary>
    internal const string WhichPeer = "Who are you sending this to?";

    /// <summary>When a project holds nobody to speak to.</summary>
    internal const string NoPeers =
        "There is nobody in this project to send anything to yet. Add an agent, or wait for one to register.";

    /// <summary>The row that sends to every agent in the project at once.</summary>
    /// <param name="howMany">How many agents it will reach.</param>
    /// <returns>The row's label.</returns>
    /// <remarks>
    /// <b>The count is in the label because that is the last moment to say it.</b> Every Request is a model
    /// turn and an exchange owed a Result, and a row reading only "All agents" would hide the difference
    /// between three and thirty.
    /// </remarks>
    internal static string Everyone(int howMany) =>
        howMany == 1 ? "All agents (1)" : $"All agents ({howMany})";

    /// <summary>Whether a picked row is the everyone row rather than a participant.</summary>
    /// <param name="chosen">What was picked.</param>
    /// <param name="howMany">How many agents the row was built for.</param>
    /// <returns>Whether it means everybody.</returns>
    internal static bool IsEveryone(string chosen, int howMany) =>
        string.Equals(chosen, Everyone(howMany), StringComparison.Ordinal);

    /// <summary>What the dialog sending a Request inside a conversation is called.</summary>
    internal const string RequestTitle = "Send a request";

    /// <summary>Sending from the conversations list, where what is being made is a new conversation.</summary>
    /// <remarks>
    /// The same action, named for where it is offered: from a transcript it continues the conversation on
    /// screen; from the list it starts one.
    /// </remarks>
    internal const string StartTitle = "Start a conversation with a request";

    /// <summary>What the dialog sending a Result is called.</summary>
    internal const string ResultTitle = "Send the result of something you were asked";

    /// <summary>What the dialog sending a clarification is called.</summary>
    /// <remarks>
    /// One action for both directions, as <c>parley_clarify</c> is one tool: a person holding a request asks
    /// its requester for what only it can give, and a person asked something answers it.
    /// </remarks>
    internal const string ClarifyTitle = "Ask for, or give, a clarification";

    /// <summary>What is said beside a Request a Result is owed on.</summary>
    internal const string Owes = "you owe a result";

    /// <summary>How one owed message reads in a chooser.</summary>
    /// <param name="owed">The delivered message.</param>
    /// <returns>Who sent it, and the start of what they said, made safe to draw.</returns>
    /// <remarks>
    /// Made safe here rather than by the chooser, because the chooser matches the row picked against this same
    /// string: both sides go through one function, so the match survives the neutralising.
    /// </remarks>
    internal static string Label(Delivered owed)
    {
        ArgumentNullException.ThrowIfNull(owed);

        var payload = Payload.Read(owed.Message.PayloadJson);
        var said = payload.Raw is { Length: > 0 } raw ? raw : payload.Said;

        return Safe.Text($"{owed.Message.From.Value}: {Shorten(said, 60)}");
    }

    /// <summary>The Result form's label for an item's outcome.</summary>
    /// <param name="id">The item's id, as the requester wrote it.</param>
    /// <returns>The label, made safe to draw.</returns>
    internal static string OutcomeLabel(string id) =>
        Safe.Text($"{id} — what came of it? ({string.Join(" / ", SeatVoice.Outcomes.Select(o => o.Value))})");

    /// <summary>The Result form's label for what an item came to.</summary>
    /// <param name="id">The item's id, as the requester wrote it.</param>
    /// <returns>The label, made safe to draw.</returns>
    internal static string SaidLabel(string id) =>
        Safe.Text($"{id} — say what was done, the answer, or what blocks it:");

    /// <summary>The Request as it was sent, whole.</summary>
    /// <param name="owed">The delivered message.</param>
    /// <returns>The text to show above the Result form.</returns>
    /// <remarks>
    /// Everything the Request carried, because a Result written without reading it is a Result for a narrower piece
    /// of work than was asked for. A Request stored before 0.7 carried an intent, items and a <c>done_when</c>, and
    /// they are all shown; one written since carries the ask.
    /// </remarks>
    internal static string Asked(Delivered owed)
    {
        ArgumentNullException.ThrowIfNull(owed);

        var payload = Payload.Read(owed.Message.PayloadJson);
        var body = new StringBuilder()
            .Append("From ")
            .Append(owed.Message.From.Value)
            .Append(':')
            .AppendLine()
            .AppendLine(payload.Raw is { Length: > 0 } raw ? raw : payload.Said);

        foreach (var point in payload.Points)
        {
            body.Append("  - ").AppendLine(Reading.Point(point));
        }

        if (payload.DoneWhen is { Length: > 0 } doneWhen)
        {
            body.AppendLine().Append("Done when: ").AppendLine(doneWhen);
        }

        // Every word here after the first line is the requester's, and it is drawn as a form's title. Found by the
        // review gate: an escape sequence in a Request's intent, an item or done_when reached the screen a person
        // answers from. Line by line, so the breaks that lay the request out survive.
        return Safe.Lines(body.ToString());
    }

    /// <summary>What is wrong with what somebody has typed about an item.</summary>
    /// <param name="said">What is in the field.</param>
    /// <returns>The reason, or null when there is none.</returns>
    /// <remarks>A Result that says nothing closes the exchange while telling the requester nothing.</remarks>
    internal static string? ResultProblem(string said) =>
        string.IsNullOrWhiteSpace(said)
            ? "Say what came of it. An empty Result closes the exchange while telling them nothing."
            : ProseProblem(said);

    /// <summary>What is wrong with a line that simply has to be there.</summary>
    /// <param name="said">What is in the field.</param>
    /// <returns>The reason, or null when there is none.</returns>
    internal static string? RequiredProblem(string said) =>
        string.IsNullOrWhiteSpace(said) ? "This cannot be empty." : ProseProblem(said);

    /// <summary>What is wrong with a field of prose, beyond being empty.</summary>
    /// <param name="said">What is in the field.</param>
    /// <returns>The reason, or null when there is none.</returns>
    /// <remarks>
    /// The cap is enforced at the send, by which time the form has closed and what somebody wrote is gone.
    /// This catches the case that actually loses somebody's work: one field already past the limit.
    /// </remarks>
    private static string? ProseProblem(string said) =>
        said.Length <= MessageLimits.MaxPayloadCharacters
            ? null
            : $"That is {said.Length} characters and the limit is {MessageLimits.MaxPayloadCharacters}. Send what "
              + "they need for their part, and point at the rest.";

    /// <summary>What is wrong with a conversation name somebody has typed, as they type it.</summary>
    /// <param name="typed">What is in the field.</param>
    /// <returns>The reason, or null when there is none.</returns>
    internal static string? ConversationProblem(string? typed) =>
        string.IsNullOrWhiteSpace(typed)
            ? "This cannot be empty."
            : ConversationName.Problem(typed) is { } why ? Safe.Text(why) : null;

    /// <summary>A conversation name nobody has to think of.</summary>
    /// <returns>Something short and unlikely to collide.</returns>
    /// <remarks>
    /// Prefixed with the surface's name, so a conversation nobody named says where it was started. It said `top-` until
    /// the gate over the rename found it — the one place the old name was still written for somebody to read.
    /// </remarks>
    internal static string Suggested() =>
        "terminal-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);

    private static string Shorten(string text, int width = 80)
    {
        var flattened = (text ?? string.Empty).ReplaceLineEndings(" ").Trim();

        return flattened.Length <= width ? flattened : flattened[..(width - 3)] + "...";
    }
}
