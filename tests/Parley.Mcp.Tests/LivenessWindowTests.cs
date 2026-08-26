using Parley.Core;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// The windows liveness is derived from, and the two numbers that have to agree about them.
/// </summary>
public sealed class LivenessWindowTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_cadence_readers_assume_is_the_cadence_servers_keep()
    {
        // Two constants in two assemblies naming the same fact, which is the shape that drifts.
        // A reader cannot see another agent's launch environment -- the registry does not record
        // it -- so it assumes the default, and the server is what actually keeps it.
        //
        // Left unpinned, raising the tick to thirty seconds would leave every reader still
        // assuming five, and healthy agents sweeping happily on schedule would be reported
        // NotResponding fifteen seconds later. Nothing would fail; the registry would just start
        // lying, which is the defect this whole change exists to remove.
        Assert.Equal(ServerOptions.DefaultTickInterval, AgentLiveness.DefaultSweepInterval);
    }

    [Theory]
    [InlineData(0, AgentStatus.Live)]
    [InlineData(14, AgentStatus.Live)]
    [InlineData(15, AgentStatus.Live)]
    [InlineData(16, AgentStatus.NotResponding)]
    [InlineData(299, AgentStatus.NotResponding)]
    [InlineData(300, AgentStatus.NotResponding)]
    [InlineData(301, AgentStatus.Unreachable)]
    public void Each_boundary_falls_where_it_is_documented(int silentSeconds, AgentStatus expected)
    {
        // Both edges pinned to the second, and inclusively: at exactly three sweeps an agent is
        // still live, and at exactly five minutes it is still merely not responding. Off-by-one
        // here is invisible in every other test, because they all sit far from the edge.
        Assert.Equal(
            expected,
            AgentLiveness.Derive(
                AgentStatus.Live,
                T0,
                T0.AddSeconds(silentSeconds),
                AgentLiveness.DefaultSweepInterval));
    }

    [Fact]
    public void The_window_scales_with_the_cadence_it_is_given()
    {
        // The doubt window is three sweeps rather than a fixed number of seconds, so a
        // deployment that sweeps slowly is not written off for keeping its own schedule.
        var slow = TimeSpan.FromSeconds(60);

        Assert.Equal(
            AgentStatus.Live,
            AgentLiveness.Derive(AgentStatus.Live, T0, T0.AddSeconds(120), slow));

        Assert.Equal(
            AgentStatus.NotResponding,
            AgentLiveness.Derive(AgentStatus.Live, T0, T0.AddSeconds(200), slow));
    }

    [Fact]
    public void Retirement_outranks_a_heartbeat_from_one_second_ago()
    {
        // An operator who retires an agent whose session is still running has retired it. A
        // derivation that let a fresh timestamp win would be the control-that-does-not-hold §14
        // describes, arriving by a new route.
        Assert.Equal(
            AgentStatus.Retired,
            AgentLiveness.Derive(AgentStatus.Retired, T0, T0.AddSeconds(1), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Never_seen_outranks_the_age_rules()
    {
        // An agent with no timestamp has not been silent for five minutes; it has not started.
        // Reporting it Unreachable would refuse the traffic meant to wake it.
        Assert.Equal(
            AgentStatus.Offline,
            AgentLiveness.Derive(AgentStatus.Offline, null, T0.AddYears(1), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void A_clock_that_went_backwards_reads_live_rather_than_throwing()
    {
        // Two machines writing to one file need not agree to the millisecond. A negative silence
        // means the agent demonstrably wrote recently, which is the question being asked.
        Assert.Equal(
            AgentStatus.Live,
            AgentLiveness.Derive(
                AgentStatus.Live, T0.AddMinutes(5), T0, TimeSpan.FromSeconds(5)));
    }
}
