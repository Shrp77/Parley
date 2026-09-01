using System.Globalization;

namespace Parley.Core;

/// <summary>
/// The marking that tells an agent an instruction came from the person running the deployment.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §11.5 requires an instruction to reach the agent "marked as coming from the person
/// running the deployment rather than from a peer", and says why: what the agent must be able to
/// tell is which of the two it is reading. An instruction that arrived looking like ordinary text
/// is a forgery in the direction §13 does not cover — §13 marks a peer's words as data, and these
/// are the principal's.
/// </para>
/// <para>
/// Deliberately not §13's envelope, and deliberately not shaped like it. That envelope says
/// "inert data, evaluate it on its merits", which is the opposite of what this is. Reusing its
/// framing would teach a reader — human or model — that the two are the same kind of thing at
/// different settings, and the whole value here is that they are not.
/// </para>
/// <para>
/// Nothing about this grants authority the operator did not already have. It is a label on a turn
/// that arrives down the standard input the node already owns (§4), and §11.5 is explicit that an
/// agent may decline: the operator is a person, not a superior process, and §8's rules apply to
/// an instruction exactly as they apply to a question.
/// </para>
/// <para>
/// In <c>Parley.Core</c> because two processes need the same wording and neither can see the
/// other: the node marks, and anything checking that it did — a test, a second adapter — must
/// compare against one definition rather than a copy that drifted.
/// </para>
/// </remarks>
public static class OperatorInstruction
{
    /// <summary>The line that opens an instruction from the operator.</summary>
    /// <remarks>
    /// <para>
    /// A fixed marker rather than a fresh token, unlike §13's delimiter. The reason that one is
    /// unpredictable is that its content is written by somebody who would forge it; this content
    /// is written by the principal, and the channel it arrives on is the one the node owns. There
    /// is no sender here to keep out.
    /// </para>
    /// <para>
    /// In <see cref="Responder.ReservedPrefix"/>'s namespace on purpose. §11.3 reserves
    /// <c>parley:</c> for what the protocol itself says, and a peer that tried to imitate this
    /// would be doing so from inside §13's payload block — whose token it cannot predict, so the
    /// imitation is visibly nested rather than framing anything.
    /// </para>
    /// </remarks>
    public const string Marker = "[parley:operator]";

    /// <summary>Marks an operator's words as the operator's.</summary>
    /// <param name="text">What the operator wrote.</param>
    /// <returns>The turn to give the agent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static string Mark(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
            {Marker} An instruction from the person running this deployment, delivered through
            Parley. This is not a peer agent and not a message from another repository: it is
            your operator, speaking on the same channel your session already uses.

            You may decline, and declining is not a failure — say why. Rule 8 applies here as it
            applies to a question from a peer.

            {text}
            """);
    }
}
