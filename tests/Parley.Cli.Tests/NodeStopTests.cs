using System.Collections.Concurrent;
using Parley.Configuration;
using Parley.Core;
using Parley.Headless;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// What a node does when a person stops or restarts an agent it started (SPEC.md §11.1, the fourth way an agent ends).
/// </summary>
/// <remarks>
/// Driven with a stand-in agent process, as the refresh tests are: the stop goes through the same wait for the turn, so
/// what is held here is that a stop leaves nothing running and a restart leaves a new process.
/// </remarks>
[Integration]
public sealed class NodeStopTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("parley-node-stop").FullName;

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
    public async Task A_stop_ends_the_process_and_starts_nothing()
    {
        var (catalogue, identity, command) = Catalogued("docs");
        var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();
        var old = ResidentAgent.Start(
            AgentName.Create("docs@alpha"), InvocationCommand.Parse(command), _workspace, TimeProvider.System);
        residents[identity] = old;
        var said = new Said();

        try
        {
            await NodeCommand.StopAsync(
                new Wire.StopAgent { Agent = "docs@alpha", Because = "stuck" }, catalogue, "alpha", residents, said.Writer, Ct);

            await said.UntilAsync("as a person asked");

            Assert.False(residents.ContainsKey(identity), "it is still in the table");
            Assert.False(old.IsAlive, "its process is still running");
            Assert.Contains("next work for it starts it again", said.Text, StringComparison.Ordinal);

            // A freeze sends this same stop, and a frozen agent is started by nothing (review, lens 3 on fc26cb8a).
            Assert.Contains("unless it is frozen", said.Text, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var resident in residents.Values)
            {
                await resident.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task A_restart_replaces_the_process()
    {
        var (catalogue, identity, command) = Catalogued("docs");
        var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();
        var old = ResidentAgent.Start(
            AgentName.Create("docs@alpha"), InvocationCommand.Parse(command), _workspace, TimeProvider.System);
        residents[identity] = old;
        var said = new Said();

        try
        {
            await NodeCommand.StopAsync(
                new Wire.StopAgent { Agent = "docs@alpha", Because = "stuck", Restart = true },
                catalogue, "alpha", residents, said.Writer, Ct);

            await said.UntilAsync("restarted once its turn was over");

            Assert.True(residents.TryGetValue(identity, out var now), "nothing is running");
            Assert.NotSame(old, now);
            Assert.True(now.IsAlive);
        }
        finally
        {
            foreach (var resident in residents.Values)
            {
                await resident.DisposeAsync();
            }
        }
    }

    [Theory]
    [InlineData(1500)]
    [InlineData(0)]
    public async Task A_start_asked_for_while_the_process_is_still_going_is_made_once_it_has_gone(int lingers)
    {
        // The stop had closed the agent's input and the process had not exited yet, so the node still counted it as running
        // and gave the start to it as a turn. The turn came back "process died", the stop started nothing, and the broker,
        // told the agent was started, held every later wake for two minutes (review, lens 1 on 0.64.5). This is the start an
        // unfreeze asks for when the frozen agent's process detaches, and any message that arrives just after a person's stop.
        var (catalogue, identity, command) = Catalogued("docs", lingers);
        var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();
        var old = ResidentAgent.Start(
            AgentName.Create("docs@alpha"), InvocationCommand.Parse(command), _workspace, TimeProvider.System);
        residents[identity] = old;
        var said = new Said();

        try
        {
            await NodeCommand.StopAsync(
                new Wire.StopAgent { Agent = "docs@alpha", Because = "frozen" }, catalogue, "alpha", residents, said.Writer, Ct);

            // Not before the stop has closed its input: until then it is simply running, and a start is a turn.
            var deadline = DateTime.UtcNow.AddSeconds(20);

            while (!old.IsStopping && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10, Ct);
            }

            Assert.True(old.IsStopping, "the stop never began");

            await NodeCommand.StartAsync(
                new Wire.StartAgent { Agent = "docs@alpha", Because = "unfrozen" }, catalogue, "alpha", residents, said.Writer, Ct);

            deadline = DateTime.UtcNow.AddSeconds(30);

            while (DateTime.UtcNow < deadline
                && !(residents.TryGetValue(identity, out var current) && !ReferenceEquals(current, old) && current.IsAlive))
            {
                await Task.Delay(25, Ct);
            }

            Assert.True(residents.TryGetValue(identity, out var now), $"nothing is running. The node said: {said.Text}");
            Assert.NotSame(old, now);
            Assert.True(now.IsAlive, "the process started in its place is not running");
            Assert.False(old.IsAlive, "the stopped process is still running");
            Assert.Single(residents);
        }
        finally
        {
            foreach (var resident in residents.Values)
            {
                await resident.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task An_agent_this_node_is_not_running_is_not_stopped_and_it_says_so()
    {
        var (catalogue, _, _) = Catalogued("docs");
        var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();
        var said = new Said();

        await NodeCommand.StopAsync(
            new Wire.StopAgent { Agent = "docs@alpha" }, catalogue, "alpha", residents, said.Writer, Ct);

        Assert.Empty(residents);
        Assert.Contains("not stopped", said.Text, StringComparison.Ordinal);
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

    private (NodeCatalogue Catalogue, AgentIdentity Identity, string Command) Catalogued(string role, int lingers = 0)
    {
        var stub = Path.Combine(_workspace, "stub.ps1");

        File.WriteAllLines(stub,
        [
            "[Console]::Out.WriteLine('{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s\",\"model\":\"stub\"}'); [Console]::Out.Flush()",
            "while ($null -ne ($line = [Console]::In.ReadLine())) {",
            "  [Console]::Out.WriteLine('{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"ok\",\"num_turns\":1,\"total_cost_usd\":0,\"session_id\":\"s\",\"permission_denials\":[]}'); [Console]::Out.Flush()",
            "}",

            // A process that takes a while to go after its input closes, as a real agent does while it shuts its tools down.
            $"Start-Sleep -Milliseconds {lingers.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
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
