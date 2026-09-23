namespace Parley.Core;

/// <summary>
/// The kind of message travelling between two participants (SPEC.md §5).
/// </summary>
/// <remarks>
/// <para>
/// Three, and every one is answered (principle 2.7). A Request always ends in a Result, a Clarify
/// always gets a reply in the same exchange, and a Result closes its exchange. The two types that
/// existed only to carry no reply are gone: an announcement is a Request like any other (§5.4), and
/// nothing times out, so there is no notice that a Result will never come (§14).
/// </para>
/// <para>
/// There is no type the broker alone may send. When the person closes a conversation the broker
/// writes a Result whose items are all <see cref="Outcome.Closed"/>, attributed to
/// <c>parley:broker</c>, and the schema holds that attribution and that outcome together (§9.3).
/// </para>
/// </remarks>
public enum MessageType
{
    /// <summary>
    /// Opens an exchange, asking the receiver for work in its repository or information it holds.
    /// Carries items, the requester's intent, and what counts as done.
    /// </summary>
    Request = 0,

    /// <summary>Asks for or supplies something within an open exchange, handing the turn over.</summary>
    /// <remarks>
    /// For the requester only. What the receiver needs from anybody else it asks for with a Request
    /// of its own, in the same conversation (§5.2).
    /// </remarks>
    Clarify = 1,

    /// <summary>Reports what the Request came to, item by item, and closes the exchange.</summary>
    Result = 2
}
