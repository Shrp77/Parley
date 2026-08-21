namespace Parley.Core;

/// <summary>
/// The kind of message travelling between two agents.
/// </summary>
/// <remarks>
/// <para>
/// Four an agent may send, and the set is closed by design. Agents do not exchange
/// pleasantries, so every additional type would be a new way to spend tokens without
/// producing a decision.
/// </para>
/// <para>
/// <see cref="Ghost"/> is the exception: no agent can send one, and the schema refuses a ghost
/// that does not record the broker as its responder. The converse is deliberately not enforced,
/// because <c>parley:broker</c> also attributes the forced <c>unknown</c> §7 writes when the
/// clarify cap breaks — which is an <see cref="Answer"/>. It exists because
/// SPEC.md §14 requires the broker to tell an asker that no answer is coming, and none of the
/// other four can carry that — an <see cref="Answer"/> is refused on a ghosted exchange by the
/// trigger asserting that a ghost means nobody answered, and a <see cref="Notify"/> may not
/// name an exchange at all.
/// </para>
/// </remarks>
public enum MessageType
{
    /// <summary>
    /// Opens an exchange and claims the pair slot. Carries one or more sub-questions, the
    /// asker's intent, and what the asker will do with each possible answer.
    /// </summary>
    Question = 0,

    /// <summary>
    /// Requests or supplies detail within an already-open exchange.
    /// </summary>
    /// <remarks>
    /// Deliberately claims no slot of its own. If it did, an answerer needing detail could
    /// not ask for it, because the pair's only slot is held by the very question being
    /// clarified — a deadlock inside the deadlock fix.
    /// </remarks>
    Clarify = 1,

    /// <summary>
    /// Resolves and closes an exchange, releasing the pair slot.
    /// </summary>
    Answer = 2,

    /// <summary>
    /// A fire-and-forget event. Opens no exchange, claims no slot, and expects no reply.
    /// </summary>
    /// <remarks>
    /// Typically a contract change announcement. Expected to be the highest-volume traffic
    /// in a mature deployment.
    /// </remarks>
    Notify = 3,

    /// <summary>
    /// The broker telling an asker that its question will never be answered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written only by the broker, when the grace period runs out on an orphan (SPEC.md §14).
    /// It carries the asker's own <c>intent</c>, <c>sub_questions</c> and <c>consequences</c>
    /// back to it, because by then hours have passed and an exchange identifier alone means
    /// nothing to a model that no longer remembers asking — §4.2's argument for why a delivered
    /// Answer reconstitutes them applies at least as strongly to a non-answer.
    /// </para>
    /// <para>
    /// A type of its own rather than an Answer, and the reason is what the schema can see.
    /// A ghost asserts that nobody answered, which the trigger on <c>exchanges</c> enforces by
    /// refusing the resolution when an <c>answer</c> message exists; writing the broker's notice
    /// as an Answer would make that invariant false the moment it was recorded. Attribution
    /// cannot rescue it either — §11.3 is explicit that the responder column cannot distinguish
    /// the broker writing its own name from the broker faithfully recording an adapter that
    /// supplied it. The type can be distinguished, so the type is what carries the distinction.
    /// </para>
    /// </remarks>
    Ghost = 4
}
