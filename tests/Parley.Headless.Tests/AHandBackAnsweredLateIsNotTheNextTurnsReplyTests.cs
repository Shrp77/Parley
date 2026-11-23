using Parley.Core;
using Xunit;

namespace Parley.Headless.Tests;

/// <summary>
/// A hand-back's result that comes after its turn ran out of patience is the agent's own, never the reply of a given turn
/// waiting meanwhile (SPEC.md §11.6).
/// </summary>
[Integration]
public sealed class AHandBackAnsweredLateIsNotTheNextTurnsReplyTests : IDisposable
{
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(15);

    private const string Init = """{"type":"system","subtype":"init","session_id":"s"}""";

    private const string Started = """{"type":"result","subtype":"success","is_error":false,"result":"STARTED","num_turns":1,"total_cost_usd":1,"permission_denials":[]}""";

    private const string PeerDone = """{"type":"result","subtype":"success","is_error":false,"result":"PEER","num_turns":1,"total_cost_usd":2,"permission_denials":[],"origin":{"kind":"peer","from":"a1","name":"general-purpose"}}""";

    private const string Second = """{"type":"result","subtype":"success","is_error":false,"result":"SECOND","num_turns":1,"total_cost_usd":3,"permission_denials":[]}""";

    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "parley-owed-" + Guid.NewGuid().ToString("N"));

    public AHandBackAnsweredLateIsNotTheNextTurnsReplyTests() => Directory.CreateDirectory(_workspace);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
    public async Task A_hand_back_answered_after_its_turn_gave_up_is_not_the_waiting_given_turns_reply()
    {
        // Turn 1 answers. A sub-agent hands back and the runner begins a turn of its own (init), which runs past the node's
        // patience. Turn 2 is then given; while it waits, the hand-back's tagged result arrives, then turn 2's own reply.
        var template = StubRunner.Scripted(
            _workspace,
            [Started, StubRunner.Pause(1), Init, StubRunner.Pause(6), PeerDone],
            [Second]);

        var ended = new TaskCompletionSource<TurnOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var agent = ResidentAgent.Start(
            AgentName.Create("billing@test"), InvocationCommand.Parse(template), _workspace, TimeProvider.System);

        agent.OwnTurnPatience = TimeSpan.FromSeconds(1);
        agent.Watch(new OwnWork(
            _ => Task.CompletedTask,
            (_, outcome) =>
            {
                ended.TrySetResult(outcome);
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask));

        var one = await agent.TakeTurnAsync("1", Short, Ct);

        Assert.Equal("STARTED", one.Reply);

        var own = await ended.Task.WaitAsync(Short, Ct);

        Assert.True(own.TimedOut, own.Describe());

        var two = await agent.TakeTurnAsync("2", Short, Ct);

        Assert.True(two.Succeeded, two.Describe());
        Assert.Equal("SECOND", two.Reply);
    }
}
