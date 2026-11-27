using System.Globalization;
using Parley.Core;
using Xunit;

namespace Parley.Headless.Tests;

/// <summary>
/// A prompt goes to the agent whole or not at all, whenever its caller cancels.
/// </summary>
/// <remarks>
/// Lens 1 run 129: the caller's cancel reached the write. Standard input flushes on every write, so a cancel seen by the
/// flush came after the line had gone, and the turn ended uncounted: the agent answered a prompt nobody was owed, and
/// every turn after read the reply before its own.
/// </remarks>
[Integration]
public sealed class APromptIsSentWholeTests : IDisposable
{
    /// <summary>How many cancelled turns are tried.</summary>
    /// <remarks>The unfixed code failed within 5 to 25 rounds of the probe this test is.</remarks>
    private const int Rounds = 300;

    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "parley-resident-" + Guid.NewGuid().ToString("N"));

    public APromptIsSentWholeTests() => Directory.CreateDirectory(_workspace);

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

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private ResidentAgent Start(string template) =>
        ResidentAgent.Start(
            AgentName.Create("billing@test"),
            InvocationCommand.Parse(template),
            _workspace,
            TimeProvider.System);

    [Fact]
    public async Task A_cancel_at_any_moment_leaves_the_next_turn_reading_its_own_reply()
    {
        // Each round cancels a turn at a random moment around its write, then asks a turn that is not cancelled for a
        // number of its own. A reply read one behind carries the cancelled turn's number instead.
        await using var agent = Start(StubRunner.Echoing(_workspace));

        for (var round = 1; round <= Rounds; round++)
        {
            using (var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct))
            {
                var spin = Random.Shared.Next(0, 3000);
                var cancelling = Task.Run(
                    () =>
                    {
                        Thread.SpinWait(spin);
                        stopping.Cancel();
                    },
                    Ct);

                try
                {
                    _ = await agent.TakeTurnAsync(Number(round), Patience, stopping.Token);
                }
                catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
                {
                }

                await cancelling;
            }

            var own = 100_000 + round;
            var checking = await agent.TakeTurnAsync(Number(own), Patience, Ct);

            Assert.True(checking.Completed, $"round {round}: {checking.Describe()}");
            Assert.True(
                checking.CostUsd == own,
                $"round {round}: the turn read the reply to {checking.CostUsd}, not its own {own}");
        }
    }

    [Fact]
    public async Task An_agent_that_will_not_take_its_prompt_is_ended_rather_than_waited_on_for_ever()
    {
        // Far beyond any pipe's buffer, so the write can only finish if the agent reads.
        await using var agent = Start(StubRunner.NotReading(_workspace));
        var prompt = new string('x', 4 * 1024 * 1024);

        var started = DateTime.UtcNow;
        var outcome = await agent.TakeTurnAsync(prompt, TimeSpan.FromSeconds(2), Ct);
        var elapsed = DateTime.UtcNow - started;

        Assert.True(outcome.ProcessDied, outcome.Describe());
        Assert.True(elapsed < Patience, $"the turn took {elapsed.TotalSeconds:0}s");

        // Ended, so the node replaces it on the next wake rather than writing after half a line.
        var deadline = DateTime.UtcNow + Patience;

        while (agent.IsAlive && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
        }

        Assert.False(agent.IsAlive, "the agent that would not take its prompt is still running");
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
