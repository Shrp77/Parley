using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Parley.Core;
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
/// that failed after the move left a file in quarantine with nobody told. Lens 2 of the same round found the rule missed
/// at the roots: a location or staging directory already a link when the broker started had everything it reached
/// deleted, a quarantine directory that was a link was moved through, and a put-back that failed too left a file in
/// quarantine with no problem.
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

    [Theory]
    [InlineData(StoreFiles.Files)]
    [InlineData(StoreFiles.FilesIncoming)]
    [InlineData(StoreFiles.FilesQuarantine)]
    public async Task A_storage_directory_already_a_junction_when_the_broker_starts_is_not_entered(string suffix)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "mklink");

        var store = Path.Combine(_base, "alpha.db");
        var outside = Directory.CreateDirectory(Path.Combine(_base, "persons-documents")).FullName;
        var precious = Path.Combine(outside, "diary.md");
        await File.WriteAllTextAsync(precious, "the person's own", Ct);
        var photos = Directory.CreateDirectory(Path.Combine(outside, "photos")).FullName;
        Run("mklink", $"/J \"{store}{suffix}\" \"{outside}\"");

        await using (var server = await StartAsync(store))
        {
            await server.Runtime.Sweeper.IdleAsync();
        }

        var observed = $"preciousExists={File.Exists(precious)} photosExists={Directory.Exists(photos)} " +
                       $"stillLink={new DirectoryInfo(store + suffix).LinkTarget is not null}";
        TestContext.Current.SendDiagnosticMessage(observed);

        Assert.True(File.Exists(precious) && Directory.Exists(photos), "the broker deleted through a storage link: " + observed);
    }

    [Fact]
    public async Task A_location_swapped_for_a_junction_is_not_entered_by_the_sweep()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "mklink");

        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await FileWire.PlaceAsync(billing.Client, Billing, "terms.md", Text("net 30"), "pricing", Ct);

        // Behind the link, a folder and file of the recorded names with other bytes: never quarantined, never deleted.
        var outside = Directory.CreateDirectory(Path.Combine(_base, "outside")).FullName;
        var precious = Path.Combine(Directory.CreateDirectory(Path.Combine(outside, "pricing")).FullName, "terms.md");
        await File.WriteAllTextAsync(precious, "the person's own terms, not Parley's", Ct);
        var other = Path.Combine(outside, "notes.md");
        await File.WriteAllTextAsync(other, "the person's notes", Ct);

        var root = broker.Runtime.Files.Root;
        Directory.Delete(root, recursive: true);
        Run("mklink", $"/J \"{root}\" \"{outside}\"");

        await broker.Runtime.Sweeper.SweepAsync(Ct);

        var operators = broker.ConnectAsOperator();
        var problems = (await operators.ListFileProblemsAsync(new Wire.ListFileProblemsRequest(), cancellationToken: Ct)).Problems;
        var observed = $"preciousExists={File.Exists(precious)} otherExists={File.Exists(other)} " +
                       $"problems=[{string.Join(", ", problems.Select(p => p.Kind + ":" + p.Path))}] " +
                       $"rootIsLink={new DirectoryInfo(root).LinkTarget is not null}";
        TestContext.Current.SendDiagnosticMessage(observed);

        Assert.True(File.Exists(precious) && File.Exists(other), "the sweep reached through the location's link: " + observed);
        Assert.Empty(problems);
        Assert.Null(new DirectoryInfo(root).LinkTarget);
        Assert.Empty((await operators.ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct)).Files);
    }

    [Fact]
    public async Task A_quarantine_directory_swapped_for_a_junction_is_not_moved_through()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "mklink");

        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await FileWire.PlaceAsync(billing.Client, Billing, "terms.md", Text("net 30"), "pricing", Ct);
        await File.WriteAllTextAsync(Path.Combine(broker.Runtime.Files.FolderPath("pricing"), "terms.md"), "net 99", Ct);

        var outside = Directory.CreateDirectory(Path.Combine(_base, "elsewhere")).FullName;
        var quarantine = broker.Runtime.Files.Quarantine;

        if (Directory.Exists(quarantine))
        {
            Directory.Delete(quarantine, recursive: true);
        }

        Run("mklink", $"/J \"{quarantine}\" \"{outside}\"");

        await broker.Runtime.Sweeper.SweepAsync(Ct);

        var problems = (await broker.ConnectAsOperator().ListFileProblemsAsync(new Wire.ListFileProblemsRequest(), cancellationToken: Ct)).Problems;
        var landedOutside = Directory.EnumerateFiles(outside, "*", SearchOption.AllDirectories).Count();
        TestContext.Current.SendDiagnosticMessage($"landedOutside={landedOutside} problems={problems.Count}");

        Assert.Equal(0, landedOutside);
        Assert.Contains(problems, p => p.Kind == Wire.FileProblemKind.Quarantined);
        Assert.Single(Directory.EnumerateFiles(quarantine, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_quarantine_neither_recorded_nor_put_back_is_still_shown()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "icacls");

        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await FileWire.PlaceAsync(billing.Client, Billing, "terms.md", Text("net 30"), "pricing", Ct);
        _ = await FileWire.FetchAndReportAsync(orders.Client, Orders, "terms.md", "pricing", Ct);

        var folder = broker.Runtime.Files.FolderPath("pricing");
        var path = Path.Combine(folder, "terms.md");
        await File.WriteAllTextAsync(path, "net 99", Ct);

        // Another program lets files be moved out of the folder and none be added: the put-back fails.
        var sid = OperatingSystem.IsWindows() ? System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value : "";
        RunExe("icacls", $"\"{folder}\" /deny *{sid}:(WD)");

        var connection = ConnectionOf(broker.Runtime);
        var armed = 1;
        SQLitePCL.raw.sqlite3_commit_hook(
            connection.Handle,
            _ => !File.Exists(path) && Interlocked.Exchange(ref armed, 0) == 1 ? 1 : 0,
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
        finally
        {
            SQLitePCL.raw.sqlite3_commit_hook(connection.Handle, null, null);
            RunExe("icacls", $"\"{folder}\" /remove:d *{sid}");
        }

        await broker.Runtime.Sweeper.SweepAsync(Ct);

        var problems = (await broker.ConnectAsOperator().ListFileProblemsAsync(new Wire.ListFileProblemsRequest(), cancellationToken: Ct)).Problems;
        var quarantined = Directory.EnumerateFiles(broker.Runtime.Files.Quarantine, "*", SearchOption.AllDirectories).ToList();
        var observed = $"thrown={thrown?.GetType().Name} inQuarantine={quarantined.Count} " +
                       $"problems=[{string.Join(", ", problems.Select(p => p.Kind + "->" + p.MovedTo))}]";
        TestContext.Current.SendDiagnosticMessage(observed);

        // The commit's own failure is what the sweep reports, not the put-back's.
        Assert.IsType<SqliteException>(thrown);
        Assert.Single(quarantined);
        Assert.Contains(problems, p => p.Kind == Wire.FileProblemKind.Quarantined && p.MovedTo.Length > 0);
    }

    private static async Task<ParleyServer> StartAsync(string store)
    {
        var runtime = BrokerRuntime.Open(ProjectName.Create("test"), store, Parley.Broker.BrokerOptions.Default, TimeProvider.System);

        return await ParleyServer.StartAsync(runtime, "http://127.0.0.1:0", Ct);
    }

    private static void RunExe(string exe, string arguments)
    {
        using var made = Process.Start(new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        made.WaitForExit();
        Assert.Equal(0, made.ExitCode);
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
