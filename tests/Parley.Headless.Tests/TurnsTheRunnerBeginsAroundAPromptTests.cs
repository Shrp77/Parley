using Parley.Core;
using Xunit;

namespace Parley.Headless.Tests;

/// <summary>
/// Turns the runner begins on its own close to a prompt the node gave (SPEC.md §11.6): a prompt folded into such a turn,
/// a turn begun by a sub-agent's hand-back, and a reply owed to nobody that never comes.
/// </summary>
/// <remarks>
/// <para>
/// Each shape is what Claude Code 2.1.330 was seen to write, run as a node runs it. A result carries the turn's
/// <c>origin</c> when the runner began the turn itself: <c>task-notification</c> when background work it started finished,
/// <c>peer</c> when a background sub-agent handed back its report.
/// </para>
/// <para>
/// A prompt that arrives while the runner is starting a turn of its own is folded into that turn as a queued command: the
/// runner answers both with one result, tagged with the turn's origin, and no result of the prompt's own ever comes.
/// </para>
/// </remarks>
[Integration]
public sealed class TurnsTheRunnerBeginsAroundAPromptTests : IDisposable
{
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(5);

    private const string OneTask = """{"type":"system","subtype":"background_tasks_changed","tasks":[{"id":"b1"}]}""";

    private const string NoTasks = """{"type":"system","subtype":"background_tasks_changed","tasks":[]}""";

    private const string Init = """{"type":"system","subtype":"init","session_id":"s"}""";

    private const string Started = """{"type":"result","subtype":"success","is_error":false,"result":"STARTED","num_turns":1,"total_cost_usd":1,"permission_denials":[]}""";

    private const string Done = """{"type":"result","subtype":"success","is_error":false,"result":"DONE","num_turns":1,"total_cost_usd":2,"permission_denials":[],"origin":{"kind":"task-notification","producer":"session-task"}}""";

    private const string PeerDone = """{"type":"result","subtype":"success","is_error":false,"result":"PEER","num_turns":1,"total_cost_usd":2,"permission_denials":[],"origin":{"kind":"peer","from":"a1","name":"general-purpose"}}""";

    private const string Second = """{"type":"result","subtype":"success","is_error":false,"result":"SECOND","num_turns":1,"total_cost_usd":3,"permission_denials":[]}""";

    private const string Third = """{"type":"result","subtype":"success","is_error":false,"result":"THIRD","num_turns":1,"total_cost_usd":4,"permission_denials":[]}""";

    private const string Fourth = """{"type":"result","subtype":"success","is_error":false,"result":"FOURTH","num_turns":1,"total_cost_usd":5,"permission_denials":[]}""";

    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "parley-folded-" + Guid.NewGuid().ToString("N"));

    public TurnsTheRunnerBeginsAroundAPromptTests() => Directory.CreateDirectory(_workspace);

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
    public async Task A_prompt_folded_into_a_turn_of_the_agents_own_is_answered_by_it_and_every_later_turn_by_its_own()
    {
        // Turn 1 answers; its background work finishes and the runner begins a turn of its own. Prompt 2 was queued behind
        // turn 1 and is written as it ends, before the own turn's init is read, and the runner folds it into that turn: the
        // one result is the own turn's, tagged. Swallowed as the agent's own, turn 2 waited out its whole time, its reply was
        // counted owed, and every turn after waited the same for a reply that never came and said nothing. The own turn's
        // init is written only once prompt 2 has been read, so the fold is what the runner sees however long turn 1 takes to
        // let go: a pause in its place lost the race whenever the test host was busy.
        var template = StubRunner.Scripted(
            _workspace,
            [OneTask, Started],
            [NoTasks, Init, StubRunner.Pause(1), Done],
            [Third],
            [Fourth]);

        await using var agent = ResidentAgent.Start(
            AgentName.Create("billing@test"), InvocationCommand.Parse(template), _workspace, TimeProvider.System);

        agent.OwnTurnPatience = Short;

        var first = agent.TakeTurnAsync("1", Short, Ct);
        var second = agent.TakeTurnAsync("2", Short, Ct);

        var one = await first;
        var two = await second;
        var three = await agent.TakeTurnAsync("3", Short, Ct);
        var four = await agent.TakeTurnAsync("4", Short, Ct);

        Assert.Equal("STARTED", one.Reply);
        Assert.True(two.Succeeded, $"turn 2: {two.Describe()}");
        Assert.Equal("DONE", two.Reply);
        Assert.True(three.Succeeded, $"turn 3: {three.Describe()}");
        Assert.Equal("THIRD", three.Reply);
        Assert.True(four.Succeeded, $"turn 4: {four.Describe()}");
        Assert.Equal("FOURTH", four.Reply);
    }

    [Fact]
    public async Task A_turn_begun_by_a_sub_agents_hand_back_is_the_agents_own_and_never_the_next_given_turns_reply()
    {
        // Claude Code begins a turn with origin kind "peer" when a background sub-agent hands back its report. Known only by
        // "task-notification", that result was read as a given turn's reply: the agent was held for the whole patience by a
        // turn of its own that never ended, and the next given turn took the hand-back's result as its own at once.
        var template = StubRunner.Scripted(
            _workspace,
            [Started, StubRunner.Pause(1), Init, PeerDone],
            [Second]);

        var ended = new TaskCompletionSource<TurnOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var agent = ResidentAgent.Start(
            AgentName.Create("billing@test"), InvocationCommand.Parse(template), _workspace, TimeProvider.System);

        agent.OwnTurnPatience = TimeSpan.FromSeconds(20);
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

        TurnOutcome own;

        try
        {
            own = await ended.Task.WaitAsync(Short, Ct);
        }
        catch (TimeoutException)
        {
            Assert.Fail("the hand-back's turn was not ended by its result: it holds the agent until its patience runs out");
            throw;
        }

        Assert.True(own.Succeeded, own.Describe());
        Assert.Equal("PEER", own.Reply);

        var two = await agent.TakeTurnAsync("2", Short, Ct);

        Assert.True(two.Succeeded, two.Describe());
        Assert.Equal("SECOND", two.Reply);
    }

    [Fact]
    public async Task A_result_no_turn_waits_for_is_discarded_and_never_read_as_the_next_turns_reply()
    {
        // A prompt the runner did not fold into a turn of its own after all is answered after that turn, when the given turn
        // has taken the turn's result and let go. That second result is nobody's: queued for the next given turn, it was read
        // as that turn's reply at once, and every reply after went to the turn after its own.
        const string Stray = """{"type":"result","subtype":"success","is_error":false,"result":"STRAY","num_turns":1,"total_cost_usd":9,"permission_denials":[]}""";

        var template = StubRunner.Scripted(
            _workspace,
            [Started, StubRunner.Pause(1), Stray],
            [Second]);

        await using var agent = ResidentAgent.Start(
            AgentName.Create("billing@test"), InvocationCommand.Parse(template), _workspace, TimeProvider.System);

        var one = await agent.TakeTurnAsync("1", Short, Ct);

        Assert.Equal("STARTED", one.Reply);

        await Task.Delay(TimeSpan.FromSeconds(2), Ct);

        var two = await agent.TakeTurnAsync("2", Short, Ct);

        Assert.True(two.Succeeded, two.Describe());
        Assert.Equal("SECOND", two.Reply);
    }

    [Fact]
    public async Task A_reply_owed_to_nobody_that_never_comes_stops_holding_turns_once_the_runner_has_ended_a_turn_and_gone_quiet()
    {
        // Turn 1 gives up before the runner answers. The runner then begins a turn of its own, folds turn 1's prompt into it
        // and goes quiet: the reply turn 1 is owed never comes. Every later turn waited its whole time for that reply and
        // ended timed out with nothing said, for as long as the process lived.
        var template = StubRunner.Scripted(
            _workspace,
            [StubRunner.Pause(2), Init, Done],
            [Second],
            [Third]);

        await using var agent = ResidentAgent.Start(
            AgentName.Create("billing@test"), InvocationCommand.Parse(template), _workspace, TimeProvider.System);

        agent.OwnTurnPatience = Short;

        var one = await agent.TakeTurnAsync("1", TimeSpan.FromSeconds(1), Ct);

        Assert.True(one.TimedOut, one.Describe());

        // Past the own turn's result, so the runner has ended a turn since the prompt and is idle.
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);

        var two = await agent.TakeTurnAsync("2", TimeSpan.FromSeconds(2), Ct);
        var three = await agent.TakeTurnAsync("3", Short, Ct);

        Assert.True(two.Succeeded, $"turn 2: {two.Describe()}");
        Assert.Equal("SECOND", two.Reply);
        Assert.True(three.Succeeded, $"turn 3: {three.Describe()}");
        Assert.Equal("THIRD", three.Reply);
    }
}
