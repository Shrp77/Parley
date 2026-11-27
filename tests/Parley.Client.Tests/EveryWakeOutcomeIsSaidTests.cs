using Parley.Client.Seating;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Client.Tests;

/// <summary>That the person is told what became of every receiver the broker reports on (SPEC.md §5).</summary>
/// <remarks>
/// A frozen receiver fell to "was not reported", put out of the way as if it would answer unaided: the broker had said
/// frozen, and nothing answers until the person unfreezes it (review, lens 3 run B on 5de89187).
/// </remarks>
[Unit]
public sealed class EveryWakeOutcomeIsSaidTests
{
    private static AgentName Orders => AgentName.Create("orders@test");

    public static TheoryData<Wire.WakeOutcome> EveryOutcome =>
        new(Enum.GetValues<Wire.WakeOutcome>().Where(outcome => outcome != Wire.WakeOutcome.Unspecified));

    [Theory]
    [MemberData(nameof(EveryOutcome))]
    public void Every_outcome_the_broker_gives_is_said_and_not_called_unreported(Wire.WakeOutcome outcome)
    {
        Assert.NotEqual(SeatVoice.Waking(Wire.WakeOutcome.Unspecified, Orders), SeatVoice.Waking(outcome, Orders));
    }

    [Fact]
    public void A_frozen_receiver_is_said_to_be_frozen_and_kept_in_front_of_the_person()
    {
        var said = SeatVoice.Waking(Wire.WakeOutcome.Frozen, Orders);

        Assert.Contains("frozen", said, StringComparison.Ordinal);
        Assert.Contains("unfreeze", said, StringComparison.Ordinal);
        Assert.False(SeatVoice.GoesOnUnaided("Sent. " + said));
    }

    [Fact]
    public void A_request_to_everybody_counts_the_receivers_frozen()
    {
        // The count was added with the frozen words (0.64.39) and no test read it: with it removed, a broadcast to two frozen
        // agents said nothing of them (review, lens 3 run C on 81b6a1a7).
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
