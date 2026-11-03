namespace Parley.Core;

/// <summary>
/// Whether there was a conversation to destroy.
/// </summary>
public enum DestroyVerdict
{
    /// <summary>No conversation of that name.</summary>
    /// <remarks>
    /// Deliberately first, so a default-constructed value is the harmless one, for the reason
    /// <see cref="PurgeVerdict"/> gives.
    /// </remarks>
    NoSuchConversation = 0,

    /// <summary>It is gone, and so is everything that belonged to it.</summary>
    Gone = 1,

    /// <summary>Nothing went: an exchange here settled one a move has since put in another conversation.</summary>
    /// <remarks>The settled one's record has to name what settled it, and storage holds it to that (SPEC.md §5.6).</remarks>
    SettlesElsewhere = 2,
}

/// <summary>
/// What one destroy removed.
/// </summary>
/// <param name="Verdict">Whether there was a conversation to destroy.</param>
/// <param name="Destroyed">
/// What was deleted, per table, worst first. Empty when <paramref name="Verdict"/> is
/// <see cref="DestroyVerdict.NoSuchConversation"/>.
/// </param>
/// <param name="SettlesIn">The other conversation, when the verdict is <see cref="DestroyVerdict.SettlesElsewhere"/>.</param>
/// <remarks>
/// <para>
/// <b>Per table rather than as a total</b>, for the reason <see cref="PurgeToll"/> records: a
/// total is the one number that carries no warning, and this report is the only account of those
/// rows that will ever exist.
/// </para>
/// <para>
/// <b>The tolls are reused from purge and the outcome is not.</b> A toll is a table and a count
/// and means the same thing whatever was destroyed; a verdict names what was looked for, and
/// <see cref="PurgeVerdict.NotRegistered"/> is about an agent. Sharing the outcome type would
/// have meant a caller destroying a conversation reading a verdict that says <i>not
/// registered</i>, which is true of nothing here.
/// </para>
/// <para>
/// There is no <c>Succeeded</c> property: a derived summary is a second definition of the outcome
/// that drifts from the first the moment a verdict is added.
/// </para>
/// </remarks>
public sealed record DestroyOutcome(DestroyVerdict Verdict, IReadOnlyList<PurgeToll> Destroyed, string? SettlesIn = null)
{
    /// <summary>There was no such conversation.</summary>
    public static DestroyOutcome NoSuchConversation { get; } =
        new(DestroyVerdict.NoSuchConversation, []);

    /// <summary>Reports a destroy refused because of a settled pair split by a move.</summary>
    /// <param name="other">The conversation holding the exchange that was settled.</param>
    /// <returns>A refusal that destroyed nothing.</returns>
    public static DestroyOutcome Settles(string other) => new(DestroyVerdict.SettlesElsewhere, [], other);

    /// <summary>What a person is told about a refusal, or <see langword="null"/> when the destroy was not refused.</summary>
    /// <param name="conversation">The conversation that was to be destroyed.</param>
    /// <returns>A sentence saying why and the two ways on.</returns>
    public string? Refusal(string conversation) => Verdict == DestroyVerdict.SettlesElsewhere
        ? $"Nothing was destroyed: something in {conversation} settled a question that is now in {SettlesIn}. "
          + $"Destroy {SettlesIn} first, or move that question to {conversation} and destroy them together."
        : null;

    /// <summary>
    /// Reports a conversation that was destroyed, and what went with it.
    /// </summary>
    /// <param name="destroyed">What was deleted, per table. Tables that lost nothing are omitted.</param>
    /// <returns>A completed destroy carrying <paramref name="destroyed"/>.</returns>
    public static DestroyOutcome Razed(IReadOnlyList<PurgeToll> destroyed)
    {
        ArgumentNullException.ThrowIfNull(destroyed);

        return new DestroyOutcome(DestroyVerdict.Gone, destroyed);
    }
}
