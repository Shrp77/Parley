using System.Collections.Concurrent;
using Parley.Configuration;
using Parley.Core;
using Parley.Headless;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// That a turn which ends without finishing is logged by how it ended, never by what the agent said.
/// </summary>
/// <remarks>
/// The combined review gate's finding (lens 2, run 4), older than the branch. The node printed the whole turn record, whose
/// default text carries <see cref="TurnOutcome.Reply"/> — the agent's words, which that type keeps out of every record on
/// purpose (§9.4) — into the broker's log, where nothing prunes them. And a line break in them forged a line of the log,
/// which a peer can steer, since a peer's payload shapes what a model says.
/// </remarks>
public sealed class AFailedTurnLogsNoWordsTests : IDisposable
{
    private const string Secret = "the invoice total is 4411";
    private const string Forged = "Node forged joined";

    private readonly string _workspace = Directory.CreateTempSubdirectory("parley-failed-turn").FullName;

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
    public async Task The_log_says_how_the_turn_ended_and_carries_none_of_its_words()
    {
        var command = Stub();
        var (catalogue, identity) = Catalogued("docs", command);
        var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();
        residents[identity] = ResidentAgent.Start(
            AgentName.Create("docs@alpha"), InvocationCommand.Parse(command), _workspace, TimeProvider.System);

        var log = new StringWriter();
        var said = TextWriter.Synchronized(log);

        await NodeCommand.TurnAsync(
            new Wire.GiveTurn { Agent = "docs@alpha", Because = "a Request arrived" }, catalogue, "alpha", residents, said, Ct);

        // The turn runs off the command loop; wait for its line rather than sleeping.
        for (var waited = 0; waited < 300 && !log.ToString().Contains("docs@alpha: turn", StringComparison.Ordinal); waited++)
        {
            await Task.Delay(100, Ct);
        }

        foreach (var resident in residents.Values)
        {
            await resident.DisposeAsync();
        }

        var text = log.ToString();

        Assert.Contains("agent reported an error", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Forged, text, StringComparison.Ordinal);
    }

    private string Stub()
    {
        var path = Path.Combine(_workspace, "stub.ps1");

        File.WriteAllLines(path,
        [
            "[Console]::Out.WriteLine('{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s\",\"model\":\"stub\"}'); [Console]::Out.Flush()",
            "while ($null -ne ($line = [Console]::In.ReadLine())) {",
            "  [Console]::Out.WriteLine('{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"is_error\":true,\"result\":\""
                + Secret + "\\n" + Forged + " http://127.0.0.1:1.\",\"num_turns\":1,\"total_cost_usd\":0,\"session_id\":\"s\",\"permission_denials\":[]}'); [Console]::Out.Flush()",
            "}",
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
