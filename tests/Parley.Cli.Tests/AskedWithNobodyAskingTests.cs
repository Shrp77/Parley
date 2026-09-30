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
/// The owner's request, end to end: every agent is asked what it owns, and its answer becomes its line, with nobody
/// asking and no surface open (SPEC.md §10.2; FR-006, FR-011, FR-013, FR-014).
/// </summary>
/// <remarks>
/// <para>
/// <b>Through the serve host's own pass</b> (<see cref="ServeCommand.FacilitateOnceAsync"/>), with the agent attached and
/// answering over a real socket and the line written into a real repository's launch configuration — the one host that
/// can do both halves, which is why the work lives there.
/// </para>
/// <para>
/// <b>The answer is in paragraphs on purpose.</b> A model answers that way, and a line refuses a break, so without the
/// flatten Auto Accept would almost never accept anything (research finding 9). What is asserted is that the words
/// survive, only their whitespace changes, and the length recorded is the agent's own message.
/// </para>
/// </remarks>
public sealed class AskedWithNobodyAskingTests : IAsyncDisposable
{
    private const string Project = "alpha";

    private static readonly AgentName Orders = AgentName.Create($"orders@{MachineName.Current}");

    private const string Paragraphs = "1. Orders: taking them, pricing them, their totals.\n\n2. Not tax, which billing owns.";

    private readonly string _workspace = Directory.CreateTempSubdirectory("parley-asked-").FullName;
    private readonly Microsoft.Data.Sqlite.SqliteConnection _store = ParleyDatabase.OpenInMemory();
    private readonly BrokerRuntime _runtime;
    private readonly ParleyServer _server;
    private readonly GrpcChannel _channel;

    public AskedWithNobodyAskingTests()
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
    public async Task An_attached_agent_is_asked_and_its_answer_becomes_its_line()
    {
        var agents = new Wire.Agents.AgentsClient(_channel);

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

        var log = new StringWriter();

        // The first pass asks. Nobody chose to.
        await ServeCommand.FacilitateOnceAsync(_runtime, Project, log, Ct);

        var inbox = await agents.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders.Value }, cancellationToken: Ct);
        var question = Assert.Single(inbox.Messages);

        Assert.Contains("What do you own", question.Envelope, StringComparison.Ordinal);

        // The agent answers in paragraphs, as a model does.
        var answered = await agents.ResultAsync(
            new Wire.ResultRequest
            {
                From = Orders.Value,
                ExchangeId = question.Message.ExchangeId,
                ResponderModel = "some-model-9",
                PayloadJson = new JsonObject
                {
                    ["outcomes"] = new JsonArray(new JsonObject
                    {
                        ["id"] = PayloadShape.WholeRequest,
                        ["outcome"] = "answered",
                        ["text"] = Paragraphs
                    })
                }.ToJsonString()
            },
            cancellationToken: Ct);

        Assert.True(answered.Ok, answered.Refusal.ToString());

        // The second pass reads it back and makes it the line.
        await ServeCommand.FacilitateOnceAsync(_runtime, Project, log, Ct);

        const string Line = "1. Orders: taking them, pricing them, their totals. 2. Not tax, which billing owns.";

        // In the launch configuration, where the adapter reads it on its next start.
        var launch = JsonNode.Parse(File.ReadAllText(Path.Combine(_workspace, ".mcp.json")))!;
        Assert.Equal(Line, launch["mcpServers"]![$"parley-{Project}"]!["env"]!["PARLEY_OWNS"]!.GetValue<string>());

        // And in the record, as accepted automatically, with the agent's own length.
        var row = Assert.Single(
            (await new Wire.Operators.OperatorsClient(_channel).DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct)).Rows,
            r => r.Name == Orders.Value);

        Assert.Equal(Line, row.OwnsAccepted);
        Assert.Equal(Wire.OwnershipSource.Automatic, row.OwnsSource);
        Assert.Contains("is now its ownership line", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_Auto_Accept_off_the_answer_waits_and_nothing_is_written()
    {
        // T042's probe of the loop itself: must fail against a pass that ignored the switch.
        var operators = new Wire.Operators.OperatorsClient(_channel);
        _ = await operators.SetAutoAcceptAsync(new Wire.SetAutoAcceptRequest { On = false }, cancellationToken: Ct);

        var before = await File.ReadAllTextAsync(Path.Combine(_workspace, ".mcp.json"), Ct);
        var agents = new Wire.Agents.AgentsClient(_channel);

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

        var log = new StringWriter();
        await ServeCommand.FacilitateOnceAsync(_runtime, Project, log, Ct);

        var question = Assert.Single(
            (await agents.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders.Value }, cancellationToken: Ct)).Messages);

        // Asked with the switch off, the question keeps 020's promise: a person reads the answer first.
        Assert.Contains("A person will read your answer", question.Envelope, StringComparison.Ordinal);

        Assert.True((await agents.ResultAsync(
            new Wire.ResultRequest
            {
                From = Orders.Value,
                ExchangeId = question.Message.ExchangeId,
                ResponderModel = "some-model-9",
                PayloadJson = new JsonObject
                {
                    ["outcomes"] = new JsonArray(new JsonObject
                    {
                        ["id"] = PayloadShape.WholeRequest,
                        ["outcome"] = "answered",
                        ["text"] = Paragraphs
                    })
                }.ToJsonString()
            },
            cancellationToken: Ct)).Ok);

        await ServeCommand.FacilitateOnceAsync(_runtime, Project, log, Ct);

        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(_workspace, ".mcp.json"), Ct));

        var waiting = Assert.Single(
            (await operators.FacilitationAsync(new Wire.Empty(), cancellationToken: Ct)).Questions);

        Assert.Equal(Wire.QuestionState.Waiting, waiting.State);
        Assert.Contains("Auto Accept is off", waiting.WaitingBecause, StringComparison.Ordinal);
        Assert.Equal(Paragraphs, waiting.Answer);

        // Discarded, the agent is not asked again on the next pass: a person decided.
        Assert.True((await operators.DiscardAnswerAsync(
            new Wire.AgentRequest { Agent = Orders.Value }, cancellationToken: Ct)).Done);

        await ServeCommand.FacilitateOnceAsync(_runtime, Project, log, Ct);

        Assert.Empty((await agents.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = Orders.Value }, cancellationToken: Ct)).Messages);
    }
}
