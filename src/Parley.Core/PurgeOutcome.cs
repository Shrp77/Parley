namespace Parley.Core;

/// <summary>
/// What became of a purge.
/// </summary>
/// <remarks>
/// Two outcomes rather than a boolean, because "there was no such agent" is usually a typo and
/// reads identically to success in a row count. There is no third outcome for a refusal: purge
/// does not refuse. See <see cref="PurgeOutcome"/>.
/// </remarks>
public enum PurgeVerdict
{
    /// <summary>No agent of that name is registered, retired or otherwise.</summary>
    /// <remarks>
    /// First so that it is the zero value, which is the whole reason the order is not the
    /// obvious one. <see cref="PurgeOutcome"/> is a record with a public positional
    /// constructor, so a default-constructed verdict is reachable — and a default that reads
    /// as "the agent was deleted" is the wrong way for that mistake to fail. Nothing persists
    /// these numbers, so the order is free to be chosen on this ground alone.
    /// </remarks>
    NotRegistered,

    /// <summary>The agent and everything it was party to are gone.</summary>
    Gone,
}

/// <summary>
/// One table's share of what a purge destroyed.
/// </summary>
/// <param name="Table">The table.</param>
/// <param name="Rows">How many of its rows were deleted.</param>
/// <remarks>
/// Reported per table rather than as a total, because the total is the one number that tells an
/// operator nothing. "Forty-one rows" could be a dead registration's leftovers or two months of
/// a working conversation; "eleven messages, three exchanges, two conversations" is the same
/// fact in a form somebody can be alarmed by. Nothing here can be undone, so the report is the
/// only account that will ever exist of what was in the database a moment ago.
/// </remarks>
public sealed record PurgeToll(string Table, int Rows);

/// <summary>
/// What one purge destroyed.
/// </summary>
/// <param name="Verdict">Whether there was an agent to purge.</param>
/// <param name="Destroyed">
/// What was deleted, per table, worst first. Empty when <paramref name="Verdict"/> is
/// <see cref="PurgeVerdict.NotRegistered"/>.
/// </param>
/// <remarks>
/// <para>
/// <b>Purge is the nuclear option and is not a polite command.</b> It deletes the agent, every
/// message it sent or received, every exchange it was party to, and every conversation it took
/// part in — including the halves of those conversations belonging to peers who did nothing
/// wrong. That collateral is not a defect being tolerated; it is what deleting a party to a
/// conversation means, since a conversation with one side removed is a record that misleads
/// whoever reads it next.
/// </para>
/// <para>
/// It used to refuse the moment anything referenced the agent, which made it useful only for a
/// registration that had never traded — a typo's worth of cleanup — and left an operator who
/// genuinely wanted an agent gone with no command that did it. Retirement is still the right
/// instrument for an agent that has finished: it keeps the history and frees nothing. Purge is
/// for when the history is the thing you want gone.
/// </para>
/// <para>
/// There is deliberately no <c>Succeeded</c> property: a derived summary is a second definition of
/// the outcome that drifts from the first the moment a verdict is added. A caller that wants to know whether the agent
/// is gone names <see cref="PurgeVerdict.Gone"/>.
/// </para>
/// </remarks>
public sealed record PurgeOutcome(PurgeVerdict Verdict, IReadOnlyList<PurgeToll> Destroyed)
{
    /// <summary>There was no such agent.</summary>
    public static PurgeOutcome NotRegistered { get; } = new(PurgeVerdict.NotRegistered, []);

    /// <summary>
    /// Reports an agent that was purged, and what went with it.
    /// </summary>
    /// <param name="destroyed">What was deleted, per table. Tables that lost nothing are omitted.</param>
    /// <returns>A completed purge carrying <paramref name="destroyed"/>.</returns>
    /// <remarks>
    /// An empty toll is legitimate and is not refused: an agent that registered and never traded
    /// leaves nothing behind but its own row, and that is the one case the old command could
    /// already handle.
    /// </remarks>
    public static PurgeOutcome Razed(IReadOnlyList<PurgeToll> destroyed)
    {
        ArgumentNullException.ThrowIfNull(destroyed);

        return new PurgeOutcome(PurgeVerdict.Gone, destroyed);
    }
}
