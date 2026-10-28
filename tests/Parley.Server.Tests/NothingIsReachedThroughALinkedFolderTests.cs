using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That nothing is read, moved or deleted through a recorded folder replaced by a link, and that a quarantine whose
/// records cannot be kept puts the file back (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <b>Found by review, round 50.</b> The sweep, a fetch and the person's removal all reached through a recorded folder
/// swapped for a junction: the sweep moved a file from outside into quarantine and the removal deleted one. And a commit
/// that failed after the move left a file in quarantine with nobody told.
/// </remarks>
public sealed class NothingIsReachedThroughALinkedFolderTests : IDisposable
{
    private const string Orders = "orders@alpha";
    private const string Billing = "billing@alpha";

    private readonly string _base = Directory.CreateTempSubdirectory("parley-probe50-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Text(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    public void Dispose()
    {
        try
        {
            foreach (var link in Directory.EnumerateDirectories(_base, "*", SearchOption.AllDirectories)
                         .Where(d => new DirectoryInfo(d).LinkTarget is not null).ToList())
            {
                Directory.Delete(link);
            }

            Directory.Delete(_base, recursive: true);
        }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static SqliteConnection ConnectionOf(BrokerRuntime runtime) =>
        (SqliteConnection)typeof(BrokerRuntime).GetField("_connection", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!;

    [Fact]
    public async Task A_quarantine_whose_records_cannot_be_kept_puts_the_file_back()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await FileWire.PlaceAsync(billing.Client, Billing, "terms.md", Text("net 30"), "pricing", Ct);
        _ = await FileWire.FetchAndReportAsync(orders.Client, Orders, "terms.md", "pricing", Ct);

        var path = Path.Combine(broker.Runtime.Files.FolderPath("pricing"), "terms.md");
        await File.WriteAllTextAsync(path, "net 99", Ct);

        var connection = ConnectionOf(broker.Runtime);
        var armed = 1;
        var fired = 0;
        SQLitePCL.raw.sqlite3_commit_hook(
            connection.Handle,
            _ =>
            {
                // Fail the first commit made after the file has left the location: the quarantine's.
                if (!File.Exists(path) && Interlocked.Exchange(ref armed, 0) == 1)
                {
                    Interlocked.Increment(ref fired);
                    return 1;
                }

                return 0;
            },
            null);

        Exception? thrown = null;

        try
        {
            await broker.Runtime.Sweeper.SweepAsync(Ct);
        }
        catch (Exception e)
        {
            thrown = e;
        }

        SQLitePCL.raw.sqlite3_commit_hook(connection.Handle, null, null);

        var operators = broker.ConnectAsOperator();
        var quarantined = Directory.Exists(broker.Runtime.Files.Quarantine)
            ? Directory.EnumerateFiles(broker.Runtime.Files.Quarantine, "*", SearchOption.AllDirectories).ToList()
            : [];
        var problems = (await operators.ListFileProblemsAsync(new Wire.ListFileProblemsRequest(), cancellationToken: Ct)).Problems;
        var listed = (await operators.ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct)).Files;

        // A second sweep, as the next ask would run.
        await broker.Runtime.Sweeper.SweepAsync(Ct);
        var problemsAfter = (await operators.ListFileProblemsAsync(new Wire.ListFileProblemsRequest(), cancellationToken: Ct)).Problems;
        var notice = (await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders }, cancellationToken: Ct)).FileNotices;

        var observed = $"fired={fired} thrown={thrown?.GetType().Name}:{thrown?.Message} inQuarantine={quarantined.Count} " +
                       $"problems={problems.Count} listed={listed.Count} problemsAfterSecondSweep={problemsAfter.Count} " +
                       $"notices={notice.Count} found='{notice.FirstOrDefault()?.Sha256Found}'";
        TestContext.Current.SendDiagnosticMessage(observed);

        Assert.Equal(1, fired);
        Assert.True(quarantined.Count == 0 || problemsAfter.Count > 0, "a file in quarantine with no problem: " + observed);
        Assert.True(notice.Count == 1 && notice[0].Sha256Found.Length == 64, "the fetcher was not told tampered: " + observed);
    }

    [Fact]
    public async Task A_recorded_folder_swapped_for_a_junction_is_not_entered_by_the_sweep()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "mklink");

        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await FileWire.PlaceAsync(billing.Client, Billing, "terms.md", Text("net 30"), "pricing", Ct);

        var outside = Directory.CreateDirectory(Path.Combine(_base, "outside")).FullName;
        var precious = Path.Combine(outside, "terms.md");
        await File.WriteAllTextAsync(precious, "the person's own terms, not Parley's", Ct);

        var folder = broker.Runtime.Files.FolderPath("pricing");
        Directory.Delete(folder, recursive: true);
        Run("mklink", $"/J \"{folder}\" \"{outside}\"");
        Assert.NotNull(new DirectoryInfo(folder).LinkTarget);

        await broker.Runtime.Sweeper.SweepAsync(Ct);

        var problems = (await broker.ConnectAsOperator().ListFileProblemsAsync(new Wire.ListFileProblemsRequest(), cancellationToken: Ct)).Problems;
        var observed = $"preciousExists={File.Exists(precious)} problems=[{string.Join(", ", problems.Select(p => p.Kind + ":" + p.Path + "->" + p.MovedTo))}] " +
                       $"linkStill={Directory.Exists(folder)}";
        TestContext.Current.SendDiagnosticMessage(observed);

        Assert.True(File.Exists(precious), "the sweep moved a file outside the location: " + observed);

        // The file placed is not behind the link, so it is gone and the link with it; nothing is a problem.
        Assert.Empty(problems);
        Assert.False(Directory.Exists(folder), "the sweep left the link: " + observed);
        Assert.Empty((await broker.ConnectAsOperator().ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct)).Files);
    }

    [Fact]
    public async Task A_fetch_through_a_recorded_folder_swapped_for_a_junction_reaches_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "mklink");

        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await FileWire.PlaceAsync(billing.Client, Billing, "terms.md", Text("net 30"), "pricing", Ct);

        var outside = Directory.CreateDirectory(Path.Combine(_base, "outside")).FullName;
        var precious = Path.Combine(outside, "terms.md");
        await File.WriteAllTextAsync(precious, "the person's own terms, not Parley's", Ct);

        var folder = broker.Runtime.Files.FolderPath("pricing");
        Directory.Delete(folder, recursive: true);
        Run("mklink", $"/J \"{folder}\" \"{outside}\"");

        using var call = orders.Client.FetchFile(
            new Wire.FetchFileRequest { Agent = Orders, Name = "terms.md", ConversationId = "pricing" }, cancellationToken: Ct);
        Wire.FileRefusal? refusal = null;

        while (await call.ResponseStream.MoveNext(Ct))
        {
            refusal ??= call.ResponseStream.Current.FrameCase == Wire.FetchFileFrame.FrameOneofCase.Refusal ? call.ResponseStream.Current.Refusal : null;
        }

        await broker.Runtime.Sweeper.IdleAsync();

        var observed = $"refusal={refusal?.Rejection} preciousExists={File.Exists(precious)}";
        TestContext.Current.SendDiagnosticMessage(observed);

        Assert.True(File.Exists(precious), "a fetch's sweep moved a file outside the location: " + observed);
    }

    [Fact]
    public async Task The_persons_removal_through_a_recorded_folder_swapped_for_a_junction_deletes_nothing_outside()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "mklink");

        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await FileWire.PlaceAsync(billing.Client, Billing, "terms.md", Text("net 30"), "pricing", Ct);

        var outside = Directory.CreateDirectory(Path.Combine(_base, "outside")).FullName;
        var precious = Path.Combine(outside, "terms.md");
        await File.WriteAllTextAsync(precious, "the person's own terms, not Parley's", Ct);

        var folder = broker.Runtime.Files.FolderPath("pricing");
        Directory.Delete(folder, recursive: true);
        Run("mklink", $"/J \"{folder}\" \"{outside}\"");

        var reply = await broker.ConnectAsOperator().RemoveFilesAsync(
            new Wire.RemoveFilesRequest { Files = { new Wire.FileRef { ConversationId = "pricing", Name = "terms.md" } } },
            cancellationToken: Ct);
        await broker.Runtime.Sweeper.IdleAsync();

        var observed = $"removed={reply.Removed.Count} refused={reply.Refused.Count} preciousExists={File.Exists(precious)}";
        TestContext.Current.SendDiagnosticMessage(observed);

        Assert.True(File.Exists(precious), "the person's removal deleted a file outside the location: " + observed);
    }

    [Fact]
    public async Task Idle_returns_with_no_sweep_ever_asked_and_after_disposal()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        var idle = broker.Runtime.Sweeper.IdleAsync();
        Assert.True(await Task.WhenAny(idle, Task.Delay(5000, Ct)) == idle, "IdleAsync never returned with nothing asked");

        await broker.Runtime.Sweeper.DisposeAsync();
        broker.Runtime.Sweeper.Ask();
        var after = broker.Runtime.Sweeper.IdleAsync();
        Assert.True(await Task.WhenAny(after, Task.Delay(5000, Ct)) == after, "IdleAsync never returned after disposal");
    }

    private static void Run(string command, string arguments)
    {
        using var made = Process.Start(new ProcessStartInfo("cmd.exe", $"/c {command} {arguments}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        made.WaitForExit();
        Assert.Equal(0, made.ExitCode);
    }
}
