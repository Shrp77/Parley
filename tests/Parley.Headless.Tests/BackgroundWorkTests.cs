using System.Collections.Concurrent;
using Parley.Core;
using Xunit;

namespace Parley.Headless.Tests;

/// <summary>
/// An agent that starts work in the background, ends its turn while that work runs, and later begins a turn of its own to
/// read what it found (SPEC.md §11.6).
/// </summary>
/// <remarks>
/// The stub writes what Claude Code 2.1.325 was seen to write, run with standard input kept open: a background task's
/// count, the turn's result while the task runs, then, once it finishes, the count at zero, an <c>init</c>, and a second
/// result tagged <c>task-notification</c>, with nobody's prompt.
/// </remarks>
[Integration]
public sealed class BackgroundWorkTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private const string OneTask = """{"type":"system","subtype":"background_tasks_changed","tasks":[{"id":"b1"}]}""";

    private const string NoTasks = """{"type":"system","subtype":"background_tasks_changed","tasks":[]}""";

    private const string Finished = """{"type":"system","subtype":"task_notification","task_id":"b1","status":"completed"}""";

    private const string Init = """{"type":"system","subtype":"init","session_id":"s"}""";

    private const string Started = """{"type":"result","subtype":"success","is_error":false,"result":"STARTED","num_turns":1,"total_cost_usd":1,"permission_denials":[]}""";

    private const string Done = """{"type":"result","subtype":"success","is_error":false,"result":"DONE","num_turns":1,"total_cost_usd":2,"permission_denials":[],"origin":{"kind":"task-notification","producer":"session-task"}}""";

    private const string DoneAgain = """{"type":"result","subtype":"success","is_error":false,"result":"AGAIN","num_turns":1,"total_cost_usd":3,"permission_denials":[],"origin":{"kind":"task-notification","producer":"session-task"}}""";

    private const string Second = """{"type":"result","subtype":"success","is_error":false,"result":"SECOND","num_turns":1,"total_cost_usd":3,"permission_denials":[]}""";

    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "parley-background-" + Guid.NewGuid().ToString("N"));

    public BackgroundWorkTests() => Directory.CreateDirectory(_workspace);

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
    public async Task The_agents_own_turn_is_told_and_the_next_given_turn_reads_its_own_reply()
    {
        var template = StubRunner.Scripted(
            _workspace,
            [OneTask, Started, StubRunner.Pause(2), Finished, NoTasks, Init, Done],
            [Second]);

        var told = new ConcurrentQueue<string>();
        var ownEnded = new TaskCompletionSource<TurnOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var agent = ResidentAgent.Start(
            AgentName.Create("billing@test"), InvocationCommand.Parse(template), _workspace, TimeProvider.System);

        agent.Watch(new OwnWork(
            _ =>
            {
                told.Enqueue("began");
                return Task.CompletedTask;
            },
            (when, outcome) =>
            {
                told.Enqueue("ended");
                ownEnded.TrySetResult(outcome);
                return Task.CompletedTask;
            },
            tasks =>
            {
                told.Enqueue($"background {tasks}");
                return Task.CompletedTask;
            }));

        var first = await agent.TakeTurnAsync("1", Patience, Ct);

        Assert.True(first.Succeeded, first.Describe());
        Assert.Equal("STARTED", first.Reply);

        var own = await ownEnded.Task.WaitAsync(Patience, Ct);

        Assert.True(own.Succeeded, own.Describe());
        Assert.Equal("DONE", own.Reply);
        Assert.Equal(0, agent.BackgroundTasks);

        // The reply to the next given turn is its own, not the result the agent's own turn left behind.
        var second = await agent.TakeTurnAsync("2", Patience, Ct);

        Assert.True(second.Succeeded, second.Describe());
        Assert.Equal("SECOND", second.Reply);
        Assert.Equal(["background 1", "background 0", "began", "ended"], told.ToArray());
    }

    [Fact]
    [Slow]
    public async Task An_own_turn_that_begins_as_a_given_turn_ends_is_told_every_time()
    {
        // The background task finishes as the given turn ends, with no pause between: the agent's own init is read on the
        // pump after the given turn's reply and before that turn has let go on its own thread. Held by a turn that was not an
        // own turn, the init was dropped: the own turn was never told, so the agent read idle while it worked and the turn's
        // cost was never recorded (review, lens 1 on 0.64.6: 2 rounds in 40). Many rounds, because it is a race.
        for (var round = 0; round < 30; round++)
        {
            var workspace = Path.Combine(_workspace, "round" + round.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(workspace);

            var template = StubRunner.Scripted(
                workspace,
                [OneTask, Started, Finished, NoTasks, Init, StubRunner.Pause(1), Done],
                [Second]);

            var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ended = new TaskCompletionSource<TurnOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

            await using var agent = ResidentAgent.Start(
                AgentName.Create("billing@test"), InvocationCommand.Parse(template), workspace, TimeProvider.System);

            agent.Watch(new OwnWork(
                _ =>
                {
                    began.TrySetResult();
                    return Task.CompletedTask;
                },
                (_, outcome) =>
                {
                    ended.TrySetResult(outcome);
                    return Task.CompletedTask;
                },
                _ => Task.CompletedTask));

            var first = await agent.TakeTurnAsync("1", Patience, Ct);

            Assert.Equal("STARTED", first.Reply);

            try
            {
                await began.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            }
            catch (TimeoutException)
            {
                Assert.Fail($"round {round}: the agent's own turn was never told to have begun");
            }

            var own = await ended.Task.WaitAsync(Patience, Ct);

            Assert.True(own.Succeeded, $"round {round}: {own.Describe()}");
            Assert.Equal("DONE", own.Reply);

            // And the given turn after it waits for it and reads its own reply.
            var second = await agent.TakeTurnAsync("2", Patience, Ct);

            Assert.Equal("SECOND", second.Reply);
        }
    }

    [Fact]
    [Slow]
    public async Task Own_turns_that_follow_each_other_closely_are_each_told_in_order()
    {
        // Review, quality lens (0.62.4 to 0.62.6): the result ends an own turn on the pump and the turn lets go of the agent a
        // moment later on another thread, so an init read in between found the agent held and its turn was dropped: 37 of 60
        // rounds told only the first. A later version still lost one turn of eight in 80 rounds, between a queued turn taking
        // the lock and saying so. Eight own turns, each beginning the instant the last one's result is read, ten times over.
        const int Turns = 8;
        const int Rounds = 10;

        string[] script = [OneTask, Started, StubRunner.Pause(1), Finished, NoTasks, Init, Done, .. Enumerable.Repeat(new[] { Init, DoneAgain }, Turns - 1).SelectMany(pair => pair)];

        for (var round = 0; round < Rounds; round++)
        {
            var template = StubRunner.Scripted(_workspace, script, [Second]);
            var told = new ConcurrentQueue<string>();
            var allEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            await using var agent = ResidentAgent.Start(
                AgentName.Create("billing@test"), InvocationCommand.Parse(template), _workspace, TimeProvider.System);

            agent.Watch(new OwnWork(
                _ =>
                {
                    told.Enqueue("began");
                    return Task.CompletedTask;
                },
                (_, _) =>
                {
                    told.Enqueue("ended");

                    if (told.Count(t => t == "ended") == Turns)
                    {
                        allEnded.TrySetResult();
                    }

                    return Task.CompletedTask;
                },
                _ => Task.CompletedTask));

            _ = await agent.TakeTurnAsync("1", Patience, Ct);
            await allEnded.Task.WaitAsync(Patience, Ct);

            Assert.Equal(Enumerable.Repeat(new[] { "began", "ended" }, Turns).SelectMany(pair => pair), told.ToArray());
        }
    }

    [Fact]
    public async Task A_given_turn_queued_behind_two_own_turns_is_not_mistaken_for_a_third()
    {
        // Review, quality lens (0.62.5): the second own turn queued behind the first had its result, and the given turn took
        // the lock before it. The given turn's own init then read the lock held by a finished turn and started a phantom one,
        // which held the agent until the patience ran out and reported a turn that never happened.
        var template = StubRunner.Scripted(
            _workspace,
            [OneTask, Started, StubRunner.Pause(1), Finished, NoTasks, Init, StubRunner.Pause(2), Done, Init, StubRunner.Pause(1), DoneAgain],
            [Init, Second],
            [Second]);

        var told = new ConcurrentQueue<string>();
        var firstBegan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var agent = ResidentAgent.Start(
            AgentName.Create("billing@test"), InvocationCommand.Parse(template), _workspace, TimeProvider.System);

        agent.OwnTurnPatience = TimeSpan.FromSeconds(6);
        agent.Watch(new OwnWork(
            _ =>
            {
                told.Enqueue("began");
                firstBegan.TrySetResult();
                return Task.CompletedTask;
            },
            (_, _) =>
            {
                told.Enqueue("ended");
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask));

        _ = await agent.TakeTurnAsync("1", Patience, Ct);
        await firstBegan.Task.WaitAsync(Patience, Ct);

        // Given while the first own turn runs, so it queues for the lock with the second own turn.
        var second = await agent.TakeTurnAsync("2", Patience, Ct);
        Assert.True(second.Succeeded, second.Describe());

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var third = await agent.TakeTurnAsync("3", Patience, Ct);

        Assert.True(third.Succeeded, third.Describe());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"the third turn waited {watch.Elapsed}: a phantom own turn held the agent");
        Assert.Equal(told.Count(t => t == "began"), told.Count(t => t == "ended"));
    }

    [Fact]
    public async Task A_turn_given_while_the_agent_works_on_its_own_waits_for_it_and_reads_its_own_reply()
    {
        // The second prompt arrives while the agent's own turn runs, as the probe sent it: the runner answers its own turn
        // first, tagged, then the prompt.
        var template = StubRunner.Scripted(
            _workspace,
            [OneTask, Started, StubRunner.Pause(1), Finished, NoTasks, Init, StubRunner.Pause(2), Done],
            [Second]);

        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var agent = ResidentAgent.Start(
            AgentName.Create("billing@test"), InvocationCommand.Parse(template), _workspace, TimeProvider.System);

        agent.Watch(new OwnWork(
            _ =>
            {
                began.TrySetResult();
                return Task.CompletedTask;
            },
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask));

        _ = await agent.TakeTurnAsync("1", Patience, Ct);
        await began.Task.WaitAsync(Patience, Ct);

        var second = await agent.TakeTurnAsync("2", Patience, Ct);

        Assert.True(second.Succeeded, second.Describe());
        Assert.Equal("SECOND", second.Reply);
    }

    [Fact]
    public void A_results_origin_and_a_task_list_are_read()
    {
        var done = ResidentAgent.RunnerEvent.Read(Done)!;
        var tasks = ResidentAgent.RunnerEvent.Read(OneTask)!;
        var plain = ResidentAgent.RunnerEvent.Read(Second)!;

        Assert.Equal("task-notification", done.OriginKind);
        Assert.Equal("DONE", done.Result);
        Assert.Equal(1, tasks.Tasks);
        Assert.Null(plain.OriginKind);
        Assert.Null(plain.Tasks);
    }
}
