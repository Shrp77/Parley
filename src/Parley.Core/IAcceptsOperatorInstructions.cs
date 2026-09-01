namespace Parley.Core;

/// <summary>
/// What an agent must be able to receive from the operator. The other half of broker V1.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §11.5. An adapter that supports instructing implements this, gives the agent the
/// instruction as a turn marked as coming from the person running the deployment, and reports
/// what became of it.
/// </para>
/// <para>
/// <b>A separate contract from <see cref="IParleyBrokerClient"/>, and that is the entire point of
/// it being separate.</b> §10.2 opens by warning that an adapter surfacing an operator operation
/// into an agent's tool set would breach §10.1, and the protection the document offers is a naming
/// convention — <c>force_conclude</c> deliberately not <c>conclude</c>. That protection is thin,
/// because it depends on whoever writes the adapter reading carefully.
/// </para>
/// <para>
/// One interface holding both halves would defeat it structurally rather than thinly: whatever
/// bound the half an agent needs would bring <c>retire</c>, <c>purge</c> and the rest along with
/// it, and no name would be involved for anybody to notice. So this holds one method, and the
/// test beside it asserts that it still does.
/// </para>
/// <para>
/// <b>§13's envelope is not used here and would be wrong.</b> That envelope exists to mark a
/// peer's words as data. These are the principal's words, and what the agent must be able to tell
/// is which of the two it is reading — an instruction arriving dressed as peer traffic would be a
/// forgery in the direction §13 does not cover.
/// </para>
/// </remarks>
public interface IAcceptsOperatorInstructions
{
    /// <summary>Take an instruction from the operator, and say what came of it.</summary>
    /// <param name="text">What the operator said.</param>
    /// <param name="cancellationToken">Cancels the turn.</param>
    /// <returns>What became of it, and whatever the agent said in reply.</returns>
    /// <remarks>
    /// <para>
    /// It answers. §10.2 has four outcomes to distinguish and only this side knows which, so
    /// silence is not among them: an instruction that returns nothing is indistinguishable from
    /// one that was never delivered, and the operator's next move on that reading is to send it
    /// again.
    /// </para>
    /// <para>
    /// An agent may decline, and returning <see cref="InstructionOutcome.Declined"/> is it
    /// behaving correctly rather than failing. The operator is a person and not a superior
    /// process; §8's rules apply to what an agent does with an instruction exactly as they apply
    /// to a question.
    /// </para>
    /// </remarks>
    Task<InstructionResult> AcceptAsync(string text, CancellationToken cancellationToken = default);
}
