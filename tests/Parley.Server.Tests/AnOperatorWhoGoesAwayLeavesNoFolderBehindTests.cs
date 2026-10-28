using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a destroy or a purge whose caller goes away after its rows have gone still deletes or marks the folders they
/// named (SPEC.md §14).
/// </summary>
/// <remarks>
/// The steps after the commit took the call's cancellation, so a person closing the web page or the terminal at that
/// moment left the folder on disk with no row naming it and no mark, and no start deleted it. Each test holds the
/// store's turn three times so the operation's commit is through and its next step is waiting when the call is
/// cancelled.
/// </remarks>
public sealed class AnOperatorWhoGoesAwayLeavesNoFolderBehindTests
{
    private const string Orders = "orders@alpha";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Text(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    private static (Task Holding, TaskCompletionSource Release, Task Entered) Hold(LoopbackBroker broker)
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var holding = broker.Runtime.UseConnectionAsync(
            async (_, _, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                return 0;
            },
            Ct);

        return (holding, release, entered.Task);
    }

    [Fact]
    public async Task A_destroy_cancelled_after_its_rows_commit_leaves_no_folder_behind()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        var placed = await FileWire.PlaceAsync(orders.Client, Orders, "a.md", Text("one"), "bait", Ct);
        Assert.Equal(Wire.FileRejection.None, placed.Rejection);
        var folder = broker.Runtime.Files.FolderPath("bait");
        Assert.True(Directory.Exists(folder));

        var operators = broker.ConnectAsOperator();
        using var cts = new CancellationTokenSource();

        // H1 holds the gate: the destroy's folder read queues behind it.
        var h1 = Hold(broker);
        await h1.Entered;

        var destroy = operators.DestroyAsync(
            new Wire.ConversationRequest { ConversationId = "bait" }, cancellationToken: cts.Token).ResponseAsync;
        await Task.Delay(400, Ct);

        // H2 queues behind the folder read, so the destroy's own transaction queues behind H2.
        var h2 = Hold(broker);
        await Task.Delay(200, Ct);
        h1.Release.SetResult();
        await h1.Holding;
        await h2.Entered;
        await Task.Delay(400, Ct);

        // H3 queues behind the destroy's transaction, so the check of which rows went queues behind H3.
        var h3 = Hold(broker);
        await Task.Delay(200, Ct);
        h2.Release.SetResult();
        await h2.Holding;
        await h3.Entered;
        await Task.Delay(400, Ct);

        // The person's tool goes away now: after the rows went, before the folder is deleted.
        await cts.CancelAsync();
        await Task.Delay(400, Ct);
        h3.Release.SetResult();
        await h3.Holding;

        var outcome = "completed";
        try
        {
            await destroy;
        }
        catch (RpcException e)
        {
            outcome = e.StatusCode.ToString();
        }

        await Task.Delay(500, Ct);

        var listed = await operators.ListConversationsAsync(
            new Wire.ListConversationsRequest { IncludeArchived = true }, cancellationToken: Ct);
        var survives = listed.Conversations.Any(c => c.ConversationId == "bait");
        var files = await operators.ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct);

        var said = $"destroy={outcome}; conversation survives={survives}; folder on disk={Directory.Exists(folder)}; "
                   + $"listed={files.Files.Count}; not listed={files.FilesNotListed}";
        TestContext.Current.SendDiagnosticMessage(said);

        // The cancel must land after the commit, or the case is not the one held here.
        Assert.True(outcome == "Cancelled" && !survives, "the cancel did not land after the commit: " + said);
        Assert.False(Directory.Exists(folder), said);
    }

    [Fact]
    public async Task A_purge_cancelled_after_its_rows_commit_leaves_no_folder_behind()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        var placed = await FileWire.PlaceAsync(orders.Client, Orders, "a.md", Text("one"), "bait", Ct);
        Assert.Equal(Wire.FileRejection.None, placed.Rejection);
        var folder = broker.Runtime.Files.FolderPath("bait");

        var operators = broker.ConnectAsOperator();
        using var cts = new CancellationTokenSource();

        // The purge reads folders (1), purges (2), reads folders again (3), checks rows (4). Cancel while (3) waits.
        var h1 = Hold(broker);
        await h1.Entered;
        var purge = operators.PurgeAsync(new Wire.AgentRequest { Agent = Orders }, cancellationToken: cts.Token).ResponseAsync;
        await Task.Delay(400, Ct);

        var h2 = Hold(broker);
        await Task.Delay(200, Ct);
        h1.Release.SetResult();
        await h1.Holding;
        await h2.Entered;
        await Task.Delay(400, Ct);

        var h3 = Hold(broker);
        await Task.Delay(200, Ct);
        h2.Release.SetResult();
        await h2.Holding;
        await h3.Entered;
        await Task.Delay(400, Ct);

        await cts.CancelAsync();
        await Task.Delay(400, Ct);
        h3.Release.SetResult();
        await h3.Holding;

        var outcome = "completed";
        try
        {
            await purge;
        }
        catch (RpcException e)
        {
            outcome = e.StatusCode.ToString();
        }

        await Task.Delay(500, Ct);

        var said = $"purge={outcome}; folder on disk={Directory.Exists(folder)}";
        TestContext.Current.SendDiagnosticMessage(said);

        var stillRecorded = await broker.Runtime.UseConnectionAsync(
            (connection, _, ct) => new Parley.Store.SharedFileRepository(connection).GetConversationForFolderAsync("bait", cancellationToken: ct),
            Ct);

        Assert.True(outcome == "Cancelled" && stillRecorded is null, "the cancel did not land after the commit: " + said);
        Assert.False(Directory.Exists(folder), said);
    }
}
