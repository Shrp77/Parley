using System.Reflection;
using Parley.Server;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>That the restart wake waits longer than an adapter takes to try its broker again (SPEC.md §11.1, §14).</summary>
/// <remarks>
/// <para>
/// The wait exists so a process that outlived the broker attaches again before the wake decides whom to start; shorter
/// than the adapter's retry interval, the wake finds the agent detached and starts a second process in the same checkout
/// (review, lens 1 run 2 on ae1d0e0f). <c>ARestartWakeWaitsForLeftProcessesTests</c> attaches the old process again
/// within milliseconds, so it passed with the wait cut to a tenth of a second (review, lens 3 surface A on d99806d9).
/// </para>
/// <para>
/// The two numbers live in different assemblies, so they are compared here, where both are reachable. The adapter's is
/// private; read by name so a rename fails this test rather than silently passing.
/// </para>
/// </remarks>
[Unit]
public sealed class TheRestartWakeOutwaitsTheAdaptersRetryTests
{
    [Fact]
    public void The_wake_waits_longer_than_an_adapter_takes_to_try_again()
    {
        var field = typeof(BrokerLink).GetField("RetryAfter", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);

        var retry = Assert.IsType<TimeSpan>(field.GetValue(null));

        Assert.True(
            BrokerRuntime.RestartWakeDelayDefault > retry * 2,
            $"the restart wake waits {BrokerRuntime.RestartWakeDelayDefault}, and an adapter tries again every {retry}");
    }
}
