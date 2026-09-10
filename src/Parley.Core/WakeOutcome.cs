namespace Parley.Core;

/// <summary>
/// What became of the attempt to put a message in front of the agent it was addressed to.
/// </summary>
/// <remarks>
/// <para>
/// <b>None of these is a refusal, and that distinction is the whole reason the type exists.</b> A
/// message is stored before any of this is decided, so every member here describes the
/// <em>recipient</em> rather than the send. SPEC.md section 17 question 1 requires that whatever
/// bounds notification traffic "must not make an event undeliverable"; a surface that rendered
/// <see cref="Unreachable"/> as a failure would tell somebody their contract change had not been
/// sent when it had.
/// </para>
/// <para>
/// It exists because a sender told only that the message was stored cannot tell delivery from
/// silence. That was met in use: a notification to an agent nobody had open reported "Sent.",
/// which was true, and left the person believing a peer was reading it.
/// </para>
/// <para>
/// An enum rather than a sentence, deliberately. Everything crossing between agents is charged
/// against the working memory of whoever receives it, and a field that can hold a paragraph
/// eventually holds one. The wire carries the fact; the words belong to the surface that shows
/// them.
/// </para>
/// </remarks>
public enum WakeOutcome
{
    /// <summary>The recipient was not running, and its machine was asked to start it.</summary>
    Started = 0,

    /// <summary>
    /// The recipient was already attached, and was asked to take a turn.
    /// </summary>
    /// <remarks>
    /// Asking is not the same as its happening. A node gives a turn only to a resident of its own,
    /// so an agent attached through somebody's editor window is left alone — nothing can make a
    /// person's window take a turn, which is the case SPEC.md section 17 question 15 was written
    /// about.
    /// </remarks>
    AlreadyRunning = 1,

    /// <summary>
    /// Attention was already pending for the recipient, so nothing further was asked.
    /// </summary>
    /// <remarks>
    /// The coalescing bound, seen from the sender's side. It is not a failure and not a delay: the
    /// turn already coming will collect this message along with everything else pending, because
    /// draining an inbox collects all of it. One turn for sixty notifications is the correct
    /// number rather than a rationed one.
    /// </remarks>
    AlreadyComing = 2,

    /// <summary>
    /// Nothing here can start the recipient: its machine has no node, or that node does not name
    /// the role among what it can launch.
    /// </summary>
    /// <remarks>
    /// Ordinary rather than an error, and the case SPEC.md section 5.5 designs for rather than
    /// around: the message waits, and is collected whenever the agent next runs for any reason.
    /// Said out loud because the alternative is what was reported — a send that looked identical
    /// to one that had reached somebody.
    /// </remarks>
    Unreachable = 3
}
