using Parley.Core;
using Xunit;

namespace Parley.Headless.Tests;

/// <summary>
/// That two turns asked of one resident agent at once are taken one after the other.
/// </summary>
/// <remarks>
/// <para>
/// A node gives a turn whenever it is asked and does not know whether one is already running: the broker asks again
/// for mail a limited collection left (lens 1 run 1.18's fix), and an agent collecting usually still has its turn open
/// when it does. Two turns interleaved on one process's standard input would read each other's replies, and two
/// sessions working one repository at once is the hazard the whole resident design exists to avoid.
/// </para>
/// <para>
/// <see cref="ResidentAgent.TakeTurnAsync"/> takes a per-agent lock, which its remarks said and nothing checked. Pinned
/// by timing against a stub that answers each turn after a fixed delay: taken in order, the second completes a whole
/// delay after the first; interleaved, the two complete together.
/// </para>
/// </remarks>
public sealed class OverlappingTurnsTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private const int DelaySeconds = 2;

    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "parley-overlap-" + Guid.NewGuid().ToString("N"));

    public OverlappingTurnsTests() => Directory.CreateDirectory(_workspace);

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

    [Fact]
    public async Task Two_turns_asked_for_at_once_are_taken_one_after_the_other()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var agent = ResidentAgent.Start(
            AgentName.Create("billing@test"),
            InvocationCommand.Parse(StubRunner.Answering(_workspace, delaySeconds: DelaySeconds)),
            _workspace,
            TimeProvider.System);

        var started = DateTime.UtcNow;

        async Task<(TurnOutcome Outcome, TimeSpan At)> Turn(string prompt)
        {
            var outcome = await agent.TakeTurnAsync(prompt, Patience, ct);
            return (outcome, DateTime.UtcNow - started);
        }

        var both = await Task.WhenAll(Turn("first"), Turn("second"));
        var ordered = both.OrderBy(turn => turn.At).ToArray();

        Assert.All(both, turn => Assert.True(turn.Outcome.Completed, turn.Outcome.Describe()));
        Assert.True(agent.IsAlive);

        var gap = ordered[1].At - ordered[0].At;

        Assert.True(
            gap > TimeSpan.FromSeconds(DelaySeconds * 0.75),
            $"the turns completed {gap.TotalSeconds:0.0}s apart at {ordered[0].At.TotalSeconds:0.0}s and " +
            $"{ordered[1].At.TotalSeconds:0.0}s; taken in order they are a whole {DelaySeconds}s delay apart");
    }
}
