using Parley.Client.Seating;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Client.Tests;

/// <summary>That every wake outcome the broker can answer has words of its own on the person's surfaces.</summary>
/// <remarks>
/// <see cref="WakeReportingTests"/> names four outcomes, so an outcome added later and missed from a switch fell to "not
/// reported" with every test green: with the left running arm removed from <see cref="SeatVoice.Waking"/> or
/// <see cref="SeatVoice.Reached"/>, or its count from a broadcast's report, a person was told nothing of a receiver that a
/// node that is gone left running, and that it is started once that process ends (review, lens 3 run A on 5de89187). The
/// same run found <see cref="SeatVoice.Waking"/> has no arm for a frozen receiver at all, so a person's Request to a
/// frozen agent read "What became of it was not reported".
/// </remarks>
[Unit]
public sealed class EveryWakeOutcomeHasItsOwnWordsTests
{
    private static AgentName Orders => AgentName.Create("orders@test");

    public static TheoryData<Wire.WakeOutcome> Answered =>
        [.. Enum.GetValues<Wire.WakeOutcome>().Where(outcome => outcome != Wire.WakeOutcome.Unspecified)];

    [Theory]
    [MemberData(nameof(Answered))]
    public void A_reported_outcome_is_never_said_as_not_reported(Wire.WakeOutcome outcome)
    {
        Assert.NotEqual(SeatVoice.Waking(Wire.WakeOutcome.Unspecified, Orders), SeatVoice.Waking(outcome, Orders));
        Assert.NotEqual(SeatVoice.Reached(Wire.WakeOutcome.Unspecified), SeatVoice.Reached(outcome));
    }

    [Fact]
    public void A_receiver_left_running_is_said_to_be_started_once_that_process_ends()
    {
        var said = SeatVoice.Waking(Wire.WakeOutcome.LeftRunning, Orders);

        Assert.Contains(Orders.Value, said, StringComparison.Ordinal);
        Assert.Contains("started for this once that process ends", said, StringComparison.Ordinal);

        // Not an editor's session: nobody will prompt it.
        Assert.DoesNotContain("prompts it", said, StringComparison.Ordinal);
        Assert.Contains("once that process ends", SeatVoice.Reached(Wire.WakeOutcome.LeftRunning), StringComparison.Ordinal);
    }

    [Fact]
    public void A_request_to_everybody_counts_the_receivers_left_running()
    {
        var said = SeatVoice.Reported(
            3,
            3,
            stopped: false,
            new Dictionary<Wire.WakeOutcome, int> { [Wire.WakeOutcome.Started] = 1, [Wire.WakeOutcome.LeftRunning] = 2 },
            [],
            []);

        Assert.Contains("2 left running, started for it once that process ends", said, StringComparison.Ordinal);
    }

    // Review, lens 3 run A on 81b6a1a7: with the frozen count left out of a broadcast's report every test stayed green, and a
    // person who sent to everybody was told nothing of the receivers that will not read it until they are unfrozen.
    [Fact]
    public void A_request_to_everybody_counts_the_receivers_that_are_frozen()
    {
        var said = SeatVoice.Reported(
            3,
            3,
            stopped: false,
            new Dictionary<Wire.WakeOutcome, int> { [Wire.WakeOutcome.Started] = 1, [Wire.WakeOutcome.Frozen] = 2 },
            [],
            []);

        Assert.Contains("2 frozen, reading it once unfrozen", said, StringComparison.Ordinal);
    }
}
