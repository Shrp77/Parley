using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Configuration;
using Parley.Core;
using Parley.Headless;
using Parley.Store;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// That a node reports a turn under the name the asking project gave the agent, not the name of the project that started
/// its process (SPEC.md §11.6).
/// </summary>
/// <remarks>
/// Found by review, lens 2 run 108 and lens 1 run 106. One checkout is one process in every project that enrols it, under
/// a name in each, and the process carries the name of whichever started it. A turn another project asked for was
/// reported under that name, so that project recorded the wrong agent's turn: another agent showed as working, and the
/// one asked showed nothing.
/// </remarks>
[Integration]
public sealed class ATurnIsReportedUnderTheAskingProjectsNameTests : IAsyncDisposable
{
    private const string Project = "beta";
    private const string Token = "started-by-alpha";

    private static readonly AgentName StartedAs = AgentName.Create("billing@" + NodeCatalogue.Machine);
    private static readonly AgentName AskedAs = AgentName.Create("orders@" + NodeCatalogue.Machine);

    private readonly SqliteConnection _store = ParleyDatabase.OpenInMemory();
    private readonly LoopbackOperator _broker;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-asked-name-" + Path.GetRandomFileName());

    public ATurnIsReportedUnderTheAskingProjectsNameTests()
    {
        _broker = LoopbackOperator.Start(_store);
        _ = Directory.CreateDirectory(_root);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync().ConfigureAwait(false);
        _store.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task A_turn_another_project_asked_for_is_reported_under_that_projects_name()
    {
        var identity = AgentIdentity.Mint();
        var workspace = Path.Combine(_root, "checkout");
        _ = Directory.CreateDirectory(workspace);

        // Billing in the project that started it, orders in this one.
        await File.WriteAllTextAsync(
            Path.Combine(workspace, ".mcp.json"),
            $$"""
            {
              "mcpServers": {
                "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "billing", "PARLEY_PROJECT": "alpha" } },
                "parley-{{Project}}": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "{{Project}}" } }
              },
              "parleyAgentId": "{{identity}}"
            }
            """,
            Ct);

        var cataloguePath = Path.Combine(_root, NodeCatalogue.DefaultFileName);
        await File.WriteAllTextAsync(
            cataloguePath,
            "{ \"agents\": { \"" + identity + "\": { \"command\": "
            + System.Text.Json.JsonSerializer.Serialize(StandingAgent.Command) + ", \"workspace\": "
            + System.Text.Json.JsonSerializer.Serialize(workspace) + " } } }",
            Ct);

        // The process the other project started, which never answers, so its turn stays running.
        var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();
        await using var resident = ResidentAgent.Start(
            StartedAs,
            InvocationCommand.Parse(StandingAgent.Command),
            workspace,
            TimeProvider.System,
            new Dictionary<string, string> { [LaunchToken.Variable] = Token });
        residents[identity] = resident;

        using var channel = GrpcChannel.ForAddress(_broker.Address);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var node = NodeCommand.RunAsync(channel, _broker.Address, cataloguePath, TextWriter.Null, Project, residents, stopping.Token);

        try
        {
            // This project's adapter in that process, attached under this project's name, once its node has rejoined.
            using var attached = new Wire.Agents.AgentsClient(channel).Attach(
                new Wire.AttachRequest
                {
                    Agent = AskedAs.Value,
                    Adapter = "test",
                    Tier = Wire.AgentTier.Pull,
                    Identity = identity.ToString(),
                    LaunchToken = Token,
                    ProtocolVersion = ProtocolVersion.Current,
                },
                cancellationToken: Ct);
            Assert.True(await attached.ResponseStream.MoveNext(Ct));

            var asked = false;

            for (var tries = 0; tries < 50 && _broker.Runtime.TurnRunningSince(AskedAs) is null; tries++)
            {
                asked |= _broker.Runtime.RequestAttention(AskedAs, "a test asks for a turn");
                await Task.Delay(200, Ct);
            }

            Assert.True(asked, "the broker never asked the node for a turn, so this probe measured nothing");
            Assert.NotNull(_broker.Runtime.TurnRunningSince(AskedAs));
            Assert.Null(_broker.Runtime.TurnRunningSince(StartedAs));
        }
        finally
        {
            await stopping.CancelAsync();

            try
            {
                _ = await node;
            }
            catch (Exception ex) when (ex is OperationCanceledException
                                       || ex is RpcException { StatusCode: StatusCode.Cancelled })
            {
            }
        }
    }
}
