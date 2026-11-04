using System.Reflection;
using System.Text.Json.Nodes;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Configuration;
using Parley.Core;
using Parley.Store;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Terminal.Tests;

/// <summary>A window asks its own broker which project it is, and writes nothing else (SPEC.md §4.1.2).</summary>
/// <remarks>
/// The other project's entry is what is held here: a guess from the file would write to it, and the person would be told it worked.
/// </remarks>
[Integration]
public sealed class AWindowWritesToItsOwnProjectTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");

    private readonly string _root = Directory.CreateTempSubdirectory("parley-window-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string LaunchFile => Path.Combine(_root, ".mcp.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a green run over.
        }
    }

    private static async Task<string> SeatProjectAsync(BrokerClient client) =>
        Parley.Setup.BrokerProject.FromSeat((await client.RegistryAsync(Ct)).Select(row => row.Name.Value))!;

    private void WriteFile(string project) =>
        File.WriteAllText(
            LaunchFile,
            $$"""
            { "mcpServers": {
                "parley-{{project}}": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "{{project}}" } },
                "parley-other": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "other" } } },
              "parleyMachines": { "test": {
                "parley-{{project}}": { "PARLEY_BROKER": "http://127.0.0.1:1" },
                "parley-other": { "PARLEY_OWNS": "The other project's." } } } }
            """);

    private JsonNode Blocks => JsonNode.Parse(File.ReadAllText(LaunchFile))!["parleyMachines"]!["test"]!;

    private AgentRecord Registered() =>
        new(Orders, "test", AgentTier.Pull, _root, null, null, false, AgentStatus.Active);

    [Fact]
    public async Task Accepting_a_line_goes_to_the_project_the_broker_names()
    {
        using var connection = ParleyDatabase.OpenInMemory();
        await new AgentRepository(connection).UpsertAsync(Registered(), Ct);
        await using var broker = LoopbackOperator.Start(connection);
        using var driving = Driving.Open();
        using var channel = GrpcChannel.ForAddress(broker.Address);
        var client = new BrokerClient(new Wire.Operators.OperatorsClient(channel));
        var project = await SeatProjectAsync(client);

        WriteFile(project);

        await Ownership.AcceptAsync(
            driving.App,
            client,
            Orders,
            _root,
            ProjectManifest.WindowName,
            new OwnershipAcceptance("Mine.", OwnershipSource.Composed, null, null),
            Ct,
            _ => { },
            _ => Task.CompletedTask);

        Assert.Equal("Mine.", (string?)Blocks[$"parley-{project}"]!["PARLEY_OWNS"]);
        Assert.Equal("The other project's.", (string?)Blocks["parley-other"]!["PARLEY_OWNS"]);
    }

    private static Task RetireAsync(BrokerClient client, string name, string workspace, Func<string, Task> done) =>
        (Task)typeof(Withdrawing)
            .GetMethod("RetireAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [client, new AgentRow(name, "active", "pull", string.Empty, workspace, false), ProjectManifest.WindowName, null, Ct, done])!;

    [Fact]
    public async Task Retiring_takes_the_block_of_the_project_the_broker_names()
    {
        using var connection = ParleyDatabase.OpenInMemory();
        await new AgentRepository(connection).UpsertAsync(Registered(), Ct);
        await using var broker = LoopbackOperator.Start(connection);
        using var channel = GrpcChannel.ForAddress(broker.Address);
        var client = new BrokerClient(new Wire.Operators.OperatorsClient(channel));
        var project = await SeatProjectAsync(client);

        WriteFile(project);

        await RetireAsync(client, Orders.Value, _root, _ => Task.CompletedTask);

        Assert.Null(Blocks[$"parley-{project}"]);
        Assert.NotNull(Blocks["parley-other"]);
    }

    [Fact]
    public async Task Retiring_another_machines_agent_changes_nothing_here()
    {
        var elsewhere = AgentName.Create("orders@server");

        using var connection = ParleyDatabase.OpenInMemory();
        await new AgentRepository(connection).UpsertAsync(Registered() with { Name = elsewhere }, Ct);
        await using var broker = LoopbackOperator.Start(connection);
        using var channel = GrpcChannel.ForAddress(broker.Address);
        var client = new BrokerClient(new Wire.Operators.OperatorsClient(channel));

        WriteFile(await SeatProjectAsync(client));

        var before = File.ReadAllText(LaunchFile);
        string? said = null;

        await RetireAsync(client, elsewhere.Value, _root, text =>
        {
            said = text;

            return Task.CompletedTask;
        });

        Assert.Equal(before, File.ReadAllText(LaunchFile));
        Assert.Contains("server", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_broker_that_stops_answering_after_a_retire_loses_no_report()
    {
        using var connection = ParleyDatabase.OpenInMemory();
        await new AgentRepository(connection).UpsertAsync(Registered(), Ct);
        await using var broker = LoopbackOperator.Start(connection);
        using var channel = GrpcChannel.ForAddress(broker.Address);
        var plain = new BrokerClient(new Wire.Operators.OperatorsClient(channel));

        WriteFile(await SeatProjectAsync(plain));

        var client = new BrokerClient(new Wire.Operators.OperatorsClient(channel.CreateCallInvoker().Intercept(new NoRegistry())));
        var before = File.ReadAllText(LaunchFile);
        string? said = null;

        await RetireAsync(client, Orders.Value, _root, text =>
        {
            said = text;

            return Task.CompletedTask;
        });

        Assert.Contains("did not answer", said, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(LaunchFile));
    }

    private sealed class NoRegistry : Interceptor
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            TRequest request,
            ClientInterceptorContext<TRequest, TResponse> context,
            AsyncUnaryCallContinuation<TRequest, TResponse> continuation) =>
            context.Method.Name == "DescribeRegistry"
                ? throw new RpcException(new Status(StatusCode.Unavailable, "gone"))
                : continuation(request, context);
    }
}
