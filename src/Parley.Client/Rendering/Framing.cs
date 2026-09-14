namespace Parley.Client.Rendering;

/// <summary>
/// How a person is asked for the parts of a Request, per SPEC.md §5.1 and §8 rule 1.
/// </summary>
/// <remarks>
/// <para>
/// <b>One set of labels read by both surfaces</b>, because two surfaces wording the same field differently
/// is how a person comes to think they are different fields.
/// </para>
/// <para>
/// <b>An example per field, out of §5.1's own.</b> Stated abstractly, why somebody wants a thing and what
/// they want can read as one thing; side by side they cannot be confused:
/// </para>
/// <code>
/// intent:    "Adding tax-inclusive pricing, so invoices show exactly what the order charged."
/// item:      "Stop recomputing line-item price at invoice time. Use the price the order supplies."
/// done_when: "Invoices for orders created after the change show the order's price unchanged."
/// </code>
/// <para>
/// <b>Short because a label is rows.</b> Every one of these is drawn above its field in a terminal that has
/// to fit the fields, what is wrong with each, and the buttons.
/// </para>
/// </remarks>
public static class Framing
{
    /// <summary>How the intent is asked for.</summary>
    public const string Intent = "Why you want this — what it is for";

    /// <summary>An intent somebody can pattern from.</summary>
    public const string IntentExample =
        "e.g. \"Adding tax-inclusive pricing, so invoices show exactly what the order charged.\"";

    /// <summary>How the work or question itself is asked for.</summary>
    public const string Item = "What you want done in their repository, or what you need to know";

    /// <summary>An item somebody can pattern from.</summary>
    public const string ItemExample =
        "e.g. \"Stop recomputing line-item price at invoice time. Use the price the order supplies.\"";

    /// <summary>How <c>done_when</c> is asked for.</summary>
    /// <remarks>
    /// <b>And why it is worth writing, because this is the field people skip.</b> §5.1 calls it the
    /// highest-leverage field in the protocol: it scopes the work, it tells the receiver when to stop, and
    /// it is what the Result is checked against. A label that says only what to type gets a sentence typed
    /// to satisfy it.
    /// </remarks>
    public const string DoneWhen = "What counts as finished";

    /// <summary>Why that field earns the typing.</summary>
    public const string DoneWhenWhy = "It tells them when to stop, and it is what their Result is checked against";

    /// <summary>A <c>done_when</c> somebody can pattern from.</summary>
    public const string DoneWhenExample =
        "e.g. \"Invoices for orders created after the change show the order's price unchanged.\"";
}
