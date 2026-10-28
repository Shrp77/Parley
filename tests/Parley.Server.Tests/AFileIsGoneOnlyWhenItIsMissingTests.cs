using Google.Protobuf;
using Grpc.Core;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a file is recorded gone only when it is missing from disk, and that what goes is the file that was named
/// (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// With no watcher, a list, a fetch or a start records a recorded file missing from disk as gone. Checked outside the
/// turn a placement's commit holds, a list recorded gone a file being placed again; resolved before a wait, a removal
/// deleted a newer file placed under the name meanwhile; and a folder that could not be read had every file recorded
/// gone for good, out of every list and every count.
/// </remarks>
public sealed class AFileIsGoneOnlyWhenItIsMissingTests
{
    private const string Orders = "orders@alpha";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Text(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    private static async Task<AsyncClientStreamingCall<Wire.PlaceFileFrame, Wire.PlaceFileReply>> BeginAsync(
        AttachedClient agent, string name, string words, string conversation)
    {
        var place = agent.Client.PlaceFile(cancellationToken: Ct);
        await place.RequestStream.WriteAsync(
            new Wire.PlaceFileFrame { Header = new Wire.PlaceFileHeader { Agent = agent.Name, Name = name, ConversationId = conversation } }, Ct);
        await place.RequestStream.WriteAsync(new Wire.PlaceFileFrame { Chunk = ByteString.CopyFrom(Text(words)) }, Ct);
        return place;
    }

    [Fact]
    public async Task A_list_never_records_gone_a_file_being_placed_again()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        var ops = broker.ConnectAsOperator();

        await FileWire.PlaceAsync(orders.Client, Orders, "a.md", Text("one"), "bait", Ct);
        var path = Path.Combine(broker.Runtime.Files.FolderPath("bait"), "a.md");
        File.Delete(path);

        using var place = await BeginAsync(orders, "a.md", "one", "bait");
        await Task.Delay(300, Ct);

        var release = new TaskCompletionSource();
        var occupied = broker.Runtime.UseConnectionAsync<bool>(async (_, _, _) => { await release.Task; return true; }, Ct);
        await Task.Delay(100, Ct);

        var listing = ops.ListSharedFilesAsync(new Wire.ListFilesRequest { PageSize = 200 }, cancellationToken: Ct).ResponseAsync;
        await Task.Delay(200, Ct);
        await place.RequestStream.CompleteAsync();
        await Task.Delay(300, Ct);
        release.SetResult();
        await occupied;

        var placed = await place.ResponseAsync.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        _ = await listing.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var after = await ops.ListSharedFilesAsync(new Wire.ListFilesRequest { PageSize = 200 }, cancellationToken: Ct);

        Assert.True(
            after.Files.Any(f => f.Name == "a.md") || !File.Exists(path),
            $"placement said {placed.Outcome}; listed afterwards: [{string.Join(",", after.Files.Select(f => f.Name))}]; "
            + $"on disk: {File.Exists(path)}; not listed: {after.FilesNotListed}");
    }

    [Fact]
    public async Task A_removal_resolved_before_a_wait_never_deletes_a_newer_file_under_the_name()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        var ops = broker.ConnectAsOperator();

        await FileWire.PlaceAsync(orders.Client, Orders, "x.md", Text("old"), "bait", Ct);
        var path = Path.Combine(broker.Runtime.Files.FolderPath("bait"), "x.md");

        var turn = await broker.Runtime.Files.ChangeAsync("bait", SharedFileName.Create("x.md"), Ct);

        var removal = ops.RemoveFilesAsync(
            new Wire.RemoveFilesRequest { Files = { new Wire.FileRef { ConversationId = "bait", Name = "x.md" } } },
            cancellationToken: Ct).ResponseAsync;
        await Task.Delay(300, Ct);

        // The old file deleted by hand, recorded gone by a list; the agent places a new version under the free name.
        File.Delete(path);
        _ = await ops.ListSharedFilesAsync(new Wire.ListFilesRequest { PageSize = 200 }, cancellationToken: Ct);
        var placed = await FileWire.PlaceAsync(orders.Client, Orders, "x.md", Text("new version"), "bait", Ct);

        turn.Dispose();
        var removed = await removal.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var after = await ops.ListSharedFilesAsync(new Wire.ListFilesRequest { PageSize = 200 }, cancellationToken: Ct);

        Assert.True(
            File.Exists(path) && after.Files.Any(f => f.Name == "x.md"),
            $"placement said {placed.Outcome} ({placed.Sha256[..8]}); removal removed {removed.Removed.Count}, freed {removed.BytesFreed}; "
            + $"on disk: {File.Exists(path)}; listed: [{string.Join(",", after.Files.Select(f => f.Name))}]");
    }

    [Fact]
    public async Task A_folder_that_cannot_be_read_keeps_its_files_as_recorded()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        var ops = broker.ConnectAsOperator();

        Assert.SkipUnless(OperatorSystemIsWindows, "denies a folder's reading with icacls");

        await FileWire.PlaceAsync(orders.Client, Orders, "u.md", Text("kept"), "bait", Ct);
        var folder = broker.Runtime.Files.FolderPath("bait");
        var user = Environment.UserName;

        Run("icacls", $"\"{folder}\" /deny \"{user}:(OI)(CI)(R)\"");
        Wire.FilesReply during;

        try
        {
            during = await ops.ListSharedFilesAsync(new Wire.ListFilesRequest { PageSize = 200 }, cancellationToken: Ct);
        }
        finally
        {
            Run("icacls", $"\"{folder}\" /remove:d \"{user}\" /T");
        }

        var after = await ops.ListSharedFilesAsync(new Wire.ListFilesRequest { PageSize = 200 }, cancellationToken: Ct);

        Assert.True(
            during.Files.Count == 1 && after.Files.Count == 1,
            $"while unreadable: listed {during.Files.Count}, not listed {during.FilesNotListed}; once readable again: "
            + $"listed {after.Files.Count}, not listed {after.FilesNotListed}; on disk: {File.Exists(Path.Combine(folder, "u.md"))}");
    }

    private static bool OperatorSystemIsWindows => OperatingSystem.IsWindows();

    private static void Run(string file, string arguments)
    {
        using var process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(file, arguments) { UseShellExecute = false, RedirectStandardOutput = true })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
    }
}
