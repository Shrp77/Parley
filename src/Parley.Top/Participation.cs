using System.Text;
using Parley.Client.Rendering;
using Parley.Client.Seating;

namespace Parley.Top;

/// <summary>
/// Taking part: what is waiting, answering what is owed, and opening a conversation.
/// </summary>
/// <remarks>
/// <para>
/// The refusals are <see cref="SeatVoice"/>'s and are not copied here. It checks rule 8.1's intent
/// and consequences and rule 8.5's size cap before anything goes on the wire, and it returns the
/// sentence to show — so this collects text and reports what it is told. A second implementation
/// of those rules would be a second opinion about what the protocol requires, and the one that
/// disagreed would be the one that let something through.
/// </para>
/// <para>
/// When the broker refuses anyway, <b>its own words are shown</b>. §7.3 says a refusal returns work
/// rather than a retry instruction, and a surface that replaced the broker's sentence with a
/// generic failure would throw away the only part a person can act on.
/// </para>
/// <para>
/// <c>ghosted</c> is never offered as an outcome. §5.3 reserves it for the broker recording an
/// exchange nobody returned to, and a person able to write it over their own unanswered work would
/// be manufacturing the evidence §15 counts to find an unhealthy deployment. The list comes from
/// <see cref="SeatVoice.Outcomes"/>, so this surface cannot quietly grow a fourth.
/// </para>
/// </remarks>
internal static class Participation
{
    /// <summary>What taking delivery does, said before it is done.</summary>
    /// <remarks>
    /// A drain marks messages delivered and starts the working window of §6 on anything owed, so it
    /// is not a look at an inbox — it is taking delivery of one. Somebody who discovered that
    /// afterwards would have started a clock they did not know was running.
    /// </remarks>
    internal const string DeliveryWarning =
        "Taking delivery marks these messages delivered and starts the working window on anything "
        + "you then owe. It is not a look at the inbox.";

    /// <summary>
    /// What is said when the seat went while somebody was still writing.
    /// </summary>
    /// <remarks>
    /// It names the sweep rather than blaming the person, and it says outright that nothing was
    /// sent — which is the fact they need, because what they would otherwise assume is that the
    /// message went and the peer is thinking about it.
    /// </remarks>
    internal const string Gone =
        "Your seat was released while you were writing, so nothing was sent. Take a seat again to "
        + "send it.";

    /// <summary>What the dialog offering delivery is called.</summary>
    internal const string DeliveryTitle = "Take delivery";

    /// <summary>What the dialog asking a peer a question is called.</summary>
    internal const string AskTitle = "Ask somebody a question";

    /// <summary>What the dialog telling a peer something is called.</summary>
    internal const string TellTitle = "Tell somebody something";

    /// <summary>What the dialog answering a debt is called.</summary>
    internal const string AnswerTitle = "Answer something you owe";

    /// <summary>What is said when nothing was waiting.</summary>
    private const string NothingWaiting = "Nothing was waiting.";

    /// <summary>What is said when a person is holding nothing.</summary>
    private const string HoldingNothing =
        "You are holding nothing. Take delivery to see what is waiting.";

    /// <summary>What is said beside a message that owes an answer.</summary>
    internal const string Owes = "you owe an answer";

    /// <summary>What is said beside one that does not.</summary>
    private const string OwesNothing = "nothing owed";

    /// <summary>Takes delivery, and says what that did.</summary>
    /// <param name="voice">The person's own agent operations.</param>
    /// <param name="hold">The seat to record against.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What to say about it.</returns>
    /// <remarks>
    /// One call, and the recording is inside it. Draining and then recording separately is two
    /// surfaces' worth of the same rule, which is how they come to disagree about what a person has
    /// taken — so the drive lives in <c>Parley.Client</c> beside the state it changes.
    /// </remarks>
    internal static async Task<string> TakeDeliveryAsync(
        SeatVoice voice,
        SeatHold hold,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(voice);

        var (taken, polls) = await voice.TakeDeliveryAsync(hold, cancellationToken).ConfigureAwait(false);

        return taken.Count == 0 && polls == 0
            ? NothingWaiting
            : $"{taken.Count} taken. The working window is running on anything you now owe.";
    }

    /// <summary>What the person is already holding, one line each.</summary>
    /// <param name="hold">The seat.</param>
    /// <returns>The lines, debts first.</returns>
    /// <remarks>
    /// The owed column is a word before it is a position. Ordering debts first helps an eye and
    /// would carry the whole distinction if the word were not there, which is the colour-alone
    /// failure in a different costume.
    /// </remarks>
    internal static IReadOnlyList<string> Held(SeatHold hold)
    {
        ArgumentNullException.ThrowIfNull(hold);

        if (hold.Delivered.Count == 0)
        {
            return [HoldingNothing];
        }

        return
        [
            .. hold.Delivered
                .OrderByDescending(one => one.Owes)
                .Select(one =>
                {
                    var payload = Payload.Read(one.Message.PayloadJson);
                    var said = payload.Raw is { Length: > 0 } raw ? raw : payload.Said;

                    return $"{one.Message.From.Value}  "
                        + $"[{one.Message.Type.ToString().ToLowerInvariant()}]  "
                        + $"[{(one.Owes ? Owes : OwesNothing)}]  {Shorten(said)}";
                }),
        ];
    }

    /// <summary>How one owed question reads in a chooser.</summary>
    /// <param name="owed">The delivered question.</param>
    /// <returns>Who asked, and the start of what they asked.</returns>
    internal static string Label(Delivered owed)
    {
        ArgumentNullException.ThrowIfNull(owed);

        var payload = Payload.Read(owed.Message.PayloadJson);
        var said = payload.Raw is { Length: > 0 } raw ? raw : payload.Said;

        return $"{owed.Message.From.Value}: {Shorten(said, 60)}";
    }

    /// <summary>The question as it was asked, with everything rule 8.1 required of the asker.</summary>
    /// <param name="owed">The delivered question.</param>
    /// <returns>The text to show above the answer.</returns>
    /// <remarks>
    /// Intent and consequences included, because rule 8.1 requires an asker to state them and an
    /// answer written without reading them is an answer to a narrower question than was asked.
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
            body.Append("  - ").Append(point.Id).Append(": ").AppendLine(point.Text);
        }

        if (payload.Consequences is { Length: > 0 } consequences)
        {
            body.AppendLine().Append("Consequences: ").AppendLine(consequences);
        }

        return body.ToString();
    }

    /// <summary>What is wrong with an answer somebody has typed.</summary>
    /// <param name="said">What is in the field.</param>
    /// <returns>The reason, or null when there is none.</returns>
    /// <remarks>
    /// An empty resolution closes the exchange while telling the asker nothing, and they cannot ask
    /// again — so it is refused rather than warned about.
    /// </remarks>
    internal static string? AnswerProblem(string said) =>
        string.IsNullOrWhiteSpace(said)
            ? "An empty resolution closes the exchange while telling the asker nothing, "
              + "and they cannot ask again."
            : null;

    /// <summary>What is wrong with the consequences somebody has typed.</summary>
    /// <param name="said">What is in the field.</param>
    /// <returns>The reason, or null when there is none.</returns>
    /// <remarks>
    /// Checked here as well as by <see cref="SeatVoice"/> because this one is checked <i>as it is
    /// typed</i>, and a person meeting the length rule only on submission has already written the
    /// question. The authority is still the client's: this refuses a superset of nothing.
    /// </remarks>
    internal static string? ConsequencesProblem(string said) =>
        (said ?? string.Empty).Trim().Length < SeatVoice.MinimumConsequences
            ? $"At least {SeatVoice.MinimumConsequences} characters: say what turns on the answer."
            : null;

    /// <summary>What is wrong with a line that simply has to be there.</summary>
    /// <param name="said">What is in the field.</param>
    /// <returns>The reason, or null when there is none.</returns>
    internal static string? RequiredProblem(string said) =>
        string.IsNullOrWhiteSpace(said) ? "This cannot be empty." : null;

    /// <summary>A conversation name nobody has to think of.</summary>
    /// <returns>Something short and unlikely to collide.</returns>
    internal static string Suggested() =>
        "top-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Keeps one row to one row.</summary>
    /// <param name="text">What the message said.</param>
    /// <param name="width">How much of it to keep.</param>
    /// <returns>The first part of it.</returns>
    private static string Shorten(string text, int width = 80)
    {
        var flattened = (text ?? string.Empty).ReplaceLineEndings(" ").Trim();

        return flattened.Length <= width ? flattened : flattened[..(width - 3)] + "...";
    }
}
