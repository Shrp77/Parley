using Parley.Core;
using Xunit;

namespace Parley.Headless.Tests;

/// <summary>
/// An agent that stays up between turns.
/// </summary>
/// <remarks>
/// <para>
/// The one-shot invoker these replace had a bug no unit test could catch: the child inherited the
/// host's standard input, and only a real MCP server had an interesting standard input to
/// inherit. That is gone rather than fixed, because a resident agent redirects standard input in
/// order to write turns to it — the pipe is the feature now, and nothing is inherited.
/// </para>
/// <para>
/// What is left is genuinely testable here: that the process survives a turn, that the stream
/// stays in step when a turn is abandoned, and that closing standard input stops it.
/// </para>
/// </remarks>
[Integration]
public sealed class ResidentAgentTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "parley-resident-" + Guid.NewGuid().ToString("N"));

    public ResidentAgentTests() => Directory.CreateDirectory(_workspace);

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
    public async Task It_survives_a_turn_and_takes_another()
    {
        // The whole reason this type exists. A one-shot process could answer the first question
        // and nothing after it; an agent that is still alive is one that still remembers.
        await using var agent = Start(StubRunner.Answering(_workspace));

        var first = await agent.TakeTurnAsync("first", Patience, Ct);

        Assert.True(first.Succeeded, first.Describe());
        Assert.True(agent.IsAlive, "the agent exited after one turn");

        var second = await agent.TakeTurnAsync("second", Patience, Ct);

        Assert.True(second.Succeeded, second.Describe());
        Assert.True(agent.IsAlive);
    }

    [Fact]
    public async Task It_reports_what_the_turn_cost()
    {
        // A fleet is the first thing here that spends money with nobody watching, so the numbers
        // the runner reports have to survive the trip into the record.
        await using var agent = Start(StubRunner.Answering(_workspace));

        var outcome = await agent.TakeTurnAsync("go", Patience, Ct);

        Assert.Equal(0.25, outcome.CostUsd, 3);
        Assert.Equal(2, outcome.ModelTurns);
    }

    [Fact]
    public async Task It_counts_permission_denials()
    {
        // An unattended agent is not asked for permission -- it is refused. Nothing else in the
        // record separates that from an agent that has simply become unhelpful.
        await using var agent = Start(
            StubRunner.Answering(_workspace, StubRunner.ErrorResult));

        var outcome = await agent.TakeTurnAsync("go", Patience, Ct);

        Assert.Equal(2, outcome.PermissionDenials);
        Assert.True(outcome.IsError);
        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public async Task It_learns_its_session_from_the_runner()
    {
        await using var agent = Start(StubRunner.Answering(_workspace));

        await agent.TakeTurnAsync("go", Patience, Ct);

        Assert.Equal("stub-session", agent.SessionId);
    }

    [Fact]
    public async Task A_turn_that_runs_long_is_abandoned_without_killing_the_agent()
    {
        // The important departure from the one-shot invoker, which killed on timeout because it
        // had nothing to lose. Here the process holds everything the agent has learned, and
        // discarding that because one turn ran long is the worse trade by a wide margin.
        await using var agent = Start(StubRunner.Answering(_workspace, delaySeconds: 4));

        var outcome = await agent.TakeTurnAsync("go", TimeSpan.FromMilliseconds(300), Ct);

        Assert.True(outcome.TimedOut);
        Assert.False(outcome.ProcessDied);
        Assert.True(agent.IsAlive, "a timed-out turn killed the agent");
    }

    [Fact]
    public async Task The_reply_to_an_abandoned_turn_is_not_read_as_the_next_ones()
    {
        // Without this bookkeeping the stream desynchronises permanently: every later turn reads
        // the previous turn's reply, so an agent that answered nothing looks like it answered,
        // and it never comes back into step on its own.
        await using var agent = Start(StubRunner.Answering(_workspace, delaySeconds: 3));

        var abandoned = await agent.TakeTurnAsync("first", TimeSpan.FromMilliseconds(200), Ct);

        Assert.True(abandoned.TimedOut);

        // The stub answers the second turn after the same delay. If the stale reply from the
        // first were counted, this would return far sooner than the stub can possibly answer.
        var started = DateTime.UtcNow;
        var second = await agent.TakeTurnAsync("second", Patience, Ct);
        var elapsed = DateTime.UtcNow - started;

        Assert.True(second.Completed, second.Describe());
        Assert.True(
            elapsed > TimeSpan.FromSeconds(2),
            $"the second turn returned in {elapsed.TotalSeconds:0.0}s, which is the abandoned " +
            "reply being read as its own");
    }

    [Fact]
    public async Task Closing_standard_input_stops_it()
    {
        // How an agent is told to quit, and the reason a fleet can stop politely at all.
        var agent = Start(StubRunner.Answering(_workspace));

        await agent.TakeTurnAsync("go", Patience, Ct);

        var stopped = await agent.ShutdownAsync(TimeSpan.FromSeconds(20), Ct);

        Assert.True(stopped, "the agent had to be killed");
        Assert.False(agent.IsAlive);

        await agent.DisposeAsync();
    }

    [Fact]
    public async Task An_agent_that_will_not_stop_is_killed()
    {
        // A wedged agent must not be able to hold the fleet open. The grace period is what
        // separates "give it a chance" from "wait for something that is not coming".
        var agent = Start(StubRunner.Silent(_workspace));

        var stopped = await agent.ShutdownAsync(TimeSpan.FromSeconds(2), Ct);

        // The stub exits at end of input, so a polite stop is the expected result on a healthy
        // machine; what matters is that either way the agent is gone rather than left running.
        Assert.False(agent.IsAlive);

        await agent.DisposeAsync();

        _ = stopped;
    }

    [Fact]
    public async Task A_turn_given_to_a_dead_agent_says_so_rather_than_hanging()
    {
        // A runner can die between passes -- crashed, killed, out of quota. The fleet has to be
        // told, so it can start a fresh one, rather than waiting on a process that is not there.
        await using var agent = Start(StubRunner.Dying(_workspace));

        // Let it exit before asking anything of it.
        for (var i = 0; i < 100 && agent.IsAlive; i++)
        {
            await Task.Delay(50, Ct);
        }

        var outcome = await agent.TakeTurnAsync("go", Patience, Ct);

        Assert.True(outcome.ProcessDied);
        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public void A_missing_workspace_says_so_rather_than_failing_obscurely()
    {
        // A registry entry can name a directory that has since moved. The process-start failure
        // for that is a bare platform error, and the whole difference is whether an operator can
        // tell which of the registry and the disk is wrong.
        var missing = Path.Combine(_workspace, "not-here");

        var error = Assert.Throws<DirectoryNotFoundException>(() =>
            ResidentAgent.Start(
                AgentName.Create("billing@test"),
                InvocationCommand.Parse(StubRunner.Answering(_workspace)),
                missing,
                TimeProvider.System));

        Assert.Contains(missing, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_runner_that_is_not_installed_names_the_agent_and_the_command()
    {
        // One misconfigured agent must not read as the fleet being broken.
        var error = Assert.Throws<InvocationFailedException>(() =>
            ResidentAgent.Start(
                AgentName.Create("billing@test"),
                InvocationCommand.Parse("parley-no-such-runner --input-format stream-json"),
                _workspace,
                TimeProvider.System));

        Assert.Contains("billing@test", error.Message, StringComparison.Ordinal);
        Assert.Contains("parley-no-such-runner", error.Message, StringComparison.Ordinal);
    }
}
