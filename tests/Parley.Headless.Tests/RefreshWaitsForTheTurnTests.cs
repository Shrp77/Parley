using Parley.Core;
using Xunit;

namespace Parley.Headless.Tests;

/// <summary>
/// That refreshing an agent waits for its turn to end and never stops it mid-work (SPEC.md §11.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>The one promise the refresh rests on.</b> The broker cannot see a turn in progress and must
/// not learn to; it sends a refresh only when the agent owes nothing, and the node keeps the rest of the promise by
/// waiting on the turn lock. A refresh that did not wait would discard whatever the agent was halfway through, for the
/// sake of one sentence about what it owns.
/// </para>
/// <para>
/// Driven with a real stand-in process that takes three seconds over its turn, and watched to fail against a
/// <see cref="ResidentAgent.ShutdownWhenIdleAsync"/> that did not take the lock: the turn then comes back died.
/// </para>
/// </remarks>
[Integration]
public sealed class RefreshWaitsForTheTurnTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "parley-refresh-" + Guid.NewGuid().ToString("N"));

    public RefreshWaitsForTheTurnTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_refresh_sent_during_a_turn_stops_the_agent_only_after_the_turn_ends()
    {
        await using var agent = ResidentAgent.Start(
            AgentName.Create("billing@test"),
            InvocationCommand.Parse(StubRunner.Answering(_workspace, delaySeconds: 3)),
            _workspace,
            TimeProvider.System);

        var turn = agent.TakeTurnAsync("do the work", TimeSpan.FromSeconds(30), Ct);

        // Long enough for the turn to hold the lock; well short of the stub's three seconds.
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);

        // **A grace far shorter than the turn, and that is what makes this a test.** Closing a runner's input does not end
        // its turn -- the stand-in finishes the line it is on -- so with a generous grace a refresh that ignored the turn
        // would still pass here. With 200 ms, one that did not wait kills the agent halfway through its work.
        var stopped = agent.ShutdownWhenIdleAsync(TimeSpan.FromMilliseconds(200), Ct);

        var finished = await turn;

        Assert.True(finished.Succeeded, finished.Describe());
        Assert.False(finished.ProcessDied);

        Assert.True(await stopped, "the agent did not exit on its own once told");
        Assert.False(agent.IsAlive);
    }

    [Fact]
    public async Task No_turn_begins_between_the_wait_and_the_stop()
    {
        // A turn asked for after the refresh took the lock finds the process gone rather than being started and then
        // killed halfway through.
        await using var agent = ResidentAgent.Start(
            AgentName.Create("billing@test"),
            InvocationCommand.Parse(StubRunner.Answering(_workspace)),
            _workspace,
            TimeProvider.System);

        var stopped = agent.ShutdownWhenIdleAsync(TimeSpan.FromSeconds(10), Ct);
        var after = await agent.TakeTurnAsync("too late", TimeSpan.FromSeconds(30), Ct);

        Assert.True(await stopped);
        Assert.True(after.ProcessDied, after.Describe());
    }
}
