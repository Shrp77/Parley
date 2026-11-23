using Parley.Core;
using Xunit;

namespace Parley.Headless.Tests;

/// <summary>
/// A reply owed to a turn that gave up is still owed while the runner is quiet, when it has ended no turn since the prompt
/// (SPEC.md §11.6: forgotten only "unless the runner has ended a turn since that prompt"): one long command says nothing.
/// </summary>
[Integration]
public sealed class AQuietRunnerThatEndedNoTurnStillOwesTests : IDisposable
{
    private const string First = """{"type":"result","subtype":"success","is_error":false,"result":"FIRST","num_turns":1,"total_cost_usd":1,"permission_denials":[]}""";

    private const string Second = """{"type":"result","subtype":"success","is_error":false,"result":"SECOND","num_turns":1,"total_cost_usd":3,"permission_denials":[]}""";

    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "parley-owed-" + Guid.NewGuid().ToString("N"));

    public AQuietRunnerThatEndedNoTurnStillOwesTests() => Directory.CreateDirectory(_workspace);

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
    public async Task A_reply_owed_is_not_forgotten_while_a_runner_that_has_ended_no_turn_is_quiet()
    {
        // Turn 1 gives up after a second; the runner says nothing for four seconds and then answers prompt 1.
        var template = StubRunner.Scripted(_workspace, [StubRunner.Pause(4), First], [Second]);

        await using var agent = ResidentAgent.Start(
            AgentName.Create("billing@test"), InvocationCommand.Parse(template), _workspace, TimeProvider.System);

        var one = await agent.TakeTurnAsync("1", TimeSpan.FromSeconds(1), Ct);

        Assert.True(one.TimedOut, one.Describe());

        var two = await agent.TakeTurnAsync("2", TimeSpan.FromSeconds(2), Ct);

        Assert.NotEqual("FIRST", two.Reply);

        var three = await agent.TakeTurnAsync("3", TimeSpan.FromSeconds(10), Ct);

        Assert.True(three.Succeeded, three.Describe());
        Assert.Equal("SECOND", three.Reply);
    }
}
