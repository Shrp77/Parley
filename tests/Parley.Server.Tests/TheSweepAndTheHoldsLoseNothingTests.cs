using System.Reflection;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a sweep and a collection's holds lose nothing to the timing around them (SPEC.md §6, §9.5).
/// </summary>
/// <remarks>
/// <b>Found by review, round 49.</b> A sweep judged a file against the row it read before its turn, so a name removed
/// and placed again was quarantined; an acknowledgement deleted a newer notice about the same file; an ask landing as a
/// run ended started nothing; and a sweep stopped part way moved a file to quarantine and recorded nothing.
/// </remarks>
[Integration]
public sealed class TheSweepAndTheHoldsLoseNothingTests
{
    private const string Orders = "orders@test";
    private const string Billing = "billing@test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Text(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    private static Wire.RemoveFilesRequest Remove(string agent, string name) =>
        new() { Agent = agent, Files = { new Wire.FileRef { ConversationId = "pricing", Name = name } } };

    private static bool HoldOverEverything(FileLocationStore files) =>
        typeof(FileLocationStore).GetField("_everything", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(files) is not null;

    [Fact]
    public async Task A_sweep_does_not_quarantine_a_file_placed_again_under_a_stale_rows_name()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        var files = broker.Runtime.Files;
        _ = await FileWire.PlaceAsync(billing.Client, Billing, "terms.md", Text("net 30"), "pricing", Ct);
        _ = await FileWire.PlaceAsync(billing.Client, Billing, "other.md", Text("other"), "pricing", Ct);
        var path = Path.Combine(files.FolderPath("pricing"), "terms.md");

        // A gate the sweep's hold over everything will have to wait for.
        var blocker = await files.ChangeAsync("pricing", Parley.Core.SharedFileName.Create("other.md"), Ct);

        // Deleted by hand: the sweep's first look finds it missing.
        File.Delete(path);
        var sweep = Task.Run(() => broker.Runtime.Sweeper.SweepAsync(Ct), Ct);

        while (!HoldOverEverything(files))
        {
            await Task.Delay(5, Ct);
        }

        // Meanwhile, through Parley: the placer removes the missing file and places new content under the name.
        var removed = await billing.Client.RemoveOwnFilesAsync(Remove(Billing, "terms.md"), cancellationToken: Ct);
        Assert.Single(removed.Removed);
        var again = await FileWire.PlaceAsync(billing.Client, Billing, "terms.md", Text("net 45"), "pricing", Ct);
        Assert.Equal(Wire.FileRejection.None, again.Rejection);
        Assert.True(File.Exists(path));

        blocker.Dispose();
        await sweep;

        var problems = (await broker.ConnectAsOperator().ListFileProblemsAsync(new Wire.ListFileProblemsRequest(), cancellationToken: Ct)).Problems;
        Assert.Empty(problems);
        Assert.True(File.Exists(path), "the file placed again was moved to quarantine");
    }

    [Fact]
    public async Task Acknowledging_a_held_notice_does_not_delete_a_newer_one_for_the_same_file()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await FileWire.PlaceAsync(billing.Client, Billing, "terms.md", Text("net 30"), "pricing", Ct);
        _ = await FileWire.FetchAndReportAsync(orders.Client, Orders, "terms.md", "pricing", Ct);
        _ = await billing.Client.RemoveOwnFilesAsync(Remove(Billing, "terms.md"), cancellationToken: Ct);

        // A collection in flight, holding the first gone notice.
        using var first = orders.Client.DrainInbox(cancellationToken: Ct);
        await first.RequestStream.WriteAsync(new Wire.DrainInboxFrame { Collect = new Wire.DrainInboxRequest { Agent = Orders } }, Ct);
        Assert.True(await first.ResponseStream.MoveNext(Ct));
        var held = Assert.Single(first.ResponseStream.Current.FileNotices);

        // Meanwhile (another session of orders): the name is placed again, fetched, and removed again.
        var placed = await FileWire.PlaceAsync(billing.Client, Billing, "terms.md", Text("net 45"), "pricing", Ct);
        _ = await FileWire.FetchAndReportAsync(orders.Client, Orders, "terms.md", "pricing", Ct);
        _ = await billing.Client.RemoveOwnFilesAsync(Remove(Billing, "terms.md"), cancellationToken: Ct);

        // Control: the newer notice is waiting before the acknowledgement.
        Assert.Equal(1, (await orders.Client.WaitingAsync(new Wire.WaitingRequest { Agent = Orders }, cancellationToken: Ct)).FileNoticesWaiting);

        await first.RequestStream.WriteAsync(new Wire.DrainInboxFrame { Acknowledge = new Wire.InboxAcknowledgement() }, Ct);
        await first.RequestStream.CompleteAsync();
        Assert.False(await first.ResponseStream.MoveNext(Ct));

        var after = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders }, cancellationToken: Ct);

        Assert.NotEqual(placed.Sha256, held.Sha256Had);
        var notice = Assert.Single(after.FileNotices);
        Assert.Equal(placed.Sha256, notice.Sha256Had);
    }

    [Fact]
    public async Task An_ask_landing_as_a_run_ends_is_never_lost()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        var sweeper = broker.Runtime.Sweeper;
        var again = typeof(FileSweeper).GetField("_again", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var active = typeof(FileSweeper).GetField("_active", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var running = typeof(FileSweeper).GetField("_running", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var gate = typeof(FileSweeper).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sweeper)!;
        var lost = 0;

        for (var round = 0; round < 4000 && lost == 0; round++)
        {
            sweeper.Ask();

            // Spin a little, so the second ask lands at a different point of the run's end each round.
            var until = System.Diagnostics.Stopwatch.GetTimestamp() + (round % 200) * 50;
            while (System.Diagnostics.Stopwatch.GetTimestamp() < until)
            {
            }

            sweeper.Ask();

            // Wait for the run to end, however it ends.
            Task current;
            lock (gate)
            {
                current = (Task)running.GetValue(sweeper)!;
            }

            await current.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await Task.Delay(1, Ct);

            lock (gate)
            {
                // An ask waiting with no run to serve it is lost: nothing else will ever start one.
                if ((bool)again.GetValue(sweeper)! && !(bool)active.GetValue(sweeper)!)
                {
                    lost = round + 1;
                }
            }
        }

        Assert.True(lost == 0, $"an ask was left with no run to serve it, in round {lost}");
    }

    [Fact]
    public async Task A_sweep_stopped_by_disposal_leaves_no_quarantined_file_unrecorded()
    {
        var inconsistent = new List<string>();

        for (var round = 0; round < 12 && inconsistent.Count == 0; round++)
        {
            await using var broker = await LoopbackBroker.StartAsync(Ct);
            await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
            var files = broker.Runtime.Files;

            for (var i = 0; i < 150; i++)
            {
                _ = await FileWire.PlaceAsync(billing.Client, Billing, $"f{i:D3}.md", Text("placed " + i), "pricing", Ct);
            }

            for (var i = 0; i < 150; i++)
            {
                await File.WriteAllTextAsync(Path.Combine(files.FolderPath("pricing"), $"f{i:D3}.md"), "tampered " + i, Ct);
            }

            broker.Runtime.Sweeper.Ask();

            // Stop it part way through its turn: once some files are in quarantine.
            var quarantine = Path.Combine(files.Quarantine, "pricing");
            var target = 1 + round * 10;

            while (!(Directory.Exists(quarantine) && Directory.GetFiles(quarantine).Length >= target))
            {
                await Task.Yield();
            }

            await broker.Runtime.Sweeper.DisposeAsync();

            var moved = Directory.GetFiles(quarantine).Length;
            var operators = broker.ConnectAsOperator();
            var problems = (await operators.ListFileProblemsAsync(new Wire.ListFileProblemsRequest(), cancellationToken: Ct)).Problems
                .Count(p => p.Kind == Wire.FileProblemKind.Quarantined);
            if (moved != problems)
            {
                inconsistent.Add($"round {round}: moved {moved}, quarantined problems {problems}");
            }
        }

        Assert.True(inconsistent.Count == 0, string.Join("; ", inconsistent));
    }
}
