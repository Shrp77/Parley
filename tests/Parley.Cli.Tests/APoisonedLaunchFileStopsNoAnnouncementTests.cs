using System.Text.Json;
using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That one catalogued repository whose launch file holds text that is not valid is reported as unread, and the node
/// still announces every other.
/// </summary>
/// <remarks>
/// Lens 2 run 138. A lone surrogate escape at the top level, inside <c>mcpServers</c> or in a value threw
/// <see cref="InvalidOperationException"/> out of <see cref="LaunchConfiguration.TryRead(string?, out LaunchConfiguration, out string?)"/>, through
/// <see cref="NodeCatalogue.Announceable"/>, and ended the node, which under <c>parley up</c> stops every resident agent
/// on the machine. The value case was older than the strict parse: it threw when the agent's name was read.
/// </remarks>
[Integration]
public sealed class APoisonedLaunchFileStopsNoAnnouncementTests : IDisposable
{
    private const string Clean = """
        { "mcpServers": { "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "orders" } } } }
        """;

    private const string TopLevel = """
        { "mcpServers": { "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "billing" } } },
          "T\uD800": 1 }
        """;

    private const string InsideServers = """
        { "mcpServers": { "other\uD800": { "command": "node" },
          "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "billing" } } } }
        """;

    private const string InAValue = """
        { "mcpServers": { "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "\uD800billing" } } } }
        """;

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("parley-poisoned-");

    public static TheoryData<string> Poisoned => [TopLevel, InsideServers, InAValue];

    public void Dispose() => _root.Delete(recursive: true);

    [Theory]
    [MemberData(nameof(Poisoned))]
    public void The_clean_repository_is_announced_and_the_poisoned_one_reported(string poisoned)
    {
        var orders = AgentIdentity.Mint();
        var billing = AgentIdentity.Mint();
        var catalogue = NodeCatalogue.ReadFrom(Catalogue((orders, Workspace("orders", Clean)), (billing, Workspace("billing", poisoned))));

        var (names, contested, unread) = catalogue.Announceable(project: null);

        Assert.Equal(["orders"], names);
        Assert.Empty(contested);

        var reported = Assert.Single(unread);

        Assert.Equal(billing, reported.Identity);
        Assert.Equal(StrictJson.BrokenTextMessage, reported.Problem);
    }

    private string Workspace(string name, string launchFile)
    {
        var workspace = _root.CreateSubdirectory(name).FullName;

        File.WriteAllText(Path.Combine(workspace, LaunchConfiguration.FileName), launchFile);

        return workspace;
    }

    private string Catalogue(params (AgentIdentity Identity, string Workspace)[] agents)
    {
        var path = Path.Combine(_root.FullName, "node.json");
        var entries = agents.Select(agent =>
            $"\"{agent.Identity}\": {{ \"command\": \"claude\", \"workspace\": {JsonSerializer.Serialize(agent.Workspace)} }}");

        File.WriteAllText(path, "{ \"agents\": { " + string.Join(", ", entries) + " } }");

        return path;
    }
}
