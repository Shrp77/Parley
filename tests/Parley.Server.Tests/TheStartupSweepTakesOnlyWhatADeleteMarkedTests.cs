using System.Diagnostics;
using Grpc.Net.Client;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a starting broker deletes only the folders a destroy or a purge marked, and nothing a link reaches.
/// </summary>
/// <remarks>
/// <b>Found by review, round 19.</b> The first sweep deleted every folder the store did not record: a person's own
/// folder in the location (SPEC.md §9.5 expects one), and, after a store was restored from a backup, the files of every
/// conversation newer than the backup. Only a folder carrying <see cref="FileLocationStore.LeftBehind"/> goes now. The
/// link cases: the location's delete removes a junction or link, the folder itself included, never its target.
/// </remarks>
public sealed class TheStartupSweepTakesOnlyWhatADeleteMarkedTests : IDisposable
{
    private const string Inventory = "inventory@alpha";

    private readonly string _base = Directory.CreateTempSubdirectory("parley-sweep-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    [Fact]
    public void A_junction_left_in_the_location_is_removed_without_its_target()
    {
        var (location, outside) = Prepare();
        Junction(Path.Combine(location.Root, "linked"), outside);
        Mark(Path.Combine(location.Root, "linked"));

        location.RemoveLeftBehind([]);

        Assert.False(Directory.Exists(Path.Combine(location.Root, "linked")));
        Assert.True(File.Exists(Path.Combine(outside, "precious.txt")), "the junction's target lost its contents");
    }

    [Fact]
    public void A_destroy_of_a_folder_that_is_a_junction_removes_the_link_and_marks_nothing_outside()
    {
        // The delete walks a stack of its own and marks a folder before it starts; through a link, either would reach a
        // directory that is somebody else's.
        var (location, outside) = Prepare();
        Junction(Path.Combine(location.Root, "linked"), outside);

        location.DeleteOrMark("linked");

        Assert.False(Directory.Exists(Path.Combine(location.Root, "linked")));
        Assert.True(File.Exists(Path.Combine(outside, "precious.txt")), "the junction's target lost its contents");
        Assert.False(File.Exists(Path.Combine(outside, FileLocationStore.LeftBehind)), "the mark was written through the link");
    }

    [Fact]
    public async Task The_mark_is_written_before_anything_in_the_folder_is_deleted()
    {
        // A delete that stops because the process ends leaves only what was done before it, so a mark written after a
        // failure would never be written. A folder that deletes cleanly shows the order: the mark appears first.
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "FSEvents may coalesce a create and a delete into one event");

        var (location, _) = Prepare();
        var folder = Directory.CreateDirectory(Path.Combine(location.Root, "gone")).FullName;
        File.WriteAllText(Path.Combine(folder, "a.md"), "placed");
        var seen = new System.Collections.Concurrent.ConcurrentQueue<string>();

        using (var watcher = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName })
        {
            watcher.Created += (_, e) => seen.Enqueue("created " + e.Name);
            watcher.Deleted += (_, e) => seen.Enqueue("deleted " + e.Name);
            watcher.EnableRaisingEvents = true;

            location.DeleteOrMark("gone");

            for (var waited = 0; waited < 100 && !seen.Contains("deleted a.md"); waited++)
            {
                await Task.Delay(50, Ct);
            }
        }

        Assert.False(Directory.Exists(folder));
        Assert.Equal("created " + FileLocationStore.LeftBehind, seen.FirstOrDefault());
    }

    [Fact]
    public void A_link_under_the_marks_name_takes_no_write_to_the_file_it_reaches()
    {
        // The mark was written over whatever held its name, so a hard link put there by hand emptied a file outside the
        // location before the delete began. Windows only: the hard link is made with mklink, which needs no privilege.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "makes a hard link with mklink");

        var (location, outside) = Prepare();
        var folder = Directory.CreateDirectory(Path.Combine(location.Root, "gone")).FullName;
        var target = Path.Combine(outside, "precious.txt");

        using (var mklink = Process.Start(new ProcessStartInfo(
            "cmd.exe", $"/c mklink /H \"{Path.Combine(folder, FileLocationStore.LeftBehind)}\" \"{target}\"")
        {
            UseShellExecute = false, RedirectStandardOutput = true,
        })!)
        {
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }

        // Held open below the folder, so the delete stops and the folder keeps the mark written for it.
        var held = Directory.CreateDirectory(Path.Combine(folder, "sub")).FullName;
        File.WriteAllText(Path.Combine(held, "open.md"), "x");

        using (File.Open(Path.Combine(held, "open.md"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            location.DeleteOrMark("gone");
        }

        Assert.Equal("not Parley's", File.ReadAllText(target));
        Assert.True(File.Exists(Path.Combine(folder, FileLocationStore.LeftBehind)), "the folder was left unmarked");
    }

    [Theory]
    [InlineData("0.", false)]
    [InlineData("copied.", false)]
    [InlineData("sub ", true)]
    [InlineData("sub.", true)]
    public void A_destroy_deletes_an_entry_whose_name_Windows_trims_and_everything_beside_it(string name, bool directory)
    {
        // Windows drops a trailing dot or space from a path not written as \\?\, so such an entry could be neither read
        // nor deleted, and stopped the delete with the placed files after it still on disk.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "only Windows trims a name");

        var (location, _) = Prepare();
        var folder = Directory.CreateDirectory(Path.Combine(location.Root, "gone")).FullName;
        File.WriteAllText(Path.Combine(folder, "a.md"), "placed");
        var odd = @"\\?\" + Path.Combine(folder, name);

        if (directory)
        {
            Directory.CreateDirectory(odd);
            File.WriteAllText(odd + @"\inside.md", "x");
        }
        else
        {
            File.WriteAllText(odd, "x");
        }

        location.DeleteOrMark("gone");

        Assert.False(Directory.Exists(folder), "the folder stayed");
    }

    [Fact]
    public void A_junction_inside_an_unrecorded_folder_is_removed_without_its_target()
    {
        var (location, outside) = Prepare();
        var folder = Directory.CreateDirectory(Path.Combine(location.Root, "gone")).FullName;
        Junction(Path.Combine(folder, "deep"), outside);
        Mark(folder);

        location.RemoveLeftBehind([]);

        Assert.True(File.Exists(Path.Combine(outside, "precious.txt")), "the nested junction's target lost its contents");
    }

    [Fact]
    public void A_symbolic_link_left_in_the_location_is_removed_without_its_target()
    {
        var (location, outside) = Prepare();
        var folder = Directory.CreateDirectory(Path.Combine(location.Root, "gone")).FullName;

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(location.Root, "linked"), outside);
            Directory.CreateSymbolicLink(Path.Combine(folder, "deep"), outside);
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("symbolic links need privilege here");
        }

        Mark(Path.Combine(location.Root, "linked"));
        Mark(folder);

        location.RemoveLeftBehind([]);

        Assert.True(File.Exists(Path.Combine(outside, "precious.txt")), "the symbolic link's target lost its contents");
    }

    [Theory]
    [InlineData("stock.")]
    [InlineData("stock ")]
    public void A_marked_stray_whose_name_Windows_trims_does_not_take_a_live_folder_with_it(string stray)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows drops a trailing dot or space from a path");

        var (location, _) = Prepare();
        var live = Directory.CreateDirectory(Path.Combine(location.Root, "stock")).FullName;
        File.WriteAllText(Path.Combine(live, "stock.md"), "live stock levels");

        // As Git Bash's `mkdir 'stock.'` makes on NTFS, and marked, so the sweep would act on it.
        var odd = @"\\?\" + Path.Combine(location.Root, stray);
        Directory.CreateDirectory(odd);
        File.WriteAllText(Path.Combine(odd, FileLocationStore.LeftBehind), string.Empty);

        location.RemoveLeftBehind(["stock"]);

        Assert.True(File.Exists(Path.Combine(live, "stock.md")), "the live conversation's file was deleted by the sweep");
    }

    [Fact]
    public void An_unrecorded_folder_nothing_marked_is_left_alone()
    {
        var (location, _) = Prepare();
        var mine = Directory.CreateDirectory(Path.Combine(location.Root, "my-notes")).FullName;
        File.WriteAllText(Path.Combine(mine, "draft.md"), "the person's own words");

        location.RemoveLeftBehind([]);

        Assert.True(File.Exists(Path.Combine(mine, "draft.md")));
    }

    [Theory]
    [InlineData("STOCK")]
    [InlineData("Stock")]
    public void A_recorded_folder_whose_case_was_changed_by_hand_is_kept(string onDisk)
    {
        var (location, _) = Prepare();
        Mark(Directory.CreateDirectory(Path.Combine(location.Root, onDisk)).FullName);

        location.RemoveLeftBehind(["stock"]);

        Assert.True(Directory.Exists(Path.Combine(location.Root, onDisk)));
    }

    [Fact]
    public void A_location_reached_through_a_junction_keeps_its_recorded_folders()
    {
        var real = Directory.CreateDirectory(Path.Combine(_base, "real-location")).FullName;
        Directory.CreateDirectory(Path.Combine(real, "stock"));
        Mark(Directory.CreateDirectory(Path.Combine(real, "gone")).FullName);
        var linked = Path.Combine(_base, "alpha.db.files");
        Junction(linked, real);
        var location = new FileLocationStore(linked, Path.Combine(_base, "alpha.db.files-incoming"));
        location.Prepare();

        location.RemoveLeftBehind(["stock"]);

        Assert.True(Directory.Exists(Path.Combine(real, "stock")));
        Assert.False(Directory.Exists(Path.Combine(real, "gone")));
        Assert.True(Directory.Exists(linked));
    }

    [Fact]
    public async Task A_folder_the_person_made_in_the_location_survives_a_restart()
    {
        var store = Path.Combine(_base, "alpha.db");
        string mine;

        await using (var first = await StartAsync(store))
        {
            using var channel = GrpcChannel.ForAddress(first.Addresses[0]);
            var agents = new Wire.Agents.AgentsClient(channel);

            using (var stream = agents.Attach(
                new Wire.AttachRequest { Agent = Inventory, Adapter = "test", Tier = Wire.AgentTier.Pull, ProtocolVersion = ProtocolVersion.Current },
                cancellationToken: Ct))
            {
                Assert.True(await stream.ResponseStream.MoveNext(Ct));
                var placed = await FileWire.PlaceAsync(agents, Inventory, "stock.md", "stock"u8.ToArray(), "stock", Ct);
                Assert.Equal(Wire.PlaceOutcome.Placed, placed.Outcome);
            }

            // The person opened the location in their file browser (SPEC.md section 9.5) and made a folder of their
            // own beside the conversation's, which section 9.5 anticipates ("including one a person made by hand").
            mine = Directory.CreateDirectory(Path.Combine(first.Runtime.Files.Root, "my-notes")).FullName;
            await File.WriteAllTextAsync(Path.Combine(mine, "draft.md"), "the person's own words", Ct);
        }

        await using (var second = await StartAsync(store))
        {
            Assert.True(File.Exists(Path.Combine(second.Runtime.Files.FolderPath("stock"), "stock.md")));
            Assert.True(File.Exists(Path.Combine(mine, "draft.md")), "the person's own folder was deleted by a restart");
        }
    }

    [Fact]
    public async Task A_store_restored_from_a_backup_leaves_the_newer_conversations_files_on_disk()
    {
        var store = Path.Combine(_base, "alpha.db");
        var backup = Directory.CreateDirectory(Path.Combine(_base, "backup")).FullName;
        string ledger;

        await using (var first = await StartAsync(store))
        {
            await PlaceAsync(first, "stock.md", "stock");
        }

        // The person backs the store up while the broker is stopped.
        foreach (var file in Directory.EnumerateFiles(_base, "alpha.db*"))
        {
            File.Copy(file, Path.Combine(backup, Path.GetFileName(file)));
        }

        await using (var second = await StartAsync(store))
        {
            await PlaceAsync(second, "ledger.md", "ledger");
            ledger = Path.Combine(second.Runtime.Files.FolderPath("ledger"), "ledger.md");
            Assert.True(File.Exists(ledger));
        }

        // The store is damaged and the person restores the backup; the files location was never part of it.
        foreach (var file in Directory.EnumerateFiles(_base, "alpha.db*"))
        {
            File.Delete(file);
        }

        foreach (var file in Directory.EnumerateFiles(backup))
        {
            File.Copy(file, Path.Combine(_base, Path.GetFileName(file)));
        }

        await using (await StartAsync(store))
        {
            Assert.True(File.Exists(ledger), "a restore from backup deleted a newer conversation's files from disk");
        }
    }

    private static async Task PlaceAsync(ParleyServer server, string name, string conversation)
    {
        using var channel = GrpcChannel.ForAddress(server.Addresses[0]);
        var agents = new Wire.Agents.AgentsClient(channel);

        using var stream = agents.Attach(
            new Wire.AttachRequest { Agent = Inventory, Adapter = "test", Tier = Wire.AgentTier.Pull, ProtocolVersion = ProtocolVersion.Current },
            cancellationToken: Ct);
        Assert.True(await stream.ResponseStream.MoveNext(Ct));
        var placed = await FileWire.PlaceAsync(agents, Inventory, name, "words"u8.ToArray(), conversation, Ct);
        Assert.Equal(Wire.PlaceOutcome.Placed, placed.Outcome);
    }

    private (FileLocationStore Location, string Outside) Prepare()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_base, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "not Parley's");

        var location = new FileLocationStore(Path.Combine(_base, "alpha.db.files"), Path.Combine(_base, "alpha.db.files-incoming"));
        location.Prepare();

        return (location, outside);
    }

    private static void Mark(string folder) => File.WriteAllText(Path.Combine(folder, FileLocationStore.LeftBehind), string.Empty);

    private static void Junction(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
        Assert.True(new DirectoryInfo(link).LinkTarget is not null, "no junction was made");
    }

    private static async Task<ParleyServer> StartAsync(string store)
    {
        var runtime = BrokerRuntime.Open(ProjectName.Create("test"), store, Parley.Broker.BrokerOptions.Default, TimeProvider.System);

        return await ParleyServer.StartAsync(runtime, "http://127.0.0.1:0", Ct);
    }
}
