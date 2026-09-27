using System.Text;
using System.Text.Json.Nodes;
using Grpc.Net.Client;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Client.Views;
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
/// A person asks from their seat, the agent proposes, the person disagrees, the agent revises, and what
/// reaches the repository's launch configuration is the revision.
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
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        var project = directory.Projects[0];
        var discussion = OwnershipAsk.Conversation(Orders, new DateTimeOffset(2026, 9, 27, 9, 0, 0, TimeSpan.Zero));
        var voice = new SeatVoice(directory.AgentsFor(project), shell.Held!.Seat);

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

        // 1. The person asks.
        Assert.Contains("Sent.", await voice.RequestAsync(Orders, OwnershipAsk.Text, discussion, Ct), StringComparison.Ordinal);

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

        // 3. The person disagrees, in the same conversation and as an ordinary message.
        await voice.ClarifyAsync(asked.ExchangeId, "You do not own stock. Inventory does.", Ct);

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
            _ => (Workspace, null),
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
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        var project = directory.Projects[0];
        var discussion = OwnershipAsk.Conversation(Orders, new DateTimeOffset(2026, 9, 27, 9, 0, 0, TimeSpan.Zero));
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

        var voice = new SeatVoice(directory.AgentsFor(project), shell.Held!.Seat);

        await voice.RequestAsync(Orders, OwnershipAsk.Text, discussion, Ct);

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
            _ => (Workspace, null),
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

    private static BrokerDirectory Wired(LoopbackOperator broker) =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), "unused.db", broker.Address, null)],
            _ => GrpcChannel.ForAddress(broker.Address));

    private static async Task<Shell> SeatedAsync(
        Driving.Session driving, BrokerDirectory directory, SeatBook seats)
    {
        var shell = new Shell(directory) { Seats = seats };

        driving.Show(shell);
        await shell.RefreshAsync(Ct);
        await shell.Settled;

        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Join();

        var dialog = (TextDialog)driving.Find(AgentsRegion.JoinTitle);

        driving.Type("arvind");
        dialog.Finish(taken: true);

        await shell.Settled;

        Assert.NotNull(shell.Held);

        return shell;
    }
}
