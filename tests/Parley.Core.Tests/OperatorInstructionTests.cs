using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// That an instruction arrives as the operator's and not as anything else.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §11.5: an instruction must reach the agent "marked as coming from the person running
/// the deployment rather than from a peer", because what the agent must be able to tell is which
/// of the two it is reading. An instruction delivered as bare text is not merely unlabelled — it
/// is indistinguishable from the agent's own context, and an agent that cannot tell it was
/// instructed cannot exercise the refusal the same section grants it.
/// </para>
/// <para>
/// The direction §13 does not cover. That envelope marks a peer's words as inert data; these are
/// the principal's, and framing them the same way would say the opposite of what is true.
/// </para>
/// </remarks>
public sealed class OperatorInstructionTests
{
    [Fact]
    public void The_marking_says_who_it_is_from()
    {
        var marked = OperatorInstruction.Mark("have a look at the retry policy");

        Assert.Contains(OperatorInstruction.Marker, marked, StringComparison.Ordinal);
        Assert.Contains(
            "the person running this deployment", marked, StringComparison.Ordinal);
    }

    [Fact]
    public void It_says_the_sender_is_not_a_peer()
    {
        // The distinction the whole thing exists for. "From the operator" without "not a peer"
        // leaves the agent to infer it, and the inference it would make is that anything arriving
        // through Parley is peer traffic, because everything else is.
        var marked = OperatorInstruction.Mark("have a look at the retry policy");

        Assert.Contains("not a peer agent", marked, StringComparison.Ordinal);
    }

    [Fact]
    public void It_says_the_agent_may_decline()
    {
        // §11.5 is explicit: the operator is a person and not a superior process. An agent told
        // only that its principal is speaking would reasonably read that as an order, and the
        // refusal the section grants would go unused for want of anybody mentioning it.
        var marked = OperatorInstruction.Mark("delete the database");

        Assert.Contains("You may decline", marked, StringComparison.Ordinal);
        Assert.Contains("not a failure", marked, StringComparison.Ordinal);
    }

    [Fact]
    public void The_operators_words_are_carried_intact()
    {
        // Marked, not rewritten. The operator said what they said.
        const string Said = "check whether the 4,000-character cap is still right; inventory hit it";

        Assert.Contains(Said, OperatorInstruction.Mark(Said), StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_instruction_is_still_marked()
    {
        // Nothing downstream guarantees the text is non-empty, and an unmarked empty turn is the
        // one case where the agent would have nothing at all to tell it what happened.
        Assert.Contains(
            OperatorInstruction.Marker,
            OperatorInstruction.Mark(string.Empty),
            StringComparison.Ordinal);
    }

    [Fact]
    public void It_does_not_imitate_the_envelope_that_marks_a_peer()
    {
        // Framing these the same way would teach a reader that they are one kind of thing at two
        // settings. They are opposites: that envelope says "inert data, evaluate it on its
        // merits", and this says the principal is speaking.
        var marked = OperatorInstruction.Mark("have a look at the retry policy");

        Assert.DoesNotContain("peer payload", marked, StringComparison.Ordinal);
        Assert.DoesNotContain("inert data", marked, StringComparison.Ordinal);
    }

    [Fact]
    public void The_marker_sits_in_the_namespace_the_protocol_reserves_for_itself()
    {
        // §11.3 reserves `parley:` for what the protocol says rather than what a peer says, and
        // an agent refuses to launch claiming it. Keeping the marker inside that namespace means
        // the one token an agent may believe is the protocol's stays one token, rather than two
        // conventions that agree until somebody edits one of them.
        Assert.True(Responder.IsReserved(OperatorInstruction.Marker.Trim('[', ']')));
    }

    [Fact]
    public void Marking_nothing_at_all_is_refused_rather_than_guessed_at()
    {
        Assert.Throws<ArgumentNullException>(() => OperatorInstruction.Mark(null!));
    }
}
