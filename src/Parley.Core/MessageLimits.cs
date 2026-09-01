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
    /// <b>This is the first value here that was measured rather than judged.</b> Two thousand
    /// was chosen because it sounded like enough; four thousand was chosen the same way. Eight
    /// thousand was set from <c>refusals</c>, which is what that record was instrumented for —
    /// six agents, forty-three refusals, worst case 6,195 characters and means clustered between
    /// 4,328 and 4,556. Every refusal ever recorded fits under this number with room left, and
    /// five of the six agents were turned away on <em>answers</em>, which is the direction that
    /// costs the asker something it had already decided it needed.
    /// </para>
    /// <para>
    /// What it buys is editorial and that has not changed. A bounded answer forces the answerer
    /// to decide what matters; an unbounded one hands that job to the asker, which is the
    /// shared-context problem arriving by another route. Volume is still bounded elsewhere and
    /// more strongly — one open exchange per pair, and a cap on clarification rounds — so this
    /// remains the third control on the same risk rather than the only one.
    /// </para>
    /// <para>
    /// <b>It doubles a worst case that is no longer the binding one.</b> SPEC.md rule 8.5 works
    /// the arithmetic through: with a clarify cap of 200 the most one peer can place in another's
    /// context through a single exchange is <c>(200 + 1) ×</c> this number, which the change from
    /// four thousand moved from about 804,000 characters to about 1,608,000. Both figures are
    /// dominated by the clarify counter rather than by this constant, which is the thing to
    /// reason about before moving this one again.
    /// </para>
    /// <para>
    /// Refusals keep being counted, so the next move can be argued the same way. A limit nobody
    /// measures is a limit that moves whenever somebody complains.
    /// </para>
    /// </remarks>
    public const int MaxPayloadCharacters = 8000;
}
