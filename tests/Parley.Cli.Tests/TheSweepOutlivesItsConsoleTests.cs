using System.Collections.Concurrent;
using Parley.Configuration;
using Parley.Core;
using Parley.Headless;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// Stopping every agent a node started finishes when the console it reports to has gone.
/// </summary>
/// <remarks>
/// SIGHUP stops <c>serve</c> and <c>up</c> as Ctrl+C does because the terminal went away, and every write to a terminal
/// that went away fails with an I/O error. The sweep wrote "stopped." after the first agent, the write threw, the
/// process aborted, and every agent after the first was never told to quit (lens 1 run A on 6561e9ea, a real pty in
/// podman: exit 134, one of two agents told).
/// </remarks>
[Integration]
public sealed class TheSweepOutlivesItsConsoleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-sweepio-" + Path.GetRandomFileName());

    public TheSweepOutlivesItsConsoleTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Every_agent_is_stopped_when_the_console_has_gone()
    {
        var ct = TestContext.Current.CancellationToken;
        var entries = new List<string>();
        var roles = new[] { "orders", "billing", "inventory" };

        foreach (var role in roles)
        {
            var identity = AgentIdentity.Mint();
            var workspace = Path.Combine(_root, role);
            _ = Directory.CreateDirectory(workspace);

            await File.WriteAllTextAsync(
                Path.Combine(workspace, ".mcp.json"),
                $$"""
                {
                  "mcpServers": {
                    "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "{{role}}", "PARLEY_PROJECT": "alpha" } }
                  },
                  "{{LaunchConfiguration.IdentityMember}}": "{{identity}}"
                }
                """,
                ct);

            entries.Add(
                $$"""
                "{{identity}}": {
                  "command": {{System.Text.Json.JsonSerializer.Serialize(StandingAgent.Command)}},
                  "workspace": {{System.Text.Json.JsonSerializer.Serialize(workspace)}}
                }
                """);
        }

        var cataloguePath = Path.Combine(_root, "node.json");
        await File.WriteAllTextAsync(cataloguePath, "{ \"agents\": { " + string.Join(",", entries) + " } }", ct);

        var catalogue = NodeCatalogue.ReadFrom(cataloguePath);
        var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();

        foreach (var role in roles)
        {
            await NodeCommand.StartAsync(
                new Wire.StartAgent { Agent = role + "@" + NodeCatalogue.Machine, Because = "a peer asked" },
                catalogue, "alpha", residents, TextWriter.Null, ct);
        }

        var running = residents.Values.ToList();
        Assert.Equal(roles.Length, running.Count(r => r.IsAlive));

        Exception? thrown = null;

        try
        {
            await NodeCommand.StopEveryoneAsync(residents, new GoneConsole());
        }
        catch (IOException failure)
        {
            thrown = failure;
        }

        var left = running.Count(r => r.IsAlive);

        foreach (var resident in running)
        {
            await resident.DisposeAsync();
        }

        Assert.True(left == 0, $"{left} of {roles.Length} agents were never told to quit once the console had gone.");
        Assert.Null(thrown);
    }

    /// <summary>A console whose terminal went away: every write fails as a hung-up terminal's does.</summary>
    private sealed class GoneConsole : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value) => throw new IOException("Input/output error");

        public override void Write(string? value) => throw new IOException("Input/output error");

        public override void WriteLine(string? value) => throw new IOException("Input/output error");

        public override Task WriteLineAsync(string? value) => throw new IOException("Input/output error");
    }
}
