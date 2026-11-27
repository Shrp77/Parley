using Parley.Core;
using Xunit;

namespace Parley.Headless.Tests;

/// <summary>
/// The resident agent against a real runner, rather than a stub that agrees with it.
/// </summary>
/// <remarks>
/// <para>
/// Every other test here drives a script this repository wrote, which speaks the protocol
/// because this repository decided what it says. That is the shape of the bug that broke the
/// invoker this replaces: nine unit tests passed against a version that hung on every real
/// invocation, because the failure lived entirely in what the real thing did differently.
/// </para>
/// <para>
/// So this one starts the configured runner and takes two turns, and the assertion is the one
/// that matters: <b>the second turn remembers the first</b>. That is residency, and no stub can
/// evidence it — a stub remembers whatever it was written to remember.
/// </para>
/// <para>
/// Skipped unless <c>PARLEY_LIVE_RUNNER</c> names a command, because it costs real money and
/// needs credentials CI does not have. Set it to the same value a repository's
/// <c>PARLEY_INVOKE_COMMAND</c> carries and run this project directly. It is the check to run
/// after changing anything about the turn protocol.
/// </para>
/// </remarks>
[Integration]
public sealed class LiveRunnerTests : IDisposable
{
    private const string Variable = "PARLEY_LIVE_RUNNER";

    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "parley-live-" + Guid.NewGuid().ToString("N"));

    public LiveRunnerTests() => Directory.CreateDirectory(_workspace);

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
    public async Task A_real_runner_remembers_the_previous_turn()
    {
        var template = Environment.GetEnvironmentVariable(Variable);

        if (string.IsNullOrWhiteSpace(template))
        {
            Assert.Skip($"{Variable} is not set; this test needs a real agent runner.");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var patience = TimeSpan.FromMinutes(5);

        await using var agent = ResidentAgent.Start(
            AgentName.Create("billing@test"),
            InvocationCommand.Parse(template),
            _workspace,
            TimeProvider.System);

        var first = await agent.TakeTurnAsync(
            "Remember the number 4271. Reply with exactly: OK", patience, ct);

        Assert.True(first.Succeeded, first.Describe());
        Assert.True(agent.IsAlive, "the runner exited after one turn");

        var second = await agent.TakeTurnAsync(
            "What number did I ask you to remember? Reply with just the number.", patience, ct);

        Assert.True(second.Succeeded, second.Describe());

        // The assertion the whole file exists for. A one-shot runner cannot pass this line, and
        // neither can a resident one whose turns are not reaching the same conversation.
        Assert.Contains("4271", second.Reply ?? string.Empty, StringComparison.Ordinal);

        // Reported by the runner and captured from its init event. One session across both turns
        // is what residency means; a new identifier would mean each turn got a fresh agent.
        Assert.False(string.IsNullOrWhiteSpace(agent.SessionId));

        // The turn actually cost something, which is how we know a model ran rather than the
        // command failing fast and reporting a tidy nothing.
        Assert.True(second.CostUsd > 0, "the runner reported no cost; did a model actually run?");
    }
}
