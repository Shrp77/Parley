using System.Collections.Concurrent;
using Parley.Configuration;
using Parley.Core;
using Parley.Headless;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// That an agent restarted to read its new ownership line is given a turn, as a fresh start is.
/// </summary>
/// <remarks>
/// A Request arriving while a refresh waits for the old process to
/// exit asks for a turn the dying process cannot take, and the restart must give one — otherwise the broker's wake stays
/// claimed until a drain, so the mail sits uncollected with nothing to ask again. Driven with a stub agent that records
/// every turn it is given and takes two seconds to exit.
/// </remarks>
public sealed class ARefreshedAgentIsGivenATurnTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("parley-refresh-turn").FullName;

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
    public async Task A_turn_asked_for_while_a_refresh_stops_the_agent_reaches_the_agent_it_restarted()
    {
        var turns = Path.Combine(_workspace, "turns.txt");
        var command = Stub(turns, exitDelaySeconds: 2);
        var (catalogue, identity) = Catalogued("docs", command);
        var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();
        var old = ResidentAgent.Start(
            AgentName.Create("docs@alpha"), InvocationCommand.Parse(command), _workspace, TimeProvider.System);
        residents[identity] = old;

        await Task.Delay(1500, Ct);

        var said = TextWriter.Synchronized(new StringWriter());

        await NodeCommand.RefreshAsync(
            new Wire.RefreshAgent { Agent = "docs@alpha", Because = "a new line" }, catalogue, "alpha", residents, said, Ct);

        // A Request lands while the old process is on its way out: the broker sees it attached and asks for a turn.
        await Task.Delay(300, Ct);
        await NodeCommand.TurnAsync(
            new Wire.GiveTurn { Agent = "docs@alpha", Because = "a Request arrived" }, catalogue, "alpha", residents, said, Ct);

        // Waited for rather than slept on: the restart and its turn take a few seconds under load.
        string[] lines = [];
        ResidentAgent? current = null;

        for (var waited = 0; waited < 300 && lines.Length == 0; waited++)
        {
            await Task.Delay(100, Ct);

            current = residents.TryGetValue(identity, out var now) ? now : null;
            lines = current is not null && !ReferenceEquals(current, old) ? await TurnsAsync(turns) : [];
        }

        foreach (var resident in residents.Values)
        {
            await resident.DisposeAsync();
        }

        Assert.NotNull(current);
        Assert.NotSame(old, current);
        Assert.NotEmpty(lines);
    }

    private static async Task<string[]> TurnsAsync(string path)
    {
        try
        {
            return File.Exists(path) ? await File.ReadAllLinesAsync(path, Ct) : [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private string Stub(string turns, int exitDelaySeconds)
    {
        var path = Path.Combine(_workspace, "stub.ps1");

        File.WriteAllLines(path,
        [
            "[Console]::Out.WriteLine('{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s\",\"model\":\"stub\"}'); [Console]::Out.Flush()",
            "while ($null -ne ($line = [Console]::In.ReadLine())) {",
            $"  Add-Content -Path '{turns}' -Value $PID",
            "  [Console]::Out.WriteLine('{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"ok\",\"num_turns\":1,\"total_cost_usd\":0,\"session_id\":\"s\",\"permission_denials\":[]}'); [Console]::Out.Flush()",
            "}",
            $"Start-Sleep -Seconds {exitDelaySeconds}",
        ]);

        return "pwsh -NoProfile -NonInteractive -File \"" + path + "\" --input-format stream-json";
    }

    private (NodeCatalogue Catalogue, AgentIdentity Identity) Catalogued(string role, string command)
    {
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

        return (catalogue, resolved);
    }
}
