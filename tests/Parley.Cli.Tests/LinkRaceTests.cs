using Parley.Configuration;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>An agent flipping its own folder or launch configuration to a link to a share never gets it looked up.</summary>
/// <remarks>
/// <para>
/// The walk read every part of the path and refused a link off this machine, and an ordinary open afterwards followed
/// whatever stood there by then. A process flipping the folder, or <c>.mcp.json</c>, between a real one and a link to a
/// share won that gap within seconds, and Windows offered the person's sign-in to the host (review, lens 2 round 13).
/// </para>
/// <para>
/// The share is a TEST-NET host, which never answers: an open that followed the link takes the SMB wait, about 21
/// seconds, so a call that takes longer than a few is one that went looking. A fresh host each run, because Windows
/// answers a host it failed to reach a moment ago at once, which would hide a lookup.
/// </para>
/// </remarks>
[Integration]
[Slow]
public sealed class LinkRaceTests : IDisposable
{
    private static readonly TimeSpan Flipping = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan Looking = TimeSpan.FromSeconds(4);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-link-race-" + Guid.NewGuid().ToString("N"));
    private readonly string _share = $@"\\192.0.2.{Random.Shared.Next(2, 254)}\share";

    public LinkRaceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // Links first, by exact name, so nothing is deleted through one, and found without entering any: a walk into a link
        // to the share would wait on it.
        var entries = new List<string>();

        foreach (var entry in Directory.EnumerateFileSystemEntries(_root))
        {
            entries.Add(entry);

            if (new FileInfo(entry).LinkTarget is null && Directory.Exists(entry))
            {
                entries.AddRange(Directory.EnumerateFileSystemEntries(entry));
            }
        }

        foreach (var link in entries.Where(entry => new FileInfo(entry).LinkTarget is not null).ToList())
        {
            try
            {
                if (Directory.Exists(link))
                {
                    Directory.Delete(link);
                }
                else
                {
                    File.Delete(link);
                }
            }
            catch (IOException)
            {
                // Best effort.
            }
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public async Task A_folder_flipped_to_a_link_to_a_share_is_never_followed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "links to shares are a Windows sign-in");

        var repository = Path.Combine(_root, "repo");
        var aside = Path.Combine(_root, "repo-aside");

        Directory.CreateDirectory(repository);
        await File.WriteAllTextAsync(Path.Combine(repository, ".mcp.json"), "{}", TestContext.Current.CancellationToken);

        SkipUnlessLinksCanBeMade();

        await RaceAsync(
            () =>
            {
                Directory.Move(repository, aside);
                Directory.CreateSymbolicLink(repository, _share);
                Directory.Delete(repository);
                Directory.Move(aside, repository);
            },
            () =>
            {
                var (_, stream) = LocalOpen.File(Path.Combine(repository, ".mcp.json"), FileAccess.ReadWrite, FileShare.None);

                stream?.Dispose();
                _ = LocalOpen.Folder(repository);
            });
    }

    [Fact]
    public async Task A_scan_of_a_folder_whose_repository_flips_to_a_link_to_a_share_never_follows_it()
    {
        // The enrolment scan (the console's Enrol page in folder mode, `parley enroll scan`) checked each child and then
        // asked of its .mcp.json and .git by path; a child flipped between the two was followed to the share (review, lens
        // 3 round 12).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "links to shares are a Windows sign-in");

        var checkouts = Path.Combine(_root, "co");
        var repository = Path.Combine(checkouts, "orders");
        var aside = Path.Combine(_root, "orders-aside");

        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        await File.WriteAllTextAsync(Path.Combine(repository, ".mcp.json"), "{}", TestContext.Current.CancellationToken);

        SkipUnlessLinksCanBeMade();

        await RaceAsync(
            () =>
            {
                Directory.Move(repository, aside);
                Directory.CreateSymbolicLink(repository, _share);
                Directory.Delete(repository);
                Directory.Move(aside, repository);
            },
            () => _ = Parley.Setup.EnrollCommand.Find(checkouts));
    }

    [Fact]
    public async Task A_created_file_that_is_discarded_is_removed_through_its_own_handle()
    {
        // The undo of a failed first write deleted by path after closing; a folder swapped meanwhile redirected it.
        var folder = Directory.CreateDirectory(Path.Combine(_root, "discard")).FullName;
        var path = Path.Combine(folder, ".mcp.json");
        var created = LocalOpen.Create(path);

        await created.WriteAsync("{}"u8.ToArray(), TestContext.Current.CancellationToken);
        LocalOpen.Discard(created, path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Discarding_a_created_file_never_deletes_another_folders_file_of_that_name()
    {
        // A file is made through `repo`, a link to `real`; the link is then pointed at `other`, which holds a file of the
        // same name. Undone by path, the discard deleted `other`'s file and left the one it made (review, lens 3 round 15).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the handle-held delete is Windows'");

        SkipUnlessLinksCanBeMade();

        var real = Directory.CreateDirectory(Path.Combine(_root, "real")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(_root, "other")).FullName;
        var repository = Path.Combine(_root, "repo");
        var decoy = Path.Combine(other, ".mcp.json");

        await File.WriteAllTextAsync(decoy, "theirs", TestContext.Current.CancellationToken);
        Directory.CreateSymbolicLink(repository, real);

        var created = LocalOpen.Create(Path.Combine(repository, ".mcp.json"));

        await created.WriteAsync("ours"u8.ToArray(), TestContext.Current.CancellationToken);

        Directory.Delete(repository);
        Directory.CreateSymbolicLink(repository, other);

        try
        {
            LocalOpen.Discard(created, Path.Combine(repository, ".mcp.json"));

            Assert.True(File.Exists(decoy), "the other folder's file was deleted");
            Assert.Equal("theirs", await File.ReadAllTextAsync(decoy, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(real, ".mcp.json")), "the file it made was left");
        }
        finally
        {
            Directory.Delete(repository);
        }
    }

    [Fact]
    public async Task A_launch_configuration_flipped_to_a_link_to_a_share_is_never_followed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "links to shares are a Windows sign-in");

        var repository = Path.Combine(_root, "repo");
        var launch = Path.Combine(repository, ".mcp.json");
        var aside = Path.Combine(repository, "aside.json");

        Directory.CreateDirectory(repository);
        await File.WriteAllTextAsync(launch, "{}", TestContext.Current.CancellationToken);

        SkipUnlessLinksCanBeMade();

        await RaceAsync(
            () =>
            {
                File.Move(launch, aside);
                File.CreateSymbolicLink(launch, _share + @"\x.json");
                File.Delete(launch);
                File.Move(aside, launch);
            },
            () =>
            {
                // Shared, so the flipper can move the file while it is open: held exclusively, the opens alone kept the
                // flipper from flipping under load, and the race hardly ran.
                var (_, stream) = LocalOpen.File(launch, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                stream?.Dispose();
            });
    }

    [Fact]
    public void A_link_whose_target_holds_a_tilde_behind_a_link_to_a_share_is_never_looked_up()
    {
        // Path.GetFullPath expands short names whenever a path holds `~`, through GetLongPathNameW, which walks the real
        // path and follows every link on it before a part has been read: no race needed (review, lens 2 round 14).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "links to shares are a Windows sign-in");

        SkipUnlessLinksCanBeMade();

        var shareLink = Path.Combine(_root, "sharelink");
        var repository = Path.Combine(_root, "repo");
        var launch = Path.Combine(_root, "file-repo", ".mcp.json");

        Directory.CreateSymbolicLink(shareLink, _share);
        Directory.CreateSymbolicLink(repository, Path.Combine(shareLink, "x~y"));
        Directory.CreateDirectory(Path.GetDirectoryName(launch)!);
        File.CreateSymbolicLink(launch, Path.Combine(shareLink, "x~y.json"));

        var timer = System.Diagnostics.Stopwatch.StartNew();

        Assert.Equal(LocalOpenStatus.OffThisMachine, LocalOpen.Folder(repository));
        Assert.Equal(LocalOpenStatus.OffThisMachine, LocalOpen.File(launch, FileAccess.Read, FileShare.Read, followLink: true).Status);
        Assert.True(OffMachineLinks.LeadsOffThisMachine(repository));
        Assert.NotNull(LocalStorage.Problem(repository));
        Assert.True(timer.Elapsed < Looking, $"took {timer.Elapsed}: a short name was expanded through the link to the share");
    }

    [Fact]
    public async Task A_file_another_program_holds_is_said_to_be_there_never_read_as_absent()
    {
        // Read as no text, a held .mcp.json made a plan say *create* for a file that exists (review, lens 1 round 12).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows enforces a sharing mode on every open");
        var launch = Path.Combine(_root, ".mcp.json");

        await File.WriteAllTextAsync(launch, "{}", TestContext.Current.CancellationToken);

        using (new FileStream(launch, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => LocalOpen.ReadText(launch, json: true));
        }

        Assert.Equal("{}", LocalOpen.ReadText(launch, json: true).Text);
    }

    [Fact]
    public void A_folder_whose_real_name_ends_in_a_dot_never_lets_a_link_inside_it_pass_as_local()
    {
        // A name ending in a dot can be typed only by its short name. Its long name, read from its handle, was tidied without
        // the dot, so a relative link followed from inside it was resolved in the decoy beside it, while Windows resolved
        // it in `evil.`, to the share (review, lens 2 round 16).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Win32 path rules");

        SkipUnlessLinksCanBeMade();

        var real = Path.Combine(_root, "orders.real");
        var dotted = Path.Combine(real, "evil-folder-name.");
        var orders = Path.Combine(_root, "orders");

        Directory.CreateDirectory(@"\\?\" + dotted);
        Directory.CreateDirectory(Path.Combine(real, "evil-folder-name", "sub"));
        Directory.CreateSymbolicLink(@"\\?\" + Path.Combine(dotted, "sub"), _share);
        Directory.CreateSymbolicLink(@"\\?\" + Path.Combine(dotted, "r"), "sub");

        try
        {
            var shortName = new char[1024];
            var length = GetShortPathName(@"\\?\" + dotted, shortName, (uint)shortName.Length);
            var spelled = new string(shortName, 0, (int)length).Replace(@"\\?\", string.Empty, StringComparison.Ordinal);

            Assert.SkipUnless(
                length > 0 && !spelled.EndsWith('.') && spelled.Contains('~', StringComparison.Ordinal), "this volume keeps no 8.3 names");

            // mklink stores the target as typed; .NET's own call made it full first, expanding the short name and
            // dropping the dot, which is not the layout under test.
            using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "cmd.exe", ["/c", "mklink", "/D", orders, Path.Combine(spelled, "r")])
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            })!)
            {
                mklink.WaitForExit();
                Assert.Equal(0, mklink.ExitCode);
            }

            var timer = System.Diagnostics.Stopwatch.StartNew();

            Assert.Equal(LocalOpenStatus.OffThisMachine, LocalOpen.Folder(orders));
            Assert.NotNull(LocalStorage.Problem(orders));
            Assert.True(timer.Elapsed < Looking, $"took {timer.Elapsed}: the share was looked up");
        }
        finally
        {
            foreach (var link in (string[])[orders, @"\\?\" + Path.Combine(dotted, "r"), @"\\?\" + Path.Combine(dotted, "sub")])
            {
                try
                {
                    Directory.Delete(link);
                }
                catch (IOException)
                {
                    // Not made.
                }
            }

            Directory.Delete(@"\\?\" + dotted);
        }
    }

    [Fact]
    public async Task A_relative_link_reached_through_a_junction_is_never_judged_from_the_junctions_target()
    {
        // Windows resolves `..` in a relative link reached through a junction from the path as reached; the walk had the
        // junction's target, so `outer\o2\J2\R` with `R` -> `..\..\T` read as the decoy `T` beside `d1` while Windows went
        // to `outer\T`, a link to a share (review, lens 2 round 17).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are Windows'");

        SkipUnlessLinksCanBeMade();

        var o2 = Directory.CreateDirectory(Path.Combine(_root, "outer", "o2")).FullName;
        var d2 = Directory.CreateDirectory(Path.Combine(_root, "d1", "d2")).FullName;
        var junction = Path.Combine(o2, "J2");
        var orders = Path.Combine(_root, "orders");

        Directory.CreateDirectory(Path.Combine(_root, "T"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "outer", "T"), _share);
        Directory.CreateSymbolicLink(Path.Combine(d2, "R"), @"..\..\T");

        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", ["/c", "mklink", "/J", junction, d2])
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        })!)
        {
            await mklink.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, mklink.ExitCode);
        }

        Directory.CreateSymbolicLink(orders, Path.Combine(junction, "R"));

        try
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();

            Assert.Equal(LocalOpenStatus.OffThisMachine, LocalOpen.Folder(orders));
            Assert.NotNull(LocalStorage.Problem(orders));
            Assert.True(timer.Elapsed < Looking, $"took {timer.Elapsed}: the share was looked up");
        }
        finally
        {
            foreach (var link in (string[])[orders, junction, Path.Combine(d2, "R"), Path.Combine(_root, "outer", "T")])
            {
                Directory.Delete(link);
            }
        }
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, [System.Runtime.InteropServices.Out] char[] shortPath, uint length);

    private void SkipUnlessLinksCanBeMade()
    {
        var probe = Path.Combine(_root, "probe");

        try
        {
            Directory.CreateSymbolicLink(probe, _root);
            Directory.Delete(probe);
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("symbolic links need Developer Mode here");
        }
    }

    // Flips on one thread and opens on this one until the time is up; every open must come back before an SMB wait could.
    private static async Task RaceAsync(Action flip, Action open)
    {
        using var stop = new CancellationTokenSource(Flipping);
        var flips = 0;

        var flipper = Task.Run(
            () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        flip();
                        flips++;
                    }
                    catch (Exception busy) when (busy is IOException or UnauthorizedAccessException)
                    {
                        // An open held it for a moment; flip again.
                    }
                }
            },
            TestContext.Current.CancellationToken);

        var opens = 0;
        var slowest = TimeSpan.Zero;

        while (!stop.IsCancellationRequested)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();

            open();
            opens++;

            slowest = timer.Elapsed > slowest ? timer.Elapsed : slowest;
            Assert.True(timer.Elapsed < Looking, $"open {opens} took {timer.Elapsed}: it followed a link to the share");
        }

        await flipper;

        Assert.True(flips > 100 && opens > 100, $"the race hardly ran: {flips} flips, {opens} opens");
    }
}
