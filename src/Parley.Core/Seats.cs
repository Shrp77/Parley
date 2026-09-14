namespace Parley.Core;

/// <summary>
/// How a person taking part from a seat is recognised (SPEC.md §11.2).
/// </summary>
/// <remarks>
/// <para>
/// A seat is an ordinary attachment and speaks the same protocol as an agent, so the only thing
/// distinguishing a person in the registry is the adapter they attached through. That matters in one
/// place in the protocol: a person may always start a new conversation (principle 2.10), where an agent
/// holding a Request may not.
/// </para>
/// <para>
/// In <see cref="Parley.Core"/> because the broker decides on it and the surfaces that seat a person
/// register under it, and neither can reference the other.
/// </para>
/// </remarks>
public static class Seats
{
    /// <summary>The adapter a person's seat registers under.</summary>
    public const string Adapter = "parley-console";
}
