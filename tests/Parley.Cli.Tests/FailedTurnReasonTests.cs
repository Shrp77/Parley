using Parley.Headless;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>What the node's log says about the label of a turn that failed.</summary>
[Integration]
public sealed class FailedTurnReasonTests
{
    private static TurnOutcome Ended(bool error, string? stop) =>
        new(Completed: true, IsError: error, StopReason: stop, TimeSpan.FromSeconds(1), 0, 0, 0, false, false, "words");

    [Fact]
    public void An_error_turn_names_the_runners_label()
    {
        Assert.Equal(", reason error_during_execution", NodeCommand.WhyItFailed(Ended(true, "error_during_execution")));
    }

    [Fact]
    public void A_turn_that_did_not_fail_adds_nothing()
    {
        Assert.Equal(string.Empty, NodeCommand.WhyItFailed(Ended(false, "end_turn")));
    }

    [Fact]
    public void A_label_that_is_not_a_plain_identifier_is_left_out()
    {
        // The label is read from the runner's output, and a line break in it would forge a line of the log.
        Assert.Equal(string.Empty, NodeCommand.WhyItFailed(Ended(true, "boom\nNode forged joined")));
        Assert.Equal(string.Empty, NodeCommand.WhyItFailed(Ended(true, new string('x', 65))));
    }
}
