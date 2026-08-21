namespace Parley.Broker;

/// <summary>
/// Why a call to conclude a conversation was refused.
/// </summary>
public enum ClosureError
{
    /// <summary>No refusal.</summary>
    None = 0,

    /// <summary>
    /// The caller is not party to that conversation, or there is no such conversation.
    /// </summary>
    /// <remarks>
    /// The two are deliberately one value. Distinguishing them would let an agent probe which
    /// identifiers exist and who is discussing what, which is exactly the membership disclosure
    /// SPEC.md §5.0 spends most of its length preventing: if <c>billing</c> brought
    /// <c>inventory</c> into a discussion, <c>orders</c> has no business knowing that. An agent
    /// that is genuinely a participant never sees this, because it holds the exchange that made
    /// it one.
    /// </remarks>
    NotAParticipant
}

/// <summary>
/// What a participant learns from concluding: whether the discussion is over, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The shape is the control. SPEC.md §5.0 shares outcomes and withholds tallies, so this
/// carries one boolean and no counts, no roster, and no indication of who has answered or how.
/// A type that could report "three of four agree" would disclose the roster size as surely as
/// naming its members, and the broker holds that count precisely so no participant does.
/// </para>
/// <para>
/// A poll in progress reports <see cref="Concluded"/> as <see langword="false"/>, the same as a
/// discussion nobody has proposed closing. That is not an omission: what a participant needs is
/// whether it may still ask, and both answers to that are the same. An agent calling repeatedly
/// sees a state change and learns that closure was reached — never who reached it, or when they
/// were asked. §5.0 admits that as a genuine side channel and a deliberately narrow one: it
/// discloses aggregate progress on a discussion the caller is already in.
/// </para>
/// </remarks>
public sealed record ClosureOutcome
{
    private ClosureOutcome(bool concluded, ClosureError error)
    {
        Concluded = concluded;
        Error = error;
    }

    /// <summary>Gets a value indicating whether the conversation is now closed.</summary>
    /// <value>
    /// <see langword="true"/> if the discussion has concluded and no further question may name
    /// it; <see langword="false"/> if it remains open, whether or not a poll is running.
    /// </value>
    public bool Concluded { get; }

    /// <summary>Gets why the call was refused.</summary>
    /// <value><see cref="ClosureError.None"/> when it was not.</value>
    public ClosureError Error { get; }

    /// <summary>Gets a value indicating whether the call was accepted.</summary>
    public bool Succeeded => Error == ClosureError.None;

    /// <summary>Creates the outcome for an accepted call.</summary>
    /// <param name="concluded">Whether the conversation is now closed.</param>
    /// <returns>An outcome reporting the state.</returns>
    internal static ClosureOutcome Recorded(bool concluded) =>
        new(concluded, ClosureError.None);

    /// <summary>Creates the outcome for a caller that is not in the conversation.</summary>
    /// <returns>A refusal carrying nothing.</returns>
    internal static ClosureOutcome NotAParticipant() =>
        new(concluded: false, ClosureError.NotAParticipant);
}
