namespace Parley.Core;

/// <summary>
/// The bounds SPEC.md §8 places on a single message.
/// </summary>
/// <remarks>
/// <para>
/// Protocol vocabulary rather than a shape the database happens to have, which is why it sits
/// here beside <see cref="MessageType"/> and <see cref="ExchangeState"/>. The schema is still the
/// enforcement of record — §9.3 puts invariants in storage precisely because application code can
/// route around them, and the <c>CHECK</c> on <c>payload_json</c> is what makes the cap
/// unroutable-around. This is the same number, stated where an adapter can see it before writing.
/// </para>
/// <para>
/// An adapter needs it because the cap is one an agent will reach in the ordinary course of
/// working, and reaching it should produce something the agent can act on. Left to storage, an
/// over-long message arrives as a constraint violation carrying SQL, which tells a model nothing
/// about being terser and is not a shape it can correct against.
/// </para>
/// </remarks>
public static class MessageLimits
{
    /// <summary>
    /// The most characters one message's payload may carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A context-poisoning defence rather than a style preference: what a peer sends lands in
    /// another agent's context, so an unbounded message is an unbounded claim on somebody else's
    /// working memory. §8 makes terseness a rule for the same reason, and this is the part of it
    /// that does not depend on a model having read the rule.
    /// </para>
    /// <para>
    /// <b>The number is set from measurement.</b> It comes from <c>refusals</c>, which is what that record
    /// is instrumented for: the recorded refusals fit under it with room left, and most were on
    /// <em>answers</em> — Results — which is the direction that costs the requester something it had
    /// already decided it needed.
    /// </para>
    /// <para>
    /// What it buys is editorial. A bounded Result forces the receiver
    /// to decide what matters; an unbounded one hands that job to the requester, which is the
    /// shared-context problem arriving by another route. Nothing else bounds volume by default —
    /// the per-pair and clarification caps are both off unless a deployment sets them — so this is
    /// the one control on that risk that always applies.
    /// </para>
    /// <para>
    /// <b>It bounds a message, not an exchange.</b> With no clarification cap by default, an
    /// exchange that goes back and forth fifty times can carry fifty messages of this size in each
    /// direction, and SPEC.md rule 8.4 says so. Moving this number moves the size of every one of
    /// them, which is the thing to reason about before moving it again.
    /// </para>
    /// <para>
    /// Refusals keep being counted, so the next move can be argued the same way. A limit nobody
    /// measures is a limit that moves whenever somebody complains.
    /// </para>
    /// </remarks>
    public const int MaxPayloadCharacters = 8000;

    /// <summary>The longest item id a Result can carry (SPEC.md §5.3).</summary>
    /// <remarks>
    /// <para>
    /// A Result answers by id and storage records one row per outcome, holding the id to this.
    /// </para>
    /// <para>
    /// <b>Nothing a sender writes reaches here.</b> A Request is the ask (§5.1) and declares no
    /// identifiers, so what a Result names is the single spelling <see cref="PayloadShape.WholeRequest"/>.
    /// </para>
    /// </remarks>
    public const int LongestItemId = 64;
}
