using System.Diagnostics;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That the person's count of files nothing placed walks a folder without following a link out of it, looping, or
/// losing the count when part of it cannot be read (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// The walk took every file under a folder, so a link led it out of the location and a loop or an unreadable subfolder
/// threw part way, and the whole folder counted nothing, hand files beside it included. It ran inside the connection's
/// turn, so a link to a large tree stalled every agent's call. Windows only: the links and the denial are made with
/// mklink and icacls.
/// </remarks>
public sealed class TheNotListedCountWalksTheDiskSafelyTests
{
    private const string Orders = "orders@alpha";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Text(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    private static string Mklink(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink " + args)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return $"exit {p.ExitCode}: {o.Trim()}";
    }

    [Fact]
    public async Task A_link_looping_back_into_the_folder_is_one_entry_and_never_followed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "makes a junction with mklink");

        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        var ops = broker.ConnectAsOperator();

        await FileWire.PlaceAsync(orders.Client, Orders, "a.md", Text("one"), "bait", Ct);
        var folder = broker.Runtime.Files.FolderPath("bait");
        File.WriteAllText(Path.Combine(folder, "byhand.md"), "x");
        var made = Mklink($"/J \"{Path.Combine(folder, "loop")}\" \"{folder}\"");

        var sw = Stopwatch.StartNew();
        var listed = await ops.ListSharedFilesAsync(new Wire.ListFilesRequest { PageSize = 200 }, cancellationToken: Ct)
            .ResponseAsync.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.True(made.StartsWith("exit 0", StringComparison.Ordinal), made);
        Assert.Single(listed.Files);
        Assert.Equal(2, listed.FilesNotListed);
    }

    [Fact]
    public async Task A_subfolder_that_cannot_be_read_costs_nothing_of_the_count_beside_it()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "denies a folder's reading with icacls");

        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        var ops = broker.ConnectAsOperator();

        await FileWire.PlaceAsync(orders.Client, Orders, "a.md", Text("one"), "bait", Ct);
        var folder = broker.Runtime.Files.FolderPath("bait");
        File.WriteAllText(Path.Combine(folder, "byhand.md"), "x");

        var before = await ops.ListSharedFilesAsync(new Wire.ListFilesRequest { PageSize = 200 }, cancellationToken: Ct);

        var sub = Path.Combine(folder, "locked");
        Directory.CreateDirectory(sub);
        Run("icacls", $"\"{sub}\" /deny \"{Environment.UserName}:(OI)(CI)(R)\"");
        Wire.FilesReply during;

        try
        {
            during = await ops.ListSharedFilesAsync(new Wire.ListFilesRequest { PageSize = 200 }, cancellationToken: Ct);
        }
        finally
        {
            Run("icacls", $"\"{sub}\" /remove:d \"{Environment.UserName}\" /T");
        }

        Assert.Equal(1, before.FilesNotListed);
        Assert.Equal(1, during.FilesNotListed);
        Assert.Single(during.Files);
    }

    private static void Run(string file, string arguments)
    {
        using var process = Process.Start(
            new ProcessStartInfo(file, arguments) { UseShellExecute = false, RedirectStandardOutput = true })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
    }
}
