using System.Collections.Concurrent;
using Parley.Configuration;
using Parley.Core;
using Parley.Headless;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// That when two endings are asked of one agent during one turn, the later is the one the node does (SPEC.md §11.1).
/// </summary>
/// <remarks>
/// <para>
/// Review, lens 1 run C, 3 of 3 in each order: a person's stop then restart, a restart then a stop, or a pending ownership
/// refresh then a person's stop, kept the first and dropped the second without a word. The web said it had asked for a
/// restart and nothing ran afterwards; after a restart then a stop the process kept running.
/// </para>
/// <para>
/// Driven with a stand-in agent whose turn takes three seconds, so both endings are asked while the first waits for it.
/// Each case is a three-second turn plus a three-second wait for anything still to come: about 26 seconds a run, so it
/// stays out of the smoke run.
/// </para>
/// </remarks>
[Integration]
[Slow]
public sealed class TheLastEndingAskedIsTheOneDoneTests : IDisposable
{
    private const string Agent = "docs@alpha";

    private readonly string _workspace = Directory.CreateTempSubdirectory("parley-last-ending").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task A_stop_then_a_restart_leaves_one_new_process_running()
    {
        var (residents, identity, old, said) = await DuringOneTurnAsync(
            (catalogue, residents, output) => NodeCommand.StopAsync(
                new Wire.StopAgent { Agent = Agent, Because = "stuck" }, catalogue, "alpha", residents, output, Ct),
            (catalogue, residents, output) => NodeCommand.StopAsync(
                new Wire.StopAgent { Agent = Agent, Because = "stuck", Restart = true }, catalogue, "alpha", residents, output, Ct));

        try
        {
            Assert.True(residents.TryGetValue(identity, out var now), $"nothing is running. The node said: {said.Text}");
            Assert.NotSame(old, now);
            Assert.True(now.IsAlive, "the restarted process is not running");
            Assert.False(old.IsAlive, "the old process is still running");
            Assert.Single(residents);
            Assert.Contains("restarted once its turn was over, as a person asked", said.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("stopped once its turn was over", said.Text, StringComparison.Ordinal);
        }
        finally
        {
            await DisposeAllAsync(residents);
        }
    }

    [Fact]
    public async Task A_restart_then_a_stop_leaves_nothing_running()
    {
        var (residents, _, old, said) = await DuringOneTurnAsync(
            (catalogue, residents, output) => NodeCommand.StopAsync(
                new Wire.StopAgent { Agent = Agent, Because = "stuck", Restart = true }, catalogue, "alpha", residents, output, Ct),
            (catalogue, residents, output) => NodeCommand.StopAsync(
                new Wire.StopAgent { Agent = Agent, Because = "stuck" }, catalogue, "alpha", residents, output, Ct));

        try
        {
            Assert.False(old.IsAlive, "the old process is still running");
            Assert.True(residents.IsEmpty, $"something is running. The node said: {said.Text}");
            Assert.Contains("stopped once its turn was over, as a person asked", said.Text, StringComparison.Ordinal);
        }
        finally
        {
            await DisposeAllAsync(residents);
        }
    }

    [Fact]
    public async Task A_refresh_then_a_stop_leaves_nothing_running()
    {
        var (residents, _, old, said) = await DuringOneTurnAsync(
            (catalogue, residents, output) => NodeCommand.RefreshAsync(
                new Wire.RefreshAgent { Agent = Agent, Because = "line accepted" }, catalogue, "alpha", residents, output, Ct),
            (catalogue, residents, output) => NodeCommand.StopAsync(
                new Wire.StopAgent { Agent = Agent, Because = "stuck" }, catalogue, "alpha", residents, output, Ct));

        try
        {
            Assert.False(old.IsAlive, "the old process is still running");
            Assert.True(residents.IsEmpty, $"something is running. The node said: {said.Text}");
            Assert.Contains("stopped once its turn was over, as a person asked", said.Text, StringComparison.Ordinal);
        }
        finally
        {
            await DisposeAllAsync(residents);
        }
    }

    [Fact]
    public async Task A_refresh_after_a_stop_does_not_undo_the_stop()
    {
        // The one exception to the latest winning: a refresh needs only that the process ends before the agent next starts.
        var (residents, _, old, said) = await DuringOneTurnAsync(
            (catalogue, residents, output) => NodeCommand.StopAsync(
                new Wire.StopAgent { Agent = Agent, Because = "stuck" }, catalogue, "alpha", residents, output, Ct),
            (catalogue, residents, output) => NodeCommand.RefreshAsync(
                new Wire.RefreshAgent { Agent = Agent, Because = "line accepted" }, catalogue, "alpha", residents, output, Ct));

        try
        {
            Assert.False(old.IsAlive, "the old process is still running");
            Assert.True(residents.IsEmpty, $"something is running. The node said: {said.Text}");
        }
        finally
        {
            await DisposeAllAsync(residents);
        }
    }

    // `parley serve` keeps one residents table for every project, each with its own node loop. Review, lens 1 run B on
    // 5de89187, 2 of 2: the waiter ran on the first asker's token, so when that project's node stopped, a later ending from
    // another project was dropped after the node had said it was what is done, and the old process ran on.
    [Fact]
    public async Task A_later_ending_from_another_project_is_done_when_the_first_askers_node_stops()
    {
        var (catalogue, identity, residents, old, turn, said) = await HoldingATurnAsync();

        try
        {
            using var projectA = new CancellationTokenSource();   // the first asker's node loop, which then stops
            await NodeCommand.StopAsync(
                new Wire.StopAgent { Agent = Agent, Because = "stuck" }, catalogue, "alpha", residents, said.Writer, projectA.Token);
            await NodeCommand.StopAsync(
                new Wire.StopAgent { Agent = Agent, Because = "stuck", Restart = true }, catalogue, "alpha", residents, said.Writer, Ct);
            await projectA.CancelAsync();

            _ = await turn;

            await UntilAsync(() => !old.IsAlive
                && residents.TryGetValue(identity, out var now) && now.IsAlive && !ReferenceEquals(now, old));

            Assert.False(old.IsAlive, $"the old process still runs. The node said: {said.Text}");
            Assert.True(
                residents.TryGetValue(identity, out var restarted) && restarted.IsAlive && !ReferenceEquals(restarted, old),
                $"nothing new is running. The node said: {said.Text}");
            Assert.Single(residents);
            Assert.Contains("restarted once its turn was over, as a person asked", said.Text, StringComparison.Ordinal);
        }
        finally
        {
            await DisposeAllAsync(residents);
        }
    }

    [Fact]
    public async Task A_later_stop_from_another_project_is_done_when_the_first_askers_node_stops()
    {
        var (catalogue, _, residents, old, turn, said) = await HoldingATurnAsync();

        try
        {
            using var projectA = new CancellationTokenSource();   // the first asker's node loop, which then stops
            await NodeCommand.StopAsync(
                new Wire.StopAgent { Agent = Agent, Because = "stuck", Restart = true }, catalogue, "alpha", residents, said.Writer, projectA.Token);
            await NodeCommand.StopAsync(
                new Wire.StopAgent { Agent = Agent, Because = "stuck" }, catalogue, "alpha", residents, said.Writer, Ct);
            await projectA.CancelAsync();

            _ = await turn;

            await UntilAsync(() => !old.IsAlive && residents.IsEmpty
                && said.Text.Contains("stopped once its turn was over", StringComparison.Ordinal));

            Assert.False(old.IsAlive, $"the old process still runs. The node said: {said.Text}");
            Assert.True(residents.IsEmpty, $"something is running. The node said: {said.Text}");
            Assert.Contains("stopped once its turn was over, as a person asked", said.Text, StringComparison.Ordinal);
        }
        finally
        {
            await DisposeAllAsync(residents);
        }
    }

    // Starts the agent and begins a three-second turn, returning once the turn is under way.
    private async Task<(NodeCatalogue Catalogue, AgentIdentity Identity, ConcurrentDictionary<AgentIdentity, ResidentAgent> Residents, ResidentAgent Old, Task<TurnOutcome> Turn, Said Said)>
        HoldingATurnAsync()
    {
        var (catalogue, identity, command) = Catalogued("docs");
        var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();
        var old = ResidentAgent.Start(
            AgentName.Create(Agent), InvocationCommand.Parse(command), _workspace, TimeProvider.System);
        residents[identity] = old;

        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn = old.TakeTurnAsync(
            "do the work",
            TimeSpan.FromSeconds(30),
            () =>
            {
                holding.TrySetResult();
                return Task.CompletedTask;
            },
            Ct);

        await holding.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        return (catalogue, identity, residents, old, turn, new Said());
    }

    // Polls until the condition holds or twenty seconds pass; the assertions that follow say what was not so.
    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, Ct);
        }
    }

    private delegate Task Asking(
        NodeCatalogue catalogue, ConcurrentDictionary<AgentIdentity, ResidentAgent> residents, TextWriter output);

    // Starts the agent, begins a three-second turn, asks for both endings while it runs, and waits until the node has done
    // whatever it is going to do.
    private async Task<(ConcurrentDictionary<AgentIdentity, ResidentAgent> Residents, AgentIdentity Identity, ResidentAgent Old, Said Said)>
        DuringOneTurnAsync(Asking first, Asking second)
    {
        var (catalogue, identity, command) = Catalogued("docs");
        var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();
        var old = ResidentAgent.Start(
            AgentName.Create(Agent), InvocationCommand.Parse(command), _workspace, TimeProvider.System);
        residents[identity] = old;
        var said = new Said();

        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn = old.TakeTurnAsync(
            "do the work",
            TimeSpan.FromSeconds(30),
            () =>
            {
                holding.TrySetResult();
                return Task.CompletedTask;
            },
            Ct);

        await holding.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        await first(catalogue, residents, said.Writer);
        await second(catalogue, residents, said.Writer);

        var finished = await turn;

        Assert.True(finished.Succeeded, finished.Describe());

        await said.UntilAsync("once its turn was over");

        // Long enough for an ending that was not dropped to have been done as well.
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);

        return (residents, identity, old, said);
    }

    private static async Task DisposeAllAsync(ConcurrentDictionary<AgentIdentity, ResidentAgent> residents)
    {
        foreach (var resident in residents.Values)
        {
            await resident.DisposeAsync();
        }
    }

    /// <summary>What the node said, read safely while it is still writing.</summary>
    private sealed class Said
    {
        private readonly StringWriter _written = new();

        public Said() => Writer = TextWriter.Synchronized(_written);

        public TextWriter Writer { get; }

        public string Text
        {
            get
            {
                lock (Writer)
                {
                    return _written.ToString();
                }
            }
        }

        public async Task UntilAsync(string text)
        {
            for (var waited = 0; waited < 300 && !Text.Contains(text, StringComparison.Ordinal); waited++)
            {
                await Task.Delay(100, Ct);
            }

            Assert.Contains(text, Text, StringComparison.Ordinal);
        }
    }

    private (NodeCatalogue Catalogue, AgentIdentity Identity, string Command) Catalogued(string role)
    {
        var stub = Path.Combine(_workspace, "stub.ps1");

        // A turn that takes three seconds, so both endings are asked while the first is waiting for it.
        File.WriteAllLines(stub,
        [
            "[Console]::Out.WriteLine('{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s\",\"model\":\"stub\"}'); [Console]::Out.Flush()",
            "while ($null -ne ($line = [Console]::In.ReadLine())) {",
            "  Start-Sleep -Seconds 3",
            "  [Console]::Out.WriteLine('{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"ok\",\"num_turns\":1,\"total_cost_usd\":0,\"session_id\":\"s\",\"permission_denials\":[]}'); [Console]::Out.Flush()",
            "}",
        ]);

        var command = "pwsh -NoProfile -NonInteractive -File \"" + stub + "\" --input-format stream-json";
        var identity = AgentIdentity.Mint();

        File.WriteAllText(
            Path.Combine(_workspace, LaunchConfiguration.FileName),
            $$"""
            {
              "mcpServers": {
                "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "{{role}}" } }
              },
              "{{LaunchConfiguration.IdentityMember}}": "{{identity}}"
            }
            """);

        var path = Path.Combine(_workspace, "node.json");

        File.WriteAllText(
            path,
            $$"""
            {
              "agents": {
                "{{identity}}": {
                  "command": {{System.Text.Json.JsonSerializer.Serialize(command)}},
                  "workspace": {{System.Text.Json.JsonSerializer.Serialize(_workspace)}}
                }
              }
            }
            """);

        var catalogue = NodeCatalogue.ReadFrom(path);
        Assert.True(catalogue.TryResolve(role, "alpha", out var resolved, out _));

        return (catalogue, resolved, command);
    }
}
