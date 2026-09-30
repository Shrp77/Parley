using System.Collections.Concurrent;
using Parley.Configuration;
using Parley.Core;
using Parley.Headless;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// What a node does when it is asked to refresh an agent (SPEC.md §11.1).
/// </summary>
/// <remarks>
/// <b>Only an agent the node started is refreshed.</b> One attached through somebody's editor window is not this
/// machine's to stop — a person is working in it — so the node says so and leaves it, and its new line waits for its
/// next start. The waiting-for-the-turn half is <c>RefreshWaitsForTheTurnTests</c>, where a stand-in process can hold a
/// turn open.
/// </remarks>
public sealed class NodeRefreshTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("parley-node-refresh").FullName;

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

    [Fact]
    public async Task An_agent_this_node_did_not_start_is_not_refreshed_and_it_says_so()
    {
        var catalogue = Catalogued("docs");
        var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();
        var reported = new StringWriter();

        await NodeCommand.RefreshAsync(
            new Wire.RefreshAgent { Agent = "docs@alpha", Because = "its first ownership line was accepted automatically" },
            catalogue,
            "alpha",
            residents,
            reported,
            Ct);

        Assert.Empty(residents);
        Assert.Contains("not refreshed", reported.ToString(), StringComparison.Ordinal);
        Assert.Contains("docs@alpha", reported.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unusable_name_is_ignored_rather_than_throwing()
    {
        // The wire carries a string, and a node that threw on one would take a machine off the air.
        var reported = new StringWriter();

        await NodeCommand.RefreshAsync(
            new Wire.RefreshAgent { Agent = "not a name" },
            Catalogued("docs"),
            "alpha",
            new ConcurrentDictionary<AgentIdentity, ResidentAgent>(),
            reported,
            Ct);

        Assert.Empty(reported.ToString());
    }

    private NodeCatalogue Catalogued(string role)
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
                  "command": "dotnet --info",
                  "workspace": {{System.Text.Json.JsonSerializer.Serialize(_workspace)}}
                }
              }
            }
            """);

        return NodeCatalogue.ReadFrom(path);
    }
}
