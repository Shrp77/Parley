using Parley.Core;
using Xunit;

namespace Parley.Headless.Tests;

/// <summary>
/// That a line of a runner's output the strict file reader would refuse neither stops the pump nor costs a turn its
/// reply.
/// </summary>
/// <remarks>
/// Lens 1 run 132. A lone surrogate escape in a member name, anywhere in a line, threw
/// <see cref="InvalidOperationException"/> out of the strict parse, which the pump did not catch: the pump ended, the
/// turn read as a dead process while the process lived, and so did every turn after, until the node restarted. A result
/// line naming a member twice was skipped as not JSON, so its turn timed out and every later turn waited on a reply
/// that never came.
/// </remarks>
[Integration]
public sealed class ABadRunnerLineKeepsTurnsInStepTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    // An event that is not a result, with a lone surrogate in a name deep inside it, as a runner reporting a tool's
    // output could write.
    private const string BrokenAssistant = """
        {"type":"assistant","message":{"content":[{"type":"tool_use","input":{"K\uD800":1}}]}}
        """;

    // The first turn's result, with a lone surrogate in a name in a nested object.
    private const string BrokenNestedResult = """
        {"type":"result","subtype":"success","is_error":false,"result":"one","num_turns":1,"total_cost_usd":1,"usage":{"T\uD800":true},"permission_denials":[]}
        """;

    // The second turn's result, naming two members twice: the last copy is the one read.
    private const string DuplicatedResult = """
        {"type":"result","subtype":"success","is_error":false,"result":"stale","result":"two","num_turns":1,"total_cost_usd":0,"total_cost_usd":2,"permission_denials":[]}
        """;

    // The third turn's result, with lone surrogates in top-level names and in the reply itself. The names start with nothing
    // and with 't', so looking up "type" or "is_error" past them decodes them (lens 1 run 133: a name starting 'T' did not).
    private const string BrokenTopLevelResult = """
        {"type":"result","\uD800":0,"t\uD800":0,"subtype":"success","is_error":false,"result":"three\uD800","num_turns":1,"total_cost_usd":3,"permission_denials":[]}
        """;

    // The fourth turn's result, ordinary.
    private const string PlainResult = """
        {"type":"result","subtype":"success","is_error":false,"result":"four","num_turns":1,"total_cost_usd":4,"permission_denials":[]}
        """;

    // A result whose refused tool call carried an input nested 200 deep, as the model can write (lens 2 run 139).
    private static readonly string DeepDenialResult =
        "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"deep\",\"num_turns\":1,"
        + "\"total_cost_usd\":5,\"permission_denials\":[{\"tool_name\":\"Bash\",\"tool_input\":"
        + string.Concat(Enumerable.Repeat("{\"a\":", 200)) + "0" + new string('}', 200) + "}]}";

    // The turn after it, ordinary.
    private const string AfterDeepResult = """
        {"type":"result","subtype":"success","is_error":false,"result":"after","num_turns":1,"total_cost_usd":6,"permission_denials":[]}
        """;

    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "parley-bad-line-" + Guid.NewGuid().ToString("N"));

    public ABadRunnerLineKeepsTurnsInStepTests() => Directory.CreateDirectory(_workspace);

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
    public async Task Every_turn_completes_and_reads_its_own_reply()
    {
        var template = StubRunner.Scripted(
            _workspace,
            [BrokenAssistant, BrokenNestedResult],
            [DuplicatedResult],
            [BrokenTopLevelResult],
            [PlainResult]);

        await using var agent = ResidentAgent.Start(
            AgentName.Create("billing@test"), InvocationCommand.Parse(template), _workspace, TimeProvider.System);

        var first = await agent.TakeTurnAsync("1", Patience, Ct);

        Assert.True(first.Succeeded, first.Describe());
        Assert.Equal(1, first.CostUsd, 3);
        Assert.Equal("one", first.Reply);

        var second = await agent.TakeTurnAsync("2", Patience, Ct);

        Assert.True(second.Succeeded, second.Describe());
        Assert.Equal(2, second.CostUsd, 3);
        Assert.Equal("two", second.Reply);

        // A reply that is not valid text reads as absent; the turn is still this one's, and complete.
        var third = await agent.TakeTurnAsync("3", Patience, Ct);

        Assert.True(third.Succeeded, third.Describe());
        Assert.Equal(3, third.CostUsd, 3);
        Assert.Null(third.Reply);

        var fourth = await agent.TakeTurnAsync("4", Patience, Ct);

        Assert.True(fourth.Succeeded, fourth.Describe());
        Assert.Equal(4, fourth.CostUsd, 3);
        Assert.Equal("four", fourth.Reply);
        Assert.True(agent.IsAlive);
    }

    [Fact]
    public async Task A_result_with_a_deeply_nested_refused_call_completes_its_turn()
    {
        var template = StubRunner.Scripted(_workspace, [DeepDenialResult], [AfterDeepResult]);

        await using var agent = ResidentAgent.Start(
            AgentName.Create("billing@test"), InvocationCommand.Parse(template), _workspace, TimeProvider.System);

        var deep = await agent.TakeTurnAsync("1", Patience, Ct);

        Assert.True(deep.Succeeded, deep.Describe());
        Assert.False(deep.TimedOut);
        Assert.Equal(5, deep.CostUsd, 3);
        Assert.Equal(1, deep.PermissionDenials);
        Assert.Equal("deep", deep.Reply);

        var after = await agent.TakeTurnAsync("2", Patience, Ct);

        Assert.True(after.Succeeded, after.Describe());
        Assert.Equal(6, after.CostUsd, 3);
        Assert.Equal("after", after.Reply);
    }
}
