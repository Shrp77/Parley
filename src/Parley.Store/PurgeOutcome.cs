namespace Parley.Store;

/// <summary>
/// What became of a purge.
/// </summary>
/// <remarks>
/// Three outcomes rather than a boolean, because "there was no such agent" and "there was, and
/// it has a history worth keeping" call for opposite responses from an operator and are
/// indistinguishable from a row count. The first is usually a typo; the second is the case
/// SPEC.md §14 built retirement for.
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

    /// <summary>The registration was deleted, and its name is registrable again.</summary>
    Gone,

    /// <summary>
    /// Something references the agent, so deleting it would take that with it. See
    /// <see cref="PurgeOutcome.Holds"/> for what.
    /// </summary>
    Held,
}

/// <summary>
/// One reference standing between an agent and deletion.
/// </summary>
/// <param name="Table">The table holding the reference.</param>
/// <param name="Column">The column in it that names the agent.</param>
/// <param name="Rows">How many rows of that table name it.</param>
/// <remarks>
/// Named down to the column because two columns of the same table mean different things: an
/// agent held by <c>exchanges.asker</c> has asked something, and one held by
/// <c>exchanges.answerer</c> has been asked. An operator deciding whether the history is worth
/// keeping is deciding about that difference, and a table name alone does not carry it.
/// </remarks>
public sealed record PurgeHold(string Table, string Column, int Rows);

/// <summary>
/// What one purge did, or what stopped it.
/// </summary>
/// <param name="Verdict">Which of the three outcomes occurred.</param>
/// <param name="Holds">
/// What referenced the agent, when <paramref name="Verdict"/> is <see cref="PurgeVerdict.Held"/>;
/// empty otherwise.
/// </param>
/// <remarks>
/// There is deliberately no <c>Succeeded</c> property here, for the reason set out on
/// <see cref="ReapResult"/>: a derived summary is a second definition of the outcome that drifts
/// from the first the moment a verdict is added. A caller that wants to know whether the agent
/// is gone names <see cref="PurgeVerdict.Gone"/>.
/// </remarks>
public sealed record PurgeOutcome(PurgeVerdict Verdict, IReadOnlyList<PurgeHold> Holds)
{
    /// <summary>The registration was deleted.</summary>
    public static PurgeOutcome Gone { get; } = new(PurgeVerdict.Gone, []);

    /// <summary>There was no such agent.</summary>
    public static PurgeOutcome NotRegistered { get; } = new(PurgeVerdict.NotRegistered, []);

    /// <summary>
    /// Reports an agent that could not be purged, and what held it.
    /// </summary>
    /// <param name="holds">Every reference found, ordered by table and column.</param>
    /// <returns>A held outcome carrying <paramref name="holds"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="holds"/> is empty.</exception>
    /// <remarks>
    /// An empty hold list is refused rather than accepted as a vacuous refusal: it would print
    /// as "cannot be purged because of nothing", which tells an operator to go and look for a
    /// reference that does not exist.
    /// </remarks>
    public static PurgeOutcome HeldBy(IReadOnlyList<PurgeHold> holds)
    {
        ArgumentNullException.ThrowIfNull(holds);

        if (holds.Count == 0)
        {
            throw new ArgumentException("A held purge must say what held it.", nameof(holds));
        }

        return new PurgeOutcome(PurgeVerdict.Held, holds);
    }
}
