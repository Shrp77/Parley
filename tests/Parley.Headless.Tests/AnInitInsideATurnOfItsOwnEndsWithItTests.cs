using Parley.Core;
using Xunit;

namespace Parley.Headless.Tests;

/// <summary>
/// That an init read while a turn of the agent's own holds it is forgotten when that turn ends, so it is never taken for a
/// turn the next given turn's prompt was folded into (SPEC.md §11.6).
/// </summary>
/// <remarks>
/// Review, lens 3 run A on 81b6a1a7: with the note left standing after a turn of the agent's own let go, every test stayed
/// green, and the next given turn took a later tagged result, a turn the agent began itself, as its own reply.
/// </remarks>
[Integration]
public sealed class AnInitInsideATurnOfItsOwnEndsWithItTests : IDisposable
{
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(8);

    private const string Init = """{"type":"system","subtype":"init","session_id":"s"}""";

    private const string Started = """{"type":"result","subtype":"success","is_error":false,"result":"STARTED","num_turns":1,"total_cost_usd":1,"permission_denials":[]}""";

    private const string FirstDone = """{"type":"result","subtype":"success","is_error":false,"result":"OWN-1","num_turns":1,"total_cost_usd":2,"permission_denials":[],"origin":{"kind":"task-notification"}}""";

    private const string SecondDone = """{"type":"result","subtype":"success","is_error":false,"result":"OWN-2","num_turns":1,"total_cost_usd":3,"permission_denials":[],"origin":{"kind":"task-notification"}}""";

    private const string Second = """{"type":"result","subtype":"success","is_error":false,"result":"SECOND","num_turns":1,"total_cost_usd":4,"permission_denials":[]}""";

    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "parley-own-init-" + Guid.NewGuid().ToString("N"));

    public AnInitInsideATurnOfItsOwnEndsWithItTests() => Directory.CreateDirectory(_workspace);

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
    public async Task A_tagged_result_after_a_turn_of_its_own_ended_is_not_the_next_given_turns_reply()
    {
        // Turn 1 answers. The runner begins a turn of its own (init), and a second init comes while that turn still holds the
        // agent. The first ends; turn 2 is given; while it waits a later tagged result comes, then turn 2's own reply.
        var template = StubRunner.Scripted(
            _workspace,
            [Started, StubRunner.Pause(1), Init, Init, FirstDone, StubRunner.Pause(2), SecondDone],
            [Second]);

        var ended = new TaskCompletionSource<TurnOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var agent = ResidentAgent.Start(
            AgentName.Create("billing@test"), InvocationCommand.Parse(template), _workspace, TimeProvider.System);

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

        Assert.Equal("OWN-1", own.Reply);

        var two = await agent.TakeTurnAsync("2", Short, Ct);

        Assert.True(two.Succeeded, two.Describe());
        Assert.Equal("SECOND", two.Reply);
    }
}
