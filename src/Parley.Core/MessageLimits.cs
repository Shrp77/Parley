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
    /// five of the six agents were turned away on <em>answers</em> — Results, in 0.5 — which is the
    /// direction that costs the requester something it had already decided it needed.
    /// </para>
    /// <para>
    /// What it buys is editorial and that has not changed. A bounded Result forces the receiver
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
    /// <b>Since 0.7 nothing a sender writes reaches here.</b> A Request is the ask (§5.1) and declares no
    /// identifiers, so what a Result names is either the single spelling <c>request</c> or the identifiers of a
    /// Request stored under an earlier version — which are the reason this bound is still enforced rather than
    /// retired. 0.5 and 0.6 refused a longer one when the Request was written; a question 0.4 wrote was not, and
    /// its longer ids are read as though absent so the work can still be reported on
    /// (<see cref="PayloadShape.ItemIds"/>).
    /// </para>
    /// </remarks>
    public const int LongestItemId = 64;

    /// <summary>What is put in place of the part of a reply that did not fit.</summary>
    /// <remarks>
    /// <para>
    /// A reply is an agent's answer to an operator's instruction, and until the review gate looked
    /// it was the one piece of text in the store with no bound at all — the instruction beside it
    /// has carried the cap since the migration that set it.
    /// </para>
    /// <para>
    /// <b>Truncated rather than refused, and the direction matters.</b> Refusing the report would
    /// leave the instruction unsettled and lose the answer entirely, which is the failure the whole
    /// readback exists to remove. A shortened answer is worth more than none, and saying so in the
    /// text is what stops a reader treating the cut as the agent's own ending.
    /// </para>
    /// </remarks>
    public const string Truncated = " [... the rest was over the cap]";

    /// <summary>Cuts text to the cap, saying so where it was cut.</summary>
    /// <param name="text">What was said.</param>
    /// <returns>The text, at most <see cref="MaxPayloadCharacters"/> UTF-16 units long.</returns>
    /// <remarks>
    /// <para>
    /// Here rather than at the one caller because the storage schema performs the same cut when it
    /// carries old rows across, and two implementations of "shorten to the cap" would disagree
    /// about the boundary the first time either was edited. §9.3's argument, one level out: the
    /// CHECK is the enforcement, and this is what keeps every writer under it.
    /// </para>
    /// <para>
    /// <b>Counted in UTF-16 units while the CHECK counts characters, and the inequality is the
    /// point.</b> SQLite's <c>length()</c> counts one for an astral-plane character where this
    /// counts two, so this cuts sooner than the schema requires and can never hand the store
    /// something it will refuse. The reverse would turn every oversized reply into a lost one.
    /// </para>
    /// </remarks>
    public static string Fit(string? text)
    {
        if (text is null || text.Length <= MaxPayloadCharacters)
        {
            return text ?? string.Empty;
        }

        var cut = MaxPayloadCharacters - Truncated.Length;

        // **Backed off a surrogate, because a fixed offset cuts through one.** Found by probing the
        // boundary with astral-plane text: the offset landed between the two halves of a pair and
        // left a lone high surrogate at the end — not a character, not what the agent said, and the
        // kind of value that breaks whatever reads it next rather than here.
        //
        // One step is enough. A pair is exactly two units, so an offset can be inside at most one.
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return string.Concat(text.AsSpan(0, cut), Truncated);
    }
}
