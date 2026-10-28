using System.Diagnostics;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a folder tree put in the file location by hand, as deep as a path allows, neither stops the person's file list
/// nor a destroy, and that a destroy which stops part way leaves its folder marked (SPEC.md §9.5, §14).
/// </summary>
/// <remarks>
/// The count of files nothing placed and the folder delete each took one call per level, so a tree a few thousand
/// levels deep overflowed the broker's stack and ended the process: on every list the person made, and on a
/// destroy after its rows had committed, leaving the folder on disk with no mark for a start to find. Windows only:
/// its paths reach 32,767 characters, where Linux and macOS stop a tree far shallower than the stack.
/// </remarks>
public sealed class ADeepTreeByHandStopsNothingTests
{
    private const string Orders = "orders@alpha";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Text(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    // Each walk took one call a level, and in a Debug build overflowed at about 4,800 levels for the count and 8,100 for
    // the delete. Each test goes only a little past its own: every call on a path resolves each of its levels, so the
    // cost grows with the square of the depth.
    private const int PastTheCount = 5_500;
    private const int PastTheDelete = 9_000;

    // A tree of folders, with a file at the bottom.
    private static string DeepTree(string folder, int levels)
    {
        var path = new System.Text.StringBuilder(@"\\?\" + Path.Combine(folder, "d"));

        for (var level = 1; level < levels; level++)
        {
            path.Append(@"\d");
        }

        Directory.CreateDirectory(path.ToString());
        File.WriteAllText(Path.Combine(path.ToString(), "deep.md"), "x");

        return path.ToString();
    }

    [Fact]
    public async Task The_persons_list_counts_a_file_at_the_bottom_of_the_tree()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "only a Windows path is long enough");

        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await FileWire.PlaceAsync(orders.Client, Orders, "a.md", Text("one"), "bait", Ct);
        _ = DeepTree(broker.Runtime.Files.FolderPath("bait"), PastTheCount);

        var listed = await broker.ConnectAsOperator().ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct)
            .ResponseAsync.WaitAsync(TimeSpan.FromSeconds(120), Ct);

        Assert.Equal("a.md", Assert.Single(listed.Files).Name);
        Assert.Equal(1, listed.FilesNotListed);
    }

    [Fact]
    public async Task A_destroy_deletes_the_tree_and_only_the_link_to_somewhere_outside()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "only a Windows path is long enough");

        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await FileWire.PlaceAsync(orders.Client, Orders, "a.md", Text("one"), "bait", Ct);
        var folder = broker.Runtime.Files.FolderPath("bait");
        _ = DeepTree(folder, PastTheDelete);

        var outside = Directory.CreateTempSubdirectory("parley-outside-");

        try
        {
            File.WriteAllText(Path.Combine(outside.FullName, "keep.md"), "not the location's");
            using (var mklink = Process.Start(new ProcessStartInfo(
                "cmd.exe", $"/c mklink /J \"{Path.Combine(folder, "out")}\" \"{outside.FullName}\"")
            {
                UseShellExecute = false, RedirectStandardOutput = true,
            })!)
            {
                await mklink.WaitForExitAsync(Ct);
                Assert.Equal(0, mklink.ExitCode);
            }

            await broker.ConnectAsOperator().DestroyAsync(
                new Wire.ConversationRequest { ConversationId = "bait" }, cancellationToken: Ct)
                .ResponseAsync.WaitAsync(TimeSpan.FromSeconds(120), Ct);

            Assert.False(Directory.Exists(folder));
            Assert.True(File.Exists(Path.Combine(outside.FullName, "keep.md")));
        }
        finally
        {
            outside.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_destroy_that_stops_part_way_leaves_its_folder_marked()
    {
        // A file held open stops the delete as an overflow did: the folder stays, and only its mark tells a start to go.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a file held open cannot be deleted only on Windows");

        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await FileWire.PlaceAsync(orders.Client, Orders, "a.md", Text("one"), "bait", Ct);
        var folder = broker.Runtime.Files.FolderPath("bait");
        var bottom = DeepTree(folder, 3);

        using (File.Open(Path.Combine(bottom, "deep.md"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await broker.ConnectAsOperator().DestroyAsync(
                new Wire.ConversationRequest { ConversationId = "bait" }, cancellationToken: Ct)
                .ResponseAsync.WaitAsync(TimeSpan.FromSeconds(120), Ct);
        }

        Assert.True(Directory.Exists(folder));
        Assert.True(File.Exists(Path.Combine(folder, ".parley-pending-removal")));

        broker.Runtime.Files.RemoveLeftBehind([]);

        Assert.False(Directory.Exists(folder));
    }
}
