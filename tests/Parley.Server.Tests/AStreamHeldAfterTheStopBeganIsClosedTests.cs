using Xunit;

namespace Parley.Server.Tests;

/// <summary>
/// An attach or a join that arrives after the broker began stopping is closed at once, not held.
/// </summary>
/// <remarks>
/// The host closes every held stream once, when it begins stopping (<see cref="BrokerRuntime.CloseStreams"/>). An adapter
/// that attaches again while the host is still stopping (during the web UI's grace, say) was held by its handler, and
/// the host waited Kestrel's whole shutdown timeout for it: thirty seconds a project, so three projects outlived a
/// supervisor's ninety and were killed (review, lens 1 run B on 4b802112).
/// </remarks>
[Integration]
public sealed class AStreamHeldAfterTheStopBeganIsClosedTests
{
    private static readonly TimeSpan Promptly = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task An_attach_after_the_stop_began_is_closed_promptly()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        broker.Runtime.CloseStreams();

        await using var late = await AttachedClient.AttachAsync(broker, "orders@alpha", "C:/repo-orders", token);

        Assert.True(
            await Within(() => !late.StillAttached, token),
            "An attach after the stop began was still held.");
    }

    [Fact]
    public async Task A_join_after_the_stop_began_is_closed_promptly()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        broker.Runtime.CloseStreams();

        await using var late = await JoinedNodeClient.JoinAsync(broker, "alpha", ["orders"], token);

        Assert.True(
            await Within(() => !late.StillJoined, token),
            "A join after the stop began was still held.");
    }

    private static async Task<bool> Within(Func<bool> done, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + Promptly;

        while (DateTime.UtcNow < deadline)
        {
            if (done())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), token);
        }

        return done();
    }
}
