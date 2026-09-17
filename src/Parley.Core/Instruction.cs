namespace Parley.Core;

/// <summary>
/// What became of an instruction the operator gave an agent.
/// </summary>
/// <remarks>
/// <para>
/// Five outcomes because they are five different facts, and an interface that collapsed them
/// would leave the operator guessing at the one thing they asked. SPEC.md §10.2 sets them out:
/// the distinction that matters most is between <see cref="Declined"/> and <see cref="Failed"/>,
/// because §11.5 says an agent may refuse and a console that renders refusal as malfunction
/// teaches whoever reads it to work around the agent rather than ask why.
/// </para>
/// <para>
/// <b><see cref="Pending"/> was the fifth all along and nothing ever produced it.</b> The value was
/// here, the schema had a state for it, and the console had a branch rendering it — but the broker
/// wrote <see cref="Failed"/> when it stopped waiting, so a running turn was reported as one that
/// had been attempted and had not completed. That is a claim nobody is entitled to make while it is
/// still being attempted, and worse, it settled the record: the agent's real answer was refused when
/// it arrived, because settlement is first-come.
/// </para>
/// <para>
/// The values match the <c>outcome</c> column of the <c>instructions</c> table in §9.2. They are
/// a copy, and a copy can drift — but the schema refuses an unknown value outright, so the drift
/// surfaces as a write that fails rather than as a row nobody can read.
/// </para>
/// </remarks>
public enum InstructionOutcome
{
    /// <summary>Issued and not yet settled: the turn is running, and there is no answer yet.</summary>
    /// <remarks>
    /// The one non-terminal outcome, and therefore the only one anything has to be done about. It
    /// is what an operator is told when the broker stops waiting, which is a fact about the broker
    /// rather than about the turn — and it is deliberately not <see cref="Failed"/>, which claims
    /// the turn was attempted and did not complete.
    /// </remarks>
    Pending = 0,

    /// <summary>The agent was given the turn and acted on it.</summary>
    Taken = 1,

    /// <summary>The agent was given the turn and would not act on it.</summary>
    /// <remarks>
    /// Not a failure. The operator is a person rather than a superior process (§11.5), so a
    /// refusal is the agent behaving correctly and must not be reported as a malfunction.
    /// </remarks>
    Declined = 2,

    /// <summary>
    /// Nothing was given: the machine holding the agent is not connected, or the agent is not running there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="Failed"/> because the remedy is different and the operator can
    /// act on it — re-sending changes nothing until something is running to receive it.
    /// </para>
    /// <para>
    /// <b>Two causes, and every surface named only one.</b> A node gives an instruction only to an agent it already holds,
    /// and reports this when it holds none, so the owner instructed an agent that was simply not running and was told its
    /// machine was not connected — beside a node that was. <see cref="InstructionWording.Undeliverable"/> is the one
    /// sentence now.
    /// </para>
    /// </remarks>
    Undeliverable = 3,

    /// <summary>The turn was attempted and did not complete.</summary>
    Failed = 4
}

/// <summary>
/// The words every surface uses for an instruction outcome that is the same fact wherever it is shown.
/// </summary>
/// <remarks>
/// Here rather than on each surface because three of them said the same wrong thing: the command line, the terminal and
/// the web UI each spelled out their own sentence for <see cref="InstructionOutcome.Undeliverable"/>, and all three were
/// corrected at once only because a search found them.
/// </remarks>
public static class InstructionWording
{
    /// <summary>What an operator is told when an instruction reached nothing.</summary>
    public const string Undeliverable =
        "Nothing was delivered: an instruction reaches only an agent its machine is already running, and this one is not " +
        "running, or its machine is not connected. A request to it starts it; instruct it again once it is running.";
}

/// <summary>
/// What an agent did with an instruction, and whatever it said back.
/// </summary>
/// <param name="Outcome">What became of it.</param>
/// <param name="Reply">
/// The agent's own words, or <see langword="null"/> when it never got the turn.
/// </param>
/// <param name="Id">
/// Which instruction this is, so a caller told <see cref="InstructionOutcome.Pending"/> knows where
/// to come back to.
/// </param>
/// <remarks>
/// The reply is kept beside the instruction rather than in the message log (§9.2). It answers a
/// person and not a peer, and filing it among the traffic would make the log claim an exchange
/// that never happened — in a log whose whole worth is that it records what agents said to each
/// other.
/// </remarks>
public sealed record InstructionResult(InstructionOutcome Outcome, string? Reply, long Id = 0)
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

/// <summary>
/// One instruction as an operator reads it back, after the call that issued it has gone.
/// </summary>
/// <param name="Id">Which instruction.</param>
/// <param name="Agent">The agent it was given to.</param>
/// <param name="Text">The operator's own words.</param>
/// <param name="Outcome">What became of it.</param>
/// <param name="Reply">The agent's own words, or <see langword="null"/> if it has not answered.</param>
/// <param name="IssuedUtc">When the operator asked.</param>
/// <param name="SettledUtc">
/// When it stopped being <see cref="InstructionOutcome.Pending"/>, or <see langword="null"/> while
/// it still is.
/// </param>
/// <remarks>
/// <para>
/// <b>Separate from <see cref="InstructionResult"/> because they answer different questions.</b>
/// The result is what became of an instruction you just issued and are still holding; this is a
/// record of one you may not have issued, from a connection that was not there at the time. It
/// carries the text for that reason — a reply with no question beside it is half a transcript.
/// </para>
/// <para>
/// The text is included deliberately and the disclosure was argued rather than defaulted (FR-011).
/// These are the operator's own words to their own agent, on a surface no agent can reach, and
/// §10.2 already discloses whole conversations to the same caller. Withholding a person's words
/// from that person would be a strange place to start being careful.
/// </para>
/// </remarks>
public sealed record InstructionRecord(
    long Id,
    AgentName Agent,
    string Text,
    InstructionOutcome Outcome,
    string? Reply,
    DateTimeOffset IssuedUtc,
    DateTimeOffset? SettledUtc)
{
    /// <summary>Whether the turn is still running.</summary>
    /// <remarks>
    /// Named for the turn rather than for the record's state, because that is the question somebody
    /// reading this is actually asking. <see cref="InstructionOutcome.Pending"/> is the only
    /// non-terminal outcome, so this is the only thing that can change if they look again.
    /// </remarks>
    public bool StillRunning => Outcome == InstructionOutcome.Pending;
}

/// <summary>
/// A bounded page of instructions, and how to ask for the next one.
/// </summary>
/// <param name="Instructions">The instructions, newest first.</param>
/// <param name="NextPageToken">
/// What to pass back for the next page, or <see langword="null"/> when this is the last.
/// </param>
/// <remarks>
/// <para>
/// Bounded by the broker whatever the caller asked for. SPEC.md §10.2 makes rule 8.5's argument one
/// level up: an unbounded operator query is the mistake the message cap exists to prevent, and a
/// caller's number is a caller's number.
/// </para>
/// <para>
/// The token is opaque, for the reason the conversation listing already records: a token whose shape
/// a client parsed would be a shape the broker could not change. Instructions need less of a token
/// than conversations do — they have a monotonic identifier and no ties to break — but inventing a
/// second, more transparent convention for the simpler case buys nothing and costs a rule.
/// </para>
/// </remarks>
public sealed record InstructionPage(
    IReadOnlyList<InstructionRecord> Instructions,
    string? NextPageToken);
