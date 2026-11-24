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

    [Fact]
    public async Task A_hostile_name_added_to_a_running_nodes_catalogue_is_printed_inert_on_the_rejoin_line()
    {
        // The rejoin line names what the node now offers, from the same launch files, on every catalogue change: printed raw,
        // a checkout enrolled while the node runs forged a line of its log (review, lens 3 sweep A on 44c8e1e6).
        var ct = TestContext.Current.CancellationToken;
        var hostile = "inv" + (char)0x1B + "[2J" + (char)0x2029 + "FORGED rejoin";
        var first = (AgentIdentity.Mint(), Workspace("one", "orders"));
        var path = Catalogue(first);

        using var store = Parley.Store.ParleyDatabase.OpenInMemory();
        await using var broker = LoopbackOperator.Start(store);
        using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(broker.Address);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var inner = new StringWriter();
        var output = TextWriter.Synchronized(inner);
        string Said() { lock (output) { return inner.ToString(); } }

        var node = NodeCommand.RunAsync(channel, broker.Address, path, output, cancellationToken: stopping.Token);

        try
        {
            await UntilAsync(() => Said().Contains("Can start:", StringComparison.Ordinal), "the node never joined: " + Said());

            _ = Catalogue(first, (AgentIdentity.Mint(), Workspace("two", hostile)));

            await UntilAsync(() => Said().Contains("Rejoining with:", StringComparison.Ordinal), "the node never rejoined: " + Said());
        }
        finally
        {
            await stopping.CancelAsync();

            try
            {
                _ = await node;
            }
            catch (Grpc.Core.RpcException ended) when (ended.StatusCode == Grpc.Core.StatusCode.Cancelled)
            {
                // Stopped by the test, which is how a node's loop ends.
            }
        }

        var rejoin = Said().Split(Environment.NewLine).Single(line => line.Contains("Rejoining with:", StringComparison.Ordinal));

        Assert.Contains("inv", rejoin, StringComparison.Ordinal);
        Assert.Contains("FORGED rejoin", rejoin, StringComparison.Ordinal);
        Assert.DoesNotContain(rejoin.EnumerateRunes(), PrintableLine.Breaks);
    }

    private static async Task UntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, because);
            await Task.Delay(50, TestContext.Current.CancellationToken);
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
