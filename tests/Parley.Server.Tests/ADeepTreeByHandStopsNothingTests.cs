using System.Diagnostics;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a folder tree put in the file location by hand, deeper than a stack, neither stops the count of files nothing
/// placed nor a destroy's delete, and that a destroy which stops part way leaves its folder marked (SPEC.md §9.5, §14).
/// </summary>
/// <remarks>
/// The count of files nothing placed and the folder delete each took one call per level, so a tree a few thousand
/// levels deep overflowed the broker's stack and ended the process: on every list the person made, and on a
/// destroy after its rows had committed, leaving the folder on disk with no mark for a start to find. Windows only:
/// its paths reach 32,767 characters, where Linux and macOS stop a tree far shallower than the stack.
/// </remarks>
public sealed class ADeepTreeByHandStopsNothingTests : IDisposable
{
    private const string Orders = "orders@alpha";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Text(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Each walk took one call a level. On a thread with a small stack the old walks overflowed far shallower than on the
    // broker's, and the cost of a tree grows with the square of its depth (every call on a path resolves each of its
    // levels), so these run there: the same walks, a tree a CI runner deletes in seconds.
    private const int SmallStack = 256 * 1024;
    private const int Levels = 3_000;

    private readonly string _base = Directory.CreateTempSubdirectory("parley-deep-").FullName;

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

    // An overflow ends the process rather than throwing, so a walk that still took one call a level fails the run.
    private static void OnASmallStack(Action walk)
    {
        Exception? failed = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    walk();
                }
                catch (Exception e)
                {
                    failed = e;
                }
            },
            SmallStack);

        thread.Start();
        thread.Join();

        Assert.Null(failed);
    }

    private FileLocationStore Location()
    {
        var location = new FileLocationStore(Path.Combine(_base, "alpha.db.files"), Path.Combine(_base, "alpha.db.files-incoming"));
        location.Prepare();

        return location;
    }

    [Fact]
    public void The_count_reaches_a_file_at_the_bottom_of_the_tree()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "only a Windows path is long enough");

        var location = Location();
        var folder = Directory.CreateDirectory(Path.Combine(location.Root, "bait")).FullName;
        File.WriteAllText(Path.Combine(folder, "a.md"), "placed");
        _ = DeepTree(folder, Levels);
        var counted = 0;

        try
        {
            OnASmallStack(() => counted = FileWork.Unplaced(folder, new HashSet<string>(["a.md"], StringComparer.Ordinal)));

            Assert.Equal(1, counted);
        }
        finally
        {
            OnASmallStack(() => location.DeleteFolder("bait"));
        }
    }

    [Fact]
    public void A_destroy_deletes_the_tree_and_only_the_link_to_somewhere_outside()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "only a Windows path is long enough");

        var location = Location();
        var folder = Directory.CreateDirectory(Path.Combine(location.Root, "bait")).FullName;
        _ = DeepTree(folder, Levels);
        var outside = Directory.CreateDirectory(Path.Combine(_base, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "keep.md"), "not the location's");

        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(folder, "out")}\" \"{outside}\"")
        {
            UseShellExecute = false, RedirectStandardOutput = true,
        })!)
        {
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }

        OnASmallStack(() => location.DeleteOrMark("bait"));

        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(Path.Combine(outside, "keep.md")));
    }

    [Fact]
    public async Task A_destroy_that_stops_part_way_leaves_its_folder_marked()
    {
        // A file held open below the folder stops the delete part way, after the folder's own files have gone, and the
        // mark is what is left to tell a start to finish it: this holds that the mark is deleted last. That it is written
        // first is held by the sweep tests' The_mark_is_written_before_anything_in_the_folder_is_deleted.
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
