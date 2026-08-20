namespace Parley.Core;

/// <summary>
/// What a restart does to the exchanges it interrupted.
/// </summary>
/// <remarks>
/// <para>
/// A protocol concept rather than a hosting detail, which is why it lives here beside
/// <see cref="ExchangeState"/> and <see cref="Resolution"/>. SPEC.md §14 requires an
/// implementation to support all three values; where the decision is applied is the broker's
/// business, but what the decision <em>is</em> belongs to the protocol.
/// </para>
/// <para>
/// This exists because two restarts that are byte-identical to the broker carry opposite
/// intent, and it cannot tell them apart. An operator watching two agents burn through a quota
/// kills the process <em>specifically to stop them</em>, and resuming on restart overrides the
/// one control they reached for. An unattended machine takes a forced update at the weekend,
/// and silently dropping the exchanges wastes the whole window.
/// </para>
/// <para>
/// Nothing in the restart itself distinguishes those, so the broker must not infer. See
/// SPEC.md §14.
/// </para>
/// </remarks>
public enum ResumePolicy
{
    /// <summary>
    /// Orphans are retained but never replayed.
    /// </summary>
    /// <remarks>
    /// The safe default for interactive use, because the surprising outcome is work resuming
    /// that somebody stopped on purpose. Orphans stay visible and resumable by explicit operator
    /// action until the grace period expires and they are ghosted, which bounds how long that
    /// window stays open.
    /// </remarks>
    Never = 0,

    /// <summary>
    /// Orphaned exchanges are replayed, subject to the resume gate.
    /// </summary>
    /// <remarks>
    /// The sensible default for unattended use, where nobody is present to resume anything by
    /// hand and the alternative is discarding a weekend of work.
    /// </remarks>
    Always,

    /// <summary>
    /// The broker surfaces the pending exchanges and replays only those confirmed.
    /// </summary>
    /// <remarks>
    /// Requires somebody to ask, so a deployment choosing this must have an operator surface to
    /// ask through. A broker configured this way with nobody listening behaves as
    /// <see cref="Never"/> until the grace period runs out, which is the safe way for it to
    /// degrade rather than an accident.
    /// </remarks>
    Ask
}
