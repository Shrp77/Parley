using System.Text.Json.Nodes;
using Grpc.Net.Client;
using Parley.Broker;
using Parley.Core;
using Parley.Server;
using Parley.Store;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// That a line a person records while the serve pass accepts answers is the line that stands.
/// </summary>
/// <remarks>
/// <para>
/// Recording the person's line and settling Parley's question were two gated units, and the pass's automatic accept
/// could run between them: the question still read as answered, so the agent's words went over the person's line,
/// marked automatic, while the person was told theirs was recorded (review, lens 1 run 78, about one round in twelve
/// on a real broker).
/// </para>
/// <para>
/// <b>Ordered, not raced.</b> The test holds the gate itself, queues the person's record behind it and the pass's
/// accept behind that, then lets go. The gate serves its waiters in the order they came, so with two units the accept
/// runs between them; with one it cannot. A record that reached the gate late only puts the accept first, which the
/// person's line then overwrites, so a slow runner can make this pass for the wrong reason but never fail for one.
/// </para>
/// </remarks>
[Integration]
public sealed class APersonsLineOutlastsThePassTests : IAsyncDisposable
{
    private const string Project = "alpha";

    private static readonly AgentName Orders = AgentName.Create($"orders@{MachineName.Current}");

    private readonly string _workspace = Directory.CreateTempSubdirectory("parley-outlasts-").FullName;
    private readonly Microsoft.Data.Sqlite.SqliteConnection _store = ParleyDatabase.OpenInMemory();
    private readonly BrokerRuntime _runtime;
    private readonly ParleyServer _server;
    private readonly GrpcChannel _channel;

    public APersonsLineOutlastsThePassTests()
    {
        File.WriteAllText(
            Path.Combine(_workspace, ".mcp.json"),
            $$"""
            {
              "mcpServers": {
                "parley-{{Project}}": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "orders" } }
              }
            }
            """);

        _runtime = BrokerRuntime.Over(_store, BrokerOptions.Default, TimeProvider.System);
        _server = ParleyServer.StartAsync(_runtime, "http://127.0.0.1:0").GetAwaiter().GetResult();
        _channel = GrpcChannel.ForAddress(_server.Addresses[0]);
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Dispose();
        await _server.DisposeAsync();
        _store.Dispose();

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
    public async Task A_line_the_person_was_told_is_recorded_is_not_replaced_by_the_automatic_one()
    {
        var agents = new Wire.Agents.AgentsClient(_channel);
        var operators = new Wire.Operators.OperatorsClient(_channel);

        using var attached = agents.Attach(
            new Wire.AttachRequest
            {
                Agent = Orders.Value,
                Adapter = "test",
                Tier = Wire.AgentTier.Pull,
                Workspace = _workspace,
                ProtocolVersion = ProtocolVersion.Current
            },
            cancellationToken: Ct);
        Assert.True(await attached.ResponseStream.MoveNext(Ct));

        // Parley asks; the agent answers; the pass reads the answer back.
        await ServeCommand.FacilitateOnceAsync(_runtime, Project, TextWriter.Null, Ct);
        var question = Assert.Single(
            (await agents.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders.Value }, cancellationToken: Ct)).Messages);
        var answered = await agents.ResultAsync(
            new Wire.ResultRequest
            {
                From = Orders.Value,
                ExchangeId = question.Message.ExchangeId,
                ResponderModel = "m",
                PayloadJson = new JsonObject
                {
                    ["outcomes"] = new JsonArray(new JsonObject
                    {
                        ["id"] = PayloadShape.WholeRequest, ["outcome"] = "answered", ["text"] = "Orders, and tax."
                    })
                }.ToJsonString()
            },
            cancellationToken: Ct);
        Assert.True(answered.Ok, answered.Refusal.ToString());
        var answer = Assert.Single(await _runtime.FacilitateAsync(Ct));

        // Hold the gate, queue the person's record, then the pass's accept, and let go.
        var holding = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var blocker = _runtime.UseConnectionAsync(
            async (_, _, _) =>
            {
                holding.SetResult();
                await release.Task;
                return 0;
            },
            Ct);
        await holding.Task;

        var record = operators.RecordOwnershipAsync(
            new Wire.RecordOwnershipRequest
            {
                Agent = Orders.Value,
                Statement = "Orders. Not tax.",
                Source = Wire.OwnershipSource.Edited,
                ProposedLength = 16,
                ConversationId = question.Message.ConversationId
            },
            cancellationToken: Ct).ResponseAsync;

        // Nothing observable says the call has reached the gate, so it is given time to; see the remarks.
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        var accept = _runtime.AcceptAutomaticallyAsync(answer, Ct);

        release.SetResult();
        await blocker;
        var told = await record;
        _ = await accept;

        Assert.True(told.Recorded);
        var row = Assert.Single(
            (await operators.DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct)).Rows,
            r => r.Name == Orders.Value);
        Assert.Equal("Orders. Not tax.", row.OwnsAccepted);
        Assert.Equal(Wire.OwnershipSource.Edited, row.OwnsSource);
    }
}
