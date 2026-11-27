using System.Collections.Concurrent;
using Parley.Core;
using Xunit;

namespace Parley.Headless.Tests;

/// <summary>A resident agent tells its watcher each change in what it has running, by what names the tasks as well as how
/// many there are (feature 043, SPEC.md §11.6).</summary>
/// <remarks>
/// One task ending as another starts leaves the count where it was: compared by count alone, the person would go on being
/// shown the first task's name for work that is no longer running. And the runner saying the same list again is not a
/// change, so it is told once.
/// </remarks>
[Integration]
public sealed class EachChangeInWhatRunsIsToldTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private const string FirstTask =
        """{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"a1","task_type":"local_agent","description":"Count to three"}]}""";

    private const string OtherTask =
        """{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"b2","task_type":"local_bash","description":"Wait a while"}]}""";

    private const string NoTasks = """{"type":"system","subtype":"background_tasks_changed","tasks":[]}""";

    private const string Started = """{"type":"result","subtype":"success","is_error":false,"result":"STARTED","num_turns":1,"total_cost_usd":1,"permission_denials":[]}""";

    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "parley-each-change-" + Guid.NewGuid().ToString("N"));

    public EachChangeInWhatRunsIsToldTests() => Directory.CreateDirectory(_workspace);

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
    public async Task A_different_task_at_the_same_count_is_told_and_the_same_list_said_again_is_not()
    {
        // The last line, nothing running, is a change too: once it is told, every notice before it has been.
        var told = await TellAsync([FirstTask, OtherTask, OtherTask, NoTasks, Started]);

        Assert.Equal(
            [
                new BackgroundSnapshot(1, [new BackgroundTask("a1", TaskKind.Agent, "Count to three")]),
                new BackgroundSnapshot(1, [new BackgroundTask("b2", TaskKind.Command, "Wait a while")]),
                BackgroundSnapshot.None,
            ],
            told);
    }

    [Fact]
    public async Task The_same_list_said_twice_is_told_once()
    {
        var told = await TellAsync([FirstTask, FirstTask, NoTasks, Started]);

        Assert.Equal(
            [new BackgroundSnapshot(1, [new BackgroundTask("a1", TaskKind.Agent, "Count to three")]), BackgroundSnapshot.None],
            told);
    }

    /// <summary>Runs one turn whose lines are <paramref name="lines"/> and gives back every snapshot told, once the one
    /// with nothing running has been.</summary>
    private async Task<BackgroundSnapshot[]> TellAsync(string[] lines)
    {
        var template = StubRunner.Scripted(_workspace, lines);
        var told = new ConcurrentQueue<BackgroundSnapshot>();
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var agent = ResidentAgent.Start(
            AgentName.Create("billing@test"), InvocationCommand.Parse(template), _workspace, TimeProvider.System);

        agent.Watch(new OwnWork(
            _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            work =>
            {
                told.Enqueue(work);

                if (work.Count == 0)
                {
                    idle.TrySetResult();
                }

                return Task.CompletedTask;
            }));

        var turn = await agent.TakeTurnAsync("1", Patience, Ct);

        Assert.True(turn.Succeeded, turn.Describe());

        await idle.Task.WaitAsync(Patience, Ct);

        var snapshots = told.ToArray();

        // Each told snapshot is numbered after the one before it.
        for (var i = 1; i < snapshots.Length; i++)
        {
            Assert.True(snapshots[i].Sequence > snapshots[i - 1].Sequence, $"snapshot {i} is not numbered after {i - 1}");
        }

        return snapshots;
    }
}
