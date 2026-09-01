namespace Parley.Core;

/// <summary>
/// What became of an instruction the operator gave an agent.
/// </summary>
/// <remarks>
/// <para>
/// Four outcomes because they are four different facts, and an interface that collapsed them
/// would leave the operator guessing at the one thing they asked. SPEC.md §10.2 sets them out:
/// the distinction that matters most is between <see cref="Declined"/> and <see cref="Failed"/>,
/// because §11.5 says an agent may refuse and a console that renders refusal as malfunction
/// teaches whoever reads it to work around the agent rather than ask why.
/// </para>
/// <para>
/// The values match the <c>outcome</c> column of the <c>instructions</c> table in §9.2. They are
/// a copy, and a copy can drift — but the schema refuses an unknown value outright, so the drift
/// surfaces as a write that fails rather than as a row nobody can read.
/// </para>
/// </remarks>
public enum InstructionOutcome
{
    /// <summary>Issued, and not yet settled: the agent has not been given the turn.</summary>
    Pending = 0,

    /// <summary>The agent was given the turn and acted on it.</summary>
    Taken = 1,

    /// <summary>The agent was given the turn and would not act on it.</summary>
    /// <remarks>
    /// Not a failure. The operator is a person rather than a superior process (§11.5), so a
    /// refusal is the agent behaving correctly and must not be reported as a malfunction.
    /// </remarks>
    Declined = 2,

    /// <summary>Nothing was given: the machine holding the agent is not connected.</summary>
    /// <remarks>
    /// Distinct from <see cref="Failed"/> because the remedy is different and the operator can
    /// act on it — the machine is off, and no amount of re-sending will change that.
    /// </remarks>
    Undeliverable = 3,

    /// <summary>The turn was attempted and did not complete.</summary>
    Failed = 4
}

/// <summary>
/// What an agent did with an instruction, and whatever it said back.
/// </summary>
/// <param name="Outcome">What became of it.</param>
/// <param name="Reply">
/// The agent's own words, or <see langword="null"/> when it never got the turn.
/// </param>
/// <remarks>
/// The reply is kept beside the instruction rather than in the message log (§9.2). It answers a
/// person and not a peer, and filing it among the traffic would make the log claim an exchange
/// that never happened — in a log whose whole worth is that it records what agents said to each
/// other.
/// </remarks>
public sealed record InstructionResult(InstructionOutcome Outcome, string? Reply)
{
    /// <summary>Whether the agent was given the turn at all.</summary>
    /// <remarks>
    /// Deliberately not called <c>Succeeded</c>. A declined instruction reached the agent and was
    /// considered, which is a different thing from one that never arrived, and a property named
    /// for success would invite a console to render the two the same way.
    /// </remarks>
    public bool ReachedTheAgent =>
        Outcome is InstructionOutcome.Taken or InstructionOutcome.Declined;
}
