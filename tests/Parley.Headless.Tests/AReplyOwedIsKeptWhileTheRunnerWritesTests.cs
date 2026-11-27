using Parley.Core;
using Xunit;

namespace Parley.Headless.Tests;

/// <summary>
/// A reply owed to a turn that gave up is still owed while the runner works, even after it has ended a turn of its own since
/// the prompt (SPEC.md §11.6: forgotten only when it "said nothing for the whole of a turn's time").
/// </summary>
[Integration]
public sealed class AReplyOwedIsKeptWhileTheRunnerWritesTests : IDisposable
{
    private const string Done = """{"type":"result","subtype":"success","is_error":false,"result":"DONE","num_turns":1,"total_cost_usd":2,"permission_denials":[],"origin":{"kind":"task-notification","producer":"session-task"}}""";

    private const string First = """{"type":"result","subtype":"success","is_error":false,"result":"FIRST","num_turns":1,"total_cost_usd":1,"permission_denials":[]}""";

    private const string Second = """{"type":"result","subtype":"success","is_error":false,"result":"SECOND","num_turns":1,"total_cost_usd":3,"permission_denials":[]}""";

    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "parley-owed-" + Guid.NewGuid().ToString("N"));

    public AReplyOwedIsKeptWhileTheRunnerWritesTests() => Directory.CreateDirectory(_workspace);

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
    public async Task A_reply_owed_is_not_forgotten_while_the_runner_is_still_writing()
    {
        // Turn 1 gives up after a second. The runner ends a turn of its own (tagged) at 2 s, keeps working on prompt 1 and
        // writing lines, and answers prompt 1 at about 6 s. Turn 2's wait (3 s) sees it working the whole time.
        var template = StubRunner.Scripted(
            _workspace,
            [StubRunner.Pause(3), Done, StubRunner.Pause(1), "working", StubRunner.Pause(1), "working", StubRunner.Pause(1), "working", StubRunner.Pause(1), "working", StubRunner.Pause(1), First],
            [Second]);

        await using var agent = ResidentAgent.Start(
            AgentName.Create("billing@test"), InvocationCommand.Parse(template), _workspace, TimeProvider.System);

        var one = await agent.TakeTurnAsync("1", TimeSpan.FromSeconds(1), Ct);

        Assert.True(one.TimedOut, one.Describe());

        var two = await agent.TakeTurnAsync("2", TimeSpan.FromSeconds(5), Ct);

        Assert.NotEqual("FIRST", two.Reply);

        var three = await agent.TakeTurnAsync("3", TimeSpan.FromSeconds(10), Ct);

        Assert.True(three.Succeeded, three.Describe());
        Assert.Equal("SECOND", three.Reply);
    }
}
