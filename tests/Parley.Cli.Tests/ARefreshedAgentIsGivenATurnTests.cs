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
[Integration]
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

    [Fact]
    public async Task An_agent_whose_folder_became_a_link_to_a_share_is_not_restarted_there()
    {
        // The agent's own answer asks for this restart, and its folder can be swapped for a link to a share while the old
        // process exits: the restart started nothing-checked in that link, and Windows offered the person's sign-in to
        // the host (review, lens 2 round 16). A TEST-NET host never answers, so a restart that went there waits on it.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "links to shares are a Windows sign-in");
        var turns = Path.Combine(_workspace, "turns.txt");
        var command = Stub(turns, exitDelaySeconds: 1);
        var (catalogue, identity) = Catalogued("docs", command);
        var running = Directory.CreateTempSubdirectory("parley-refresh-running").FullName;
        var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();
        var old = ResidentAgent.Start(
            AgentName.Create("docs@alpha"), InvocationCommand.Parse(command), running, TimeProvider.System);
        residents[identity] = old;
        var aside = _workspace + "-aside";
        var probe = _workspace + "-probe";

        try
        {
            Directory.CreateSymbolicLink(probe, running);
            Directory.Delete(probe);
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("symbolic links need Developer Mode here");
        }

        var written = new StringWriter();
        var said = TextWriter.Synchronized(written);
        var timer = System.Diagnostics.Stopwatch.StartNew();

        string Said()
        {
            lock (said)
            {
                return written.ToString();
            }
        }

        // The refresh reads the workspace to find the agent, then waits for the old process to end: the swap lands in
        // that wait, as an agent's helper does it.
        await NodeCommand.RefreshAsync(
            new Wire.RefreshAgent { Agent = "docs@alpha", Because = "a new line" }, catalogue, "alpha", residents, said, Ct);

        Directory.Move(_workspace, aside);
        Directory.CreateSymbolicLink(_workspace, $@"\\192.0.2.{Random.Shared.Next(2, 254)}\share");

        try
        {
            for (var waited = 0; waited < 150 && !Said().Contains("not restarted", StringComparison.Ordinal); waited++)
            {
                await Task.Delay(100, Ct);
            }

            Assert.Contains("leads to another machine", Said(), StringComparison.Ordinal);
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), $"took {timer.Elapsed}: the share was looked up");
            Assert.False(residents.TryGetValue(identity, out var now) && !ReferenceEquals(now, old), "it was restarted");
        }
        finally
        {
            foreach (var resident in residents.Values)
            {
                await resident.DisposeAsync();
            }

            if (new DirectoryInfo(_workspace).LinkTarget is not null)
            {
                Directory.Delete(_workspace);
            }

            Directory.Move(aside, _workspace);
            Directory.Delete(running, recursive: true);
        }
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
