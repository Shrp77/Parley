using System.Text;
using System.Text.Json.Nodes;
using Grpc.Net.Client;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Core;
using Parley.Store;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Terminal.Tests;

/// <summary>
/// Asking, disagreeing, and writing what was agreed rather than what was first said.
/// </summary>
/// <remarks>
/// <para>
/// <b>The whole act, over a socket and onto a real file.</b> Everything else about this feature is
/// tested a piece at a time; this is the one that would notice the pieces being wired together wrongly.
/// Parley asks, the agent proposes too much, the person disagrees through Parley, the agent revises, and what
/// reaches the repository's launch configuration is the revision. No seat anywhere: since 022 Parley asks, and
/// a person's reply goes through Parley recorded as theirs.
/// </para>
/// <para>
/// <b>Must fail against taking the first proposal</b>, which is the tempting implementation and the one
/// that puts in front of somebody the sentence they had just talked the agent out of — and then writes
/// it where every peer reads it. 0.30.1 records the same rule being needed for the room's own column.
/// </para>
/// <para>
/// <b>It asserts the file's bytes and the broker's record, never the screen.</b> A dialog can show the
/// right words and write the wrong ones; 0.30.3 records a message going out as
/// <c>@ordthe build is greeners@test</c> under a test that stopped at the screen and passed.
/// </para>
/// </remarks>
public sealed class SettlingItByDiscussingTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-settle-" + Path.GetRandomFileName());

    public SettlingItByDiscussingTests()
    {
        Directory.CreateDirectory(Workspace);

        File.WriteAllText(
            LaunchFile,
            """
            {
              "mcpServers": {
                "parley": {
                  "command": "parley-mcp",
                  "env": { "PARLEY_AGENT": "orders" }
                }
              }
            }
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

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

    private string Workspace => Path.Combine(_root, "orders");

    private string LaunchFile => Path.Combine(Workspace, ".mcp.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The registry row the write resolves its repository from.</summary>
    /// <remarks>
    /// The workspace comes from the registry because that is where it comes from in production -- what
    /// the agent said about itself on attach -- which is exactly why the write reads the role back out
    /// of the file before touching it. 005's gate found that being trusted too far.
    /// </remarks>
    private AgentRecord Registered() =>
        new(Orders, "mcp", AgentTier.Pull, Workspace, null, null, false, AgentStatus.Active);

    private string? Written()
    {
        var root = JsonNode.Parse(File.ReadAllText(LaunchFile))!;

        return root["mcpServers"]!["parley"]!["env"]!["PARLEY_OWNS"]?.GetValue<string>();
    }

    [Fact]
    public async Task What_is_written_is_the_revision_and_not_the_first_proposal()
    {
        // The agent is registered with the workspace the write lands in, because the workspace comes
        // from the registry -- which is what 005's gate found being trusted too far, and why the write
        // reads the role back out of the file before touching it.
        using var connection = ParleyDatabase.OpenInMemory();

        await new AgentRepository(connection).UpsertAsync(
            new AgentRecord(
                Orders, "mcp", AgentTier.Pull, Workspace, null, null, false, AgentStatus.Active),
            Ct);

        await using var broker = LoopbackOperator.Start(connection);
        using var driving = Driving.Open();
        using var directory = Wired(broker);

        var project = directory.Projects[0];

        // The agent has to be attached to answer, which is also true of the deployment: §4.0 makes an
        // open connection the evidence it is reachable.
        using var attached = broker.Agents.Attach(
            new Wire.AttachRequest
            {
                Agent = Orders.Value,
                Adapter = "test",
                Tier = Wire.AgentTier.Pull,
                Workspace = Workspace,
                ProtocolVersion = ProtocolVersion.Current,
            },
            cancellationToken: Ct);

        Assert.True(await attached.ResponseStream.MoveNext(Ct), "the attach stream said nothing");

        // 1. Parley asks.
        var discussion = await ParleyAsksAsync(broker);

        var asked = Assert.Single((await broker.Operators.DescribeWaitingAsync(
            new Wire.Empty(), cancellationToken: Ct)).Edges, e => e.WaitedOn == Orders.Value);

        // 2. The agent proposes something too broad, as a Clarify so the exchange stays open -- which is
        //    what lets the argument happen at all (§5.2, FR-009).
        await broker.Agents.ClarifyAsync(
            new Wire.ClarifyRequest
            {
                From = Orders.Value,
                ExchangeId = asked.ExchangeId,
                PayloadJson = """{"ask":"Orders, invoicing, stock and shipping."}""",
                ResponderModel = "claude-opus-5",
            },
            cancellationToken: Ct);

        // 3. The person disagrees, through Parley and recorded as the person's words.
        Assert.True((await broker.Operators.ReplyToAskedBackAsync(
            new Wire.ReplyToAskedBackRequest { Agent = Orders.Value, Text = "You do not own stock. Inventory does." },
            cancellationToken: Ct)).Done);

        // 4. The agent revises. This is what must be written.
        await broker.Agents.ResultAsync(
            new Wire.ResultRequest
            {
                From = Orders.Value,
                ExchangeId = asked.ExchangeId,
                PayloadJson =
                    """{"outcomes":[{"id":"request","outcome":"answered","text":"Orders and invoicing. Not stock."}]}""",
                ResponderModel = "claude-opus-5",
            },
            cancellationToken: Ct);

        // 5. The person accepts what is in front of them, unedited.
        //
        // **The queue is run rather than swallowed.** A first draft passed `_ => { }` here, so the
        // acceptance was handed to nothing and the test asserted against a file the surface had never
        // been asked to write -- it would have passed just as well with the write deleted.
        var work = new List<Task>();
        var said = string.Empty;

        await Ownership.ReviewAsync(
            driving.App,
            new BrokerClient(directory.ClientFor(project)),
            discussion,
            [Registered()],
            project.Name.Value,
            run => work.Add(run(Ct)),
            reported =>
            {
                said = reported;

                return Task.CompletedTask;
            },
            Ct);

        var dialog = (TextDialog)driving.Find(Ownership.AcceptTitle);

        // **The field opens holding the agent's own sentence**, which is what makes accepting one
        // keystroke -- and asserting it here is how "the latest thing it said" is checked at the place a
        // person actually reads it.
        Assert.Equal("Orders and invoicing. Not stock.", dialog.Typed);

        dialog.Finish(taken: true);

        await Task.WhenAll(work);

        // The file, not the screen.
        Assert.Equal("Orders and invoicing. Not stock.", Written());
        Assert.DoesNotContain("stock and shipping", Written() ?? string.Empty, StringComparison.Ordinal);

        // And it says peers have not read it yet, which is the half a person would otherwise assume.
        Assert.Contains("Recorded", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Leaving_the_discussion_without_accepting_writes_nothing()
    {
        // AC US2.4. **Must fail against a surface that writes on the way out**, and against one that
        // treats an empty field as "remove the line": abandoning an argument is not a decision that the
        // agent owns nothing, and the conversation stays readable either way.
        using var connection = ParleyDatabase.OpenInMemory();

        await new AgentRepository(connection).UpsertAsync(
            new AgentRecord(
                Orders, "mcp", AgentTier.Pull, Workspace, null, null, false, AgentStatus.Active),
            Ct);

        await using var broker = LoopbackOperator.Start(connection);
        using var driving = Driving.Open();
        using var directory = Wired(broker);

        var project = directory.Projects[0];
        var abandoned = new List<Task>();

        using var attached = broker.Agents.Attach(
            new Wire.AttachRequest
            {
                Agent = Orders.Value,
                Adapter = "test",
                Tier = Wire.AgentTier.Pull,
                Workspace = Workspace,
                ProtocolVersion = ProtocolVersion.Current,
            },
            cancellationToken: Ct);

        Assert.True(await attached.ResponseStream.MoveNext(Ct), "the attach stream said nothing");

        var discussion = await ParleyAsksAsync(broker);

        var asked = Assert.Single((await broker.Operators.DescribeWaitingAsync(
            new Wire.Empty(), cancellationToken: Ct)).Edges, e => e.WaitedOn == Orders.Value);

        await broker.Agents.ResultAsync(
            new Wire.ResultRequest
            {
                From = Orders.Value,
                ExchangeId = asked.ExchangeId,
                PayloadJson =
                    """{"outcomes":[{"id":"request","outcome":"answered","text":"Orders and invoicing."}]}""",
                ResponderModel = "claude-opus-5",
            },
            cancellationToken: Ct);

        await Ownership.ReviewAsync(
            driving.App,
            new BrokerClient(directory.ClientFor(project)),
            discussion,
            [Registered()],
            project.Name.Value,
            run => abandoned.Add(run(Ct)),
            _ => Task.CompletedTask,
            Ct);

        var dialog = (TextDialog)driving.Find(Ownership.AcceptTitle);

        dialog.Finish(taken: false);

        await Task.WhenAll(abandoned);

        // Nothing was queued at all, which is the stronger statement: leaving does not ask for work and
        // then have it do nothing.
        Assert.Empty(abandoned);
        Assert.Null(Written());

        // And the discussion is still there to come back to, which is what FR-011 needs of it.
        var export = await broker.Operators.ExportAsync(
            new Wire.ExportRequest { ConversationId = discussion }, cancellationToken: Ct);

        Assert.NotEmpty(export.Messages);
    }

    [Fact]
    public async Task A_line_overwritten_while_it_is_recorded_is_written_again_once_recorded()
    {
        // The serve pass can write the agent's own words over the file between the terminal's write and its record and
        // accept them; the person's record then wins in the store, so the file must be put right too, or the agent's
        // next start carries the words the person overruled (review, lens 1 run 79). The overwrite is made by the
        // call itself, just before the record leaves.
        using var connection = ParleyDatabase.OpenInMemory();
        await new AgentRepository(connection).UpsertAsync(Registered(), Ct);
        await using var broker = LoopbackOperator.Start(connection);
        using var driving = Driving.Open();
        using var channel = GrpcChannel.ForAddress(broker.Address);
        var overwriting = new BeforeRecording(() => Parley.Setup.OwnershipLine.WriteAsync(
            Workspace, null, Orders, "Orders, and tax.").GetAwaiter().GetResult());
        var client = new BrokerClient(new Wire.Operators.OperatorsClient(
            Grpc.Core.Interceptors.CallInvokerExtensions.Intercept(channel.CreateCallInvoker(), overwriting)));

        await Ownership.AcceptAsync(
            driving.App,
            client,
            Orders,
            Workspace,
            null,
            new OwnershipAcceptance("Orders only.", OwnershipSource.Composed, null, null),
            Ct,
            _ => { },
            _ => Task.CompletedTask);

        Assert.True(overwriting.Ran);
        Assert.Equal("Orders only.", Written());
    }

    /// <summary>Runs something just before a RecordOwnership call leaves, as the serve pass could.</summary>
    private sealed class BeforeRecording(Action before) : Grpc.Core.Interceptors.Interceptor
    {
        public bool Ran { get; private set; }

        public override Grpc.Core.AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            TRequest request,
            Grpc.Core.Interceptors.ClientInterceptorContext<TRequest, TResponse> context,
            AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
        {
            if (context.Method.Name == "RecordOwnership")
            {
                before();
                Ran = true;
            }

            return continuation(request, context);
        }
    }

    private static BrokerDirectory Wired(LoopbackOperator broker) =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), "unused.db", broker.Address, null)],
            _ => GrpcChannel.ForAddress(broker.Address));

    /// <summary>Has Parley ask orders what it owns, and returns the conversation it asked in.</summary>
    private static async Task<string> ParleyAsksAsync(LoopbackOperator broker)
    {
        Assert.True((await broker.Operators.AskAgainAsync(
            new Wire.AgentRequest { Agent = Orders.Value }, cancellationToken: Ct)).Done);

        return Assert.Single(
            (await broker.Operators.FacilitationAsync(new Wire.Empty(), cancellationToken: Ct)).Questions)
            .ConversationId;
    }
}
