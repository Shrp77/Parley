using Parley.Server;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// That the process the adapter names on attach reaches the broker, so a person can end a process a node that is gone left
/// running (SPEC.md §11.1).
/// </summary>
/// <remarks>
/// <c>TheAdapterSaysWhichProcessHostsItTests</c> holds what <see cref="HostProcess.Id"/> answers, and the server's tests hold
/// what the broker does with a process id a hand-made attach gives. Nothing held the step between: with the attach sending
/// nothing, every test passed, and every Kill would have been answered "its adapter did not say which process hosts it".
/// </remarks>
[Integration]
public sealed class TheAdapterNamesItsHostToTheBrokerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_process_left_running_can_be_named_from_what_its_adapter_said()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        // A token no current node issued: what a node that is gone leaves behind.
        var options = fixture.OptionsFor("orders@test") with { LaunchToken = Guid.NewGuid().ToString("N") };
        await using var link = await BrokerLink.StartAsync(options, ct);

        Assert.True(fixture.Runtime.Unsupervised(options.Agent), "the attach did not read as left running");

        // No node is joined, so nothing can end it; but it is named, which is what the attach owes.
        Assert.Equal(KillOutcome.NoNode, fixture.Runtime.RequestKill(options.Agent, "review"));
    }
}
