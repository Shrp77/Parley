using Parley.Client.Seating;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Client.Tests;

/// <summary>
/// That a person sending a notification learns whether anything is going to read it.
/// </summary>
/// <remarks>
/// <para>
/// <b>"Sent." on its own is what produced the report this feature answers.</b> It was true, and it
/// left somebody believing a peer was reading a notification that had reached a closed session and
/// started nothing. The wake is half the fix; this is the other half, and it would still be worth
/// having if the wake had not been built — a send that delivers and a send that lands in an empty
/// room must not read identically.
/// </para>
/// <para>
/// Written once and read by both surfaces, because both compose through <see cref="SeatVoice"/>.
/// Two implementations of a report about the same send would disagree about what was sent.
/// </para>
/// </remarks>
[Unit]
public sealed class WakeReportingTests
{
    private static AgentName Orders => AgentName.Create("orders@test");

    public static TheoryData<Wire.WakeOutcome> Outcomes =>
        new()
        {
            Wire.WakeOutcome.Started,
            Wire.WakeOutcome.AlreadyRunning,
            Wire.WakeOutcome.AlreadyComing,
            Wire.WakeOutcome.Unreachable,
        };

    [Theory]
    [InlineData(Wire.WakeOutcome.Started, true)]
    [InlineData(Wire.WakeOutcome.AlreadyRunning, true)]
    [InlineData(Wire.WakeOutcome.AlreadyComing, true)]
    [InlineData(Wire.WakeOutcome.Unreachable, false)]
    [InlineData(Wire.WakeOutcome.OpenOutside, false)]
    public void A_send_goes_on_unaided_unless_nothing_can_give_its_receiver_a_turn(Wire.WakeOutcome outcome, bool unaided)
    {
        Assert.Equal(unaided, SeatVoice.GoesOnUnaided("Sent. Exchange 1 is open. " + SeatVoice.Waking(outcome, Orders)));
    }

    [Theory]
    [InlineData(Wire.WakeOutcome.Started)]
    [InlineData(Wire.WakeOutcome.AlreadyComing)]
    [InlineData(Wire.WakeOutcome.Unreachable)]
    [InlineData(Wire.WakeOutcome.OpenOutside)]
    [InlineData(Wire.WakeOutcome.WithThePerson)]
    public void Only_an_agent_already_running_is_listed_as_running(Wire.WakeOutcome outcome)
    {
        // Without a word of its own for each outcome in Reached, the Inbox's list of agents asked said "Running" of an
        // agent that could not be started, or was only being started, and the person waited on nothing (review, lens 3
        // round 32).
        var said = SeatVoice.Reached(outcome);

        Assert.DoesNotContain("Running", said, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(SeatVoice.Reached(Wire.WakeOutcome.Unspecified), said);
    }

    [Fact]
    public void A_refused_send_does_not_go_on_unaided()
    {
        Assert.False(SeatVoice.GoesOnUnaided("That was refused."));
    }

    [Theory]
    [MemberData(nameof(Outcomes))]
    public void Every_outcome_names_the_recipient(Wire.WakeOutcome outcome)
    {
        // A person telling one agent knows who they told; a person telling nineteen does not, and
        // these sentences are what a broadcast aggregates. A row that said "it is being started"
        // would be unusable there.
        Assert.Contains(Orders.Value, SeatVoice.Waking(outcome, Orders), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Outcomes))]
    public void No_outcome_reads_as_a_failure_to_send(Wire.WakeOutcome outcome)
    {
        // This is the requirement most easily lost to a rewording. The message is stored
        // before any of this is decided, and SPEC.md §17 question 1 requires that whatever bounds
        // notification traffic must not make an event undeliverable — so a surface that said
        // "could not send" about a recipient nothing can start would be reporting a contract change
        // as lost when it is sitting in an inbox.
        var said = SeatVoice.Waking(outcome, Orders);

        foreach (var wrong in new[] { "not sent", "failed", "could not send", "refused", "lost" })
        {
            Assert.DoesNotContain(wrong, said, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [MemberData(nameof(Outcomes))]
    public void Every_outcome_says_something_different(Wire.WakeOutcome outcome)
    {
        var said = SeatVoice.Waking(outcome, Orders);

        Assert.False(string.IsNullOrWhiteSpace(said));

        var others = Enum.GetValues<Wire.WakeOutcome>()
            .Where(other => other != outcome && other != Wire.WakeOutcome.Unspecified)
            .Select(other => SeatVoice.Waking(other, Orders));

        Assert.DoesNotContain(said, others, StringComparer.Ordinal);
    }

    [Fact]
    public void An_unreachable_recipient_is_told_the_request_is_kept()
    {
        // Worded so that it is actionable rather than alarming: the thing
        // to know is that the message survives and who is not going to read it yet.
        var said = SeatVoice.Waking(Wire.WakeOutcome.Unreachable, Orders);

        Assert.Contains("collects this when it next runs", said, StringComparison.Ordinal);
        Assert.Contains(Orders.Value, said, StringComparison.Ordinal);
        Assert.DoesNotContain("failed", said, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_agent_open_outside_Parley_is_not_blamed_on_an_editor()
    {
        // The broker answers OpenOutside for a session somebody opened, and LeftRunning for a process a node that is gone
        // left running (SPEC.md sections 11.1 and 11.7), so this sentence is about an editor's session alone.
        var said = SeatVoice.Waking(Wire.WakeOutcome.OpenOutside, Orders);

        Assert.Contains(Orders.Value + " is open outside Parley, so Parley cannot give it a turn", said, StringComparison.Ordinal);
        Assert.Contains("prompts it where it is open", said, StringComparison.Ordinal);
        Assert.DoesNotContain("editor", said, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_outcome_this_build_does_not_know_is_not_guessed_at()
    {
        // A broker older than this one, which the protocol check on attach makes unreachable — but
        // claiming a start that was never asked for is precisely the failure these sentences exist
        // to remove, so the unknown case says it does not know.
        var said = SeatVoice.Waking(Wire.WakeOutcome.Unspecified, Orders);

        Assert.Contains("not reported", said, StringComparison.Ordinal);
    }
}
