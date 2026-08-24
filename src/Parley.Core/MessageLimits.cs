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
    /// <b>The number is a judgement and has never been anything else.</b> It was two thousand,
    /// chosen because it sounded like enough, and it is four thousand now for the same quality of
    /// reason — roughly a thousand tokens, small beside any modern context and far too small to
    /// paste a file into. What the cap defends is bounded more strongly elsewhere anyway: one
    /// open exchange per pair and a cap on clarification rounds bound the volume, so this is the
    /// third control on the same risk rather than the only one.
    /// </para>
    /// <para>
    /// What it buys that those do not is editorial. A bounded answer forces the answerer to
    /// decide what matters; an unbounded one hands that job to the asker, which is the
    /// shared-context problem arriving by another route.
    /// </para>
    /// <para>
    /// Every refusal is now counted — see <c>SizeRefusalRepository</c> and the operator's
    /// <c>refusals</c> command. The next value for this constant should come from that record.
    /// A limit nobody measures is a limit that moves whenever somebody complains.
    /// </para>
    /// </remarks>
    public const int MaxPayloadCharacters = 4000;
}
