using System.Reflection;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Mcp.Tests;

/// <summary>That an agent sending a Request is told, for every wake outcome the broker answers, what became of its receiver.</summary>
/// <remarks>
/// <para>
/// No test read <c>receiver_is</c> for any outcome, so the left running arm could be removed from the adapter's words and an
/// agent whose receiver a node that is gone left running was told "unknown", with every test green (review, lens 3 run A
/// on 5de89187). "Unknown" leaves the agent to guess whether anybody will read its request.
/// </para>
/// <para>
/// Read through reflection because the words are a private detail of the tool's reply and a whole broker, node and left
/// process to reach each outcome would be testing the fixture; what is held is that no answered outcome falls through.
/// </para>
/// </remarks>
[Unit]
public sealed class EveryWakeOutcomeIsToldToTheSenderTests
{
    public static TheoryData<Wire.WakeOutcome> Answered =>
        [.. Enum.GetValues<Wire.WakeOutcome>().Where(outcome => outcome != Wire.WakeOutcome.Unspecified)];

    private static string Woken(Wire.WakeOutcome outcome)
    {
        var method = typeof(ParleyTools).GetMethod("Woken", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        return Assert.IsType<string>(method.Invoke(null, [outcome]));
    }

    [Theory]
    [MemberData(nameof(Answered))]
    public void An_answered_outcome_is_never_told_as_unknown(Wire.WakeOutcome outcome)
    {
        var said = Woken(outcome);

        Assert.NotEqual(Woken(Wire.WakeOutcome.Unspecified), said);

        var others = Enum.GetValues<Wire.WakeOutcome>().Where(other => other != outcome).Select(Woken);

        Assert.DoesNotContain(said, others, StringComparer.Ordinal);
    }

    [Fact]
    public void A_receiver_left_running_is_said_to_be_started_once_that_process_ends()
    {
        Assert.Contains("started for your request once that process ends", Woken(Wire.WakeOutcome.LeftRunning), StringComparison.Ordinal);
    }
}
