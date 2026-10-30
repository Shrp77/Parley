using System.Text.Json.Nodes;
using Grpc.Net.Client;
using Parley.Broker;
using Parley.Core;
using Parley.Server;
using Parley.Setup;
using Parley.Store;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// That a repository's unreadable launch configuration refuses its own agent's line and nothing else.
/// </summary>
/// <remarks>
/// <see cref="OwnershipLine.WriteAsync"/> promises to return rather than throw, even for a link, a server named
/// twice and a key written twice. The facilitator's pass calls it for each answer in turn after every answer has
/// been marked answered, so one such file would strand every answer after it —
/// neither accepted nor held, never visited again, and "Parley is deciding" on both surfaces for ever. A git pull is
/// enough to produce any of the three.
/// </remarks>
[Integration]
public sealed class OneBadLaunchConfigurationTests : IAsyncDisposable
{
    private const string Project = "alpha";

    private readonly string _root = Directory.CreateTempSubdirectory("parley-badlaunch-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AgentName Here(string role) => AgentName.Create($"{role}@{MachineName.Current}");

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return ValueTask.CompletedTask;
    }

    private string Repo(string name, string launch)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".mcp.json"), launch);
        return dir;
    }

    [Fact]
    public async Task A_server_named_twice_is_refused_rather_than_thrown()
    {
        var dir = Repo("orders", """
            { "mcpServers": {
                "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "orders" } },
                "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "orders" } } } }
            """);

        string? said;
        try
        {
            said = await OwnershipLine.WriteAsync(dir, Project, Here("orders"), "orders, nothing else", Ct);
        }
        catch (Exception e)
        {
            Assert.Fail($"THREW {e.GetType().FullName}: {e.Message}");
            return;
        }

        Assert.NotNull(said);
    }

    [Fact]
    public async Task A_key_written_twice_is_refused_rather_than_thrown()
    {
        var dir = Repo("orders", """
            { "mcpServers": { "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "orders" } } },
              "mcpServers": { } }
            """);

        try
        {
            var said = await OwnershipLine.WriteAsync(dir, Project, Here("orders"), "orders, nothing else", Ct);
            Assert.NotNull(said);
        }
        catch (Exception e)
        {
            Assert.Fail($"THREW {e.GetType().FullName}: {e.Message}");
        }
    }

    [Fact]
    public async Task A_linked_launch_configuration_is_refused_rather_than_thrown()
    {
        var dir = Path.Combine(_root, "orders");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(_root, "elsewhere.json");
        File.WriteAllText(target, """{ "mcpServers": { "parley-alpha": { "env": { "PARLEY_AGENT": "orders" } } } }""");

        try
        {
            File.CreateSymbolicLink(Path.Combine(dir, ".mcp.json"), target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("no symlink privilege: " + e.Message);
        }

        try
        {
            var said = await OwnershipLine.WriteAsync(dir, Project, Here("orders"), "orders, nothing else", Ct);
            Assert.NotNull(said);
        }
        catch (Exception e)
        {
            Assert.Fail($"THREW {e.GetType().FullName}: {e.Message}");
        }
    }

    [Theory]
    [InlineData("orders", "billing")]
    [InlineData("billing", "orders")]
    [InlineData("aaa", "zzz")]
    [InlineData("zzz", "aaa")]
    public async Task One_unreadable_launch_configuration_in_a_pass_strands_no_other_answer(string badRole, string goodRole)
    {
        var bad = Repo(badRole, $$"""
            { "mcpServers": {
                "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "{{badRole}}" } },
                "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "{{badRole}}" } } } }
            """);
        var good = Repo(goodRole, $$"""
            { "mcpServers": { "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "{{goodRole}}" } } } }
            """);

        using var store = ParleyDatabase.OpenInMemory();
        var runtime = BrokerRuntime.Over(store, BrokerOptions.Default, TimeProvider.System);
        await using var server = await ParleyServer.StartAsync(runtime, "http://127.0.0.1:0", cancellationToken: Ct);
        using var channel = GrpcChannel.ForAddress(server.Addresses[0]);
        var agents = new Wire.Agents.AgentsClient(channel);
        var operators = new Wire.Operators.OperatorsClient(channel);

        var streams = new List<IDisposable>();

        foreach (var (agent, ws) in new[] { (Here(badRole), bad), (Here(goodRole), good) })
        {
            var a = agents.Attach(new Wire.AttachRequest
            {
                Agent = agent.Value, Adapter = "test", Tier = Wire.AgentTier.Pull, Workspace = ws,
                ProtocolVersion = ProtocolVersion.Current
            }, cancellationToken: Ct);
            Assert.True(await a.ResponseStream.MoveNext(Ct));
            streams.Add(a);
        }

        var log = new StringWriter();
        await ServeCommand.FacilitateOnceAsync(runtime, Project, log, Ct);

        foreach (var agent in new[] { Here(badRole), Here(goodRole) })
        {
            var q = Assert.Single((await agents.DrainInboxAsync(
                new Wire.DrainInboxRequest { Agent = agent.Value }, cancellationToken: Ct)).Messages);
            Assert.True((await agents.ResultAsync(new Wire.ResultRequest
            {
                From = agent.Value, ExchangeId = q.Message.ExchangeId, ResponderModel = "m",
                PayloadJson = new JsonObject
                {
                    ["outcomes"] = new JsonArray(new JsonObject
                    {
                        ["id"] = PayloadShape.WholeRequest, ["outcome"] = "answered", ["text"] = agent.Role + " things"
                    })
                }.ToJsonString()
            }, cancellationToken: Ct)).Ok);
        }

        string? thrown = null;
        for (var pass = 0; pass < 3; pass++)
        {
            try
            {
                await ServeCommand.FacilitateOnceAsync(runtime, Project, log, Ct);
            }
            catch (Exception e)
            {
                thrown ??= $"pass {pass}: {e.GetType().Name}: {e.Message}";
            }
        }

        var questions = (await operators.FacilitationAsync(new Wire.Empty(), cancellationToken: Ct)).Questions;
        var billingFile = File.ReadAllText(Path.Combine(good, ".mcp.json"));
        var report =
            $"thrown={thrown ?? "none"}\nlog=\n{log}\nquestions=\n" +
            string.Join("\n", questions.Select(q => $"  {q.Agent} {q.State} because='{q.WaitingBecause}'")) +
            $"\nbilling owns written={billingFile.Contains("PARLEY_OWNS", StringComparison.Ordinal)}";

        foreach (var s in streams)
        {
            s.Dispose();
        }

        TestContext.Current.SendDiagnosticMessage(report);
        Assert.True(thrown is null && billingFile.Contains("PARLEY_OWNS", StringComparison.Ordinal), report);
    }
}
