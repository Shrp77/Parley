namespace Parley.Client.Rendering;

/// <summary>
/// How a person is asked for a Request, per SPEC.md §5.1 and §8 rule 1.
/// </summary>
/// <remarks>
/// <para>
/// <b>One set of labels read by both surfaces</b>, because two surfaces wording the same field differently
/// is how a person comes to think they are different fields.
/// </para>
/// <para>
/// <b>There is one field to label now, and that is the change.</b> Until 0.7 there were four — an intent, the
/// item, its kind, and a <c>done_when</c> with a paragraph arguing for itself — and the owner, composing one,
/// read them as the same thing asked four times. A receiver that cannot tell what an ask is for, or how far it
/// goes, sends a Clarify (§5.2); that is a question only the receiver is in a position to ask.
/// </para>
/// <para>
/// <b>Short because a label is rows.</b> It is drawn above its field in a terminal that has to fit the field,
/// what is wrong with it, and the buttons.
/// </para>
/// </remarks>
public static class Framing
{
    /// <summary>How the ask is asked for.</summary>
    public const string Ask = "What you want";

    /// <summary>What the ask is worth saying in it, for a surface with room for a second line.</summary>
    /// <remarks>
    /// Says what a good ask contains without asking for it in fields: the thing wanted, and why where that is not
    /// obvious. A receiver reads both out of one sentence, as a colleague would.
    /// </remarks>
    public const string AskWhy = "Say what it is for when that is not obvious, and they will do the right thing";

    /// <summary>An ask somebody can pattern from.</summary>
    public const string AskExample =
        "e.g. \"Stop recomputing line-item price at invoice time — use the price the order supplies — so invoices "
        + "show what the order charged.\"";
}
