using System.Text.Json;
using Parley.Configuration;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>That the node's join report prints the names it announces with nothing a terminal or a log reader acts on.</summary>
/// <remarks>
/// A name comes from a repository's launch configuration (<c>PARLEY_AGENT</c>), which its agent can edit, and is not held to
/// the name grammar there. Printed raw, an escape sequence cleared the screen or wrote the clipboard, and a line separator
/// forged a line of the node's log, on every broker start and every rejoin (review, lens 2 run C on fc26cb8a).
/// </remarks>
[Integration]
public sealed class TheJoinLineIsInertTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("parley-join-inert-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public async Task A_hostile_name_in_a_launch_file_is_printed_inert_whether_announced_or_contested()
    {
        var hostile = "inv" + (char)0x1B + "[2J" + (char)0x1B + "]52;c;SGVsbG8=" + (char)0x07;
        var contested = "dup" + (char)0x2029 + "FORGED contested";

        var catalogue = NodeCatalogue.ReadFrom(Catalogue(
            (AgentIdentity.Mint(), Workspace("one", hostile)),
            (AgentIdentity.Mint(), Workspace("two", contested)),
            (AgentIdentity.Mint(), Workspace("three", contested))));

        var output = new StringWriter();

        await NodeCommand.ReportJoinedAsync(
            new Wire.Joined { Machine = "test" }, catalogue, project: null, "node.json", "http://127.0.0.1:1", output);

        var said = output.ToString();

        Assert.Contains("Can start:", said, StringComparison.Ordinal);
        Assert.Contains("claimed by more than one catalogued workspace", said, StringComparison.Ordinal);

        foreach (var line in said.Split(Environment.NewLine))
        {
            Assert.DoesNotContain(line.EnumerateRunes(), PrintableLine.Breaks);
        }
    }

    private string Workspace(string folder, string agent)
    {
        var workspace = _root.CreateSubdirectory(folder).FullName;
        var launch = new { mcpServers = new Dictionary<string, object> { ["parley"] = new { command = "parley-mcp", env = new { PARLEY_AGENT = agent } } } };

        File.WriteAllText(Path.Combine(workspace, LaunchConfiguration.FileName), JsonSerializer.Serialize(launch));

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
