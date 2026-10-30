using Parley.Configuration;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That Parley keeps its work only on this machine's own disks: never a synced cloud folder, a share or a virtual file system.
/// </summary>
/// <remarks>
/// <b>The owner's rule, 2026-10-29.</b> A repository, a deployment's root and an agent's workspace are refused on
/// OneDrive, Google Drive, Dropbox, iCloud Drive, Box, a network share or any other drive that is not this machine's
/// own. Each sync client is found where it says it keeps its folder, so the environment is a seam here: no test reads
/// the developer's own.
/// </remarks>
public sealed class OnlyLocalStorageTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "parley-home-" + Guid.NewGuid().ToString("N"));

    public OnlyLocalStorageTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        // A junction inside the home folder leaves the last removal failing once with the folder already emptied, so a
        // second try is made; a temporary folder that piles up breaks other tests' timing.
        for (var attempt = 0; attempt < 2 && Directory.Exists(_home); attempt++)
        {
            try
            {
                Directory.Delete(_home, recursive: true);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private Func<string, string?> Environment(params (string Name, string Value)[] set) =>
        name => name is "USERPROFILE" or "HOME"
            ? _home
            : set.FirstOrDefault(pair => string.Equals(pair.Name, name, StringComparison.Ordinal)).Value;

    [Theory]
    [InlineData("Dropbox", "Dropbox")]
    [InlineData("iCloudDrive", "iCloud Drive")]
    [InlineData("Box", "Box")]
    [InlineData("Google Drive", "Google Drive")]
    [InlineData("My Drive", "Google Drive")]
    [InlineData("OneDrive", "OneDrive")]
    public void A_folder_a_sync_client_keeps_in_the_home_is_refused(string folder, string named)
    {
        var repository = Path.Combine(_home, folder, "src", "orders");

        Assert.Contains($"on {named}", LocalStorage.Problem(repository, Environment()), StringComparison.Ordinal);
    }

    [Fact]
    public void The_folder_onedrive_names_for_itself_is_refused_wherever_it_is()
    {
        var onedrive = Path.Combine(_home, "Somewhere", "OneDrive - Contoso");
        var repository = Path.Combine(onedrive, "orders");

        Assert.Contains("on OneDrive", LocalStorage.Problem(repository, Environment(("OneDriveCommercial", onedrive))), StringComparison.Ordinal);
    }

    [Fact]
    public void The_folder_dropbox_records_in_its_info_file_is_refused()
    {
        var local = Path.Combine(_home, "AppData", "Local");
        var dropbox = Path.Combine(_home, "Elsewhere", "Team Dropbox");
        Directory.CreateDirectory(Path.Combine(local, "Dropbox"));
        File.WriteAllText(
            Path.Combine(local, "Dropbox", "info.json"),
            System.Text.Json.JsonSerializer.Serialize(new { business = new { path = dropbox } }));

        Assert.Contains("on Dropbox", LocalStorage.Problem(Path.Combine(dropbox, "orders"), Environment(("LOCALAPPDATA", local))), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Library/CloudStorage/GoogleDrive-someone/My Drive")]
    [InlineData("Library/Mobile Documents/com~apple~CloudDocs")]
    public void The_folders_macos_keeps_for_cloud_providers_are_refused(string folder)
    {
        var repository = Path.Combine([_home, .. folder.Split('/'), "orders"]);

        Assert.NotNull(LocalStorage.Problem(repository, Environment()));
    }

    [Fact]
    public void A_share_named_as_a_path_is_refused_before_anything_is_asked()
    {
        Assert.Contains("another machine", LocalStorage.Problem(@"\\192.0.2.9\share\orders", Environment()), StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_on_this_machines_own_disk_is_local()
    {
        var repository = Path.Combine(_home, "src", "orders");
        Directory.CreateDirectory(repository);

        Assert.Null(LocalStorage.Problem(repository, Environment()));
        Assert.Null(LocalStorage.Problem(Path.Combine(_home, "Boxes", "orders"), Environment()));
    }

    [Fact]
    public async Task A_link_into_a_synced_folder_is_judged_where_it_leads()
    {
        // C:\code as a junction to OneDrive\code reads as local and is synced all the same (review, lens 1 run 69).
        var synced = Directory.CreateDirectory(Path.Combine(_home, "OneDrive", "code")).FullName;
        Directory.CreateDirectory(Path.Combine(synced, "orders"));
        var link = Path.Combine(_home, "code");
        await LinkAsync(link, synced);

        var problem = LocalStorage.Problem(Path.Combine(link, "orders"), Environment());

        Assert.NotNull(problem);
        Assert.Contains("which leads to", problem, StringComparison.Ordinal);
        Assert.Contains("on OneDrive", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sync_folder_that_is_itself_a_link_is_known_where_it_leads()
    {
        // OneDrive moved to another disk and linked back from the home folder, or macOS's /var, which is /private/var: a
        // folder named where the link leads is still in it (CI on macOS, 0.47.34).
        var moved = Directory.CreateDirectory(Path.Combine(_home, "Elsewhere", "OneDrive")).FullName;
        var repository = Directory.CreateDirectory(Path.Combine(moved, "orders")).FullName;
        await LinkAsync(Path.Combine(_home, "OneDrive"), moved);

        Assert.Contains("on OneDrive", LocalStorage.Problem(repository, Environment()), StringComparison.Ordinal);
    }

    [Fact]
    public void A_stream_spelling_of_a_folder_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "NTFS streams");
        var onedrive = Path.Combine(_home, "OneDrive");
        Directory.CreateDirectory(onedrive);

        Assert.Contains("stream", LocalStorage.Problem(onedrive + @"::$INDEX_ALLOCATION\orders", Environment()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    public void A_dropbox_info_file_that_is_not_an_object_names_no_folder_and_stops_nothing(string written)
    {
        var local = Path.Combine(_home, "AppData", "Local");
        Directory.CreateDirectory(Path.Combine(local, "Dropbox"));
        File.WriteAllText(Path.Combine(local, "Dropbox", "info.json"), written);
        var repository = Path.Combine(_home, "src", "orders");

        Assert.Null(LocalStorage.Problem(repository, Environment(("LOCALAPPDATA", local))));
    }

    [Fact]
    public async Task A_link_whose_target_is_a_stream_spelling_of_a_synced_folder_is_refused()
    {
        // The target `OneDrive::$INDEX_ALLOCATION` opens as OneDrive itself but compares as other text (lens 1 run 70).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "NTFS streams");
        var onedrive = Directory.CreateDirectory(Path.Combine(_home, "OneDrive")).FullName;
        Directory.CreateDirectory(Path.Combine(onedrive, "inside"));
        var link = Path.Combine(_home, "code");
        await LinkAsync(link, onedrive + "::$INDEX_ALLOCATION");

        Assert.NotNull(LocalStorage.Problem(Path.Combine(link, "inside"), Environment()));
    }

    [Fact]
    public void A_link_to_a_letter_for_a_share_is_refused_without_the_share_being_asked()
    {
        // A link to `K:\x`, where K: is a share's letter, was read on K: and signed the person in to its host (lens 2
        // run 74). The letter is defined for a TEST-NET host, which never answers: a lookup that slipped through takes
        // the SMB wait, about 21 seconds.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "drive letters");

        // A fresh host each run: Windows answers a host it failed to reach a moment ago at once, which would hide a lookup.
        WithLetter($@"\??\UNC\192.0.2.{Random.Shared.Next(2, 254)}\share", letter =>
        {
            // A symbolic link, because mklink /J looks its target up as it makes the junction. Making one needs Developer
            // Mode or an elevated runner.
            var link = Path.Combine(_home, "repo");

            if (!TrySymbolicLink(link, $@"{letter}\x"))
            {
                Assert.Skip("symbolic links need Developer Mode here");
            }

            var timer = System.Diagnostics.Stopwatch.StartNew();

            Assert.True(OffMachineLinks.LeadsOffThisMachine($@"{letter}\x\.mcp.json"));
            Assert.True(OffMachineLinks.LeadsOffThisMachine(Path.Combine(link, ".mcp.json")));
            Assert.NotNull(LocalStorage.Problem(link, Environment()));
            Assert.Contains("a network drive", LocalStorage.Problem($@"{letter}\x", Environment()), StringComparison.Ordinal);
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"the share was asked: {timer.Elapsed}");
        });
    }

    [Fact]
    public void A_subst_letter_for_a_synced_folder_is_refused_as_that_folder()
    {
        // SPEC §4.1's "or a subst letter": nothing held it (lens 3 run 78).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "drive letters");
        var onedrive = Directory.CreateDirectory(Path.Combine(_home, "OneDrive")).FullName;

        WithLetter($@"\??\{onedrive}", letter =>
            Assert.Contains("on OneDrive", LocalStorage.Problem($@"{letter}\orders", Environment()), StringComparison.Ordinal));
    }

    [Fact]
    public void A_subst_letter_is_walked_as_the_folder_it_stands_for()
    {
        // K: standing for a folder in the repository that links to a share: reading K:\x went through that link to the
        // share (lens 2 run 75). A TEST-NET host, fresh each run, never answers, so a lookup takes the SMB wait.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "drive letters");
        var hop = Path.Combine(_home, "hop");

        if (!TrySymbolicLink(hop, $@"\\192.0.2.{Random.Shared.Next(2, 254)}\share"))
        {
            Assert.Skip("symbolic links need Developer Mode here");
        }

        WithLetter($@"\??\{hop}", letter =>
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();

            Assert.True(OffMachineLinks.LeadsOffThisMachine($@"{letter}\x\.mcp.json"));
            Assert.NotNull(LocalStorage.Problem($@"{letter}\x", Environment()));
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"the share was asked: {timer.Elapsed}");
        });
    }

    [Theory]
    [InlineData(@"\??\GLOBALROOT\Device\Mup\192.0.2.{0}\share")]
    [InlineData(@"\Device\Mup\192.0.2.{0}\share")]
    [InlineData(@"\Device\Null")]
    public void A_letter_for_anything_but_a_local_disk_is_refused_without_being_asked(string definition)
    {
        // Asking a letter defined for GLOBALROOT\Device\Mup what kind of drive it is opened the share (lens 2 run 75), so
        // a letter is judged by its name alone.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "drive letters");

        WithLetter(string.Format(System.Globalization.CultureInfo.InvariantCulture, definition, Random.Shared.Next(2, 254)), letter =>
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();

            Assert.True(OffMachineLinks.LeadsOffThisMachine($@"{letter}\x"));
            Assert.NotNull(LocalStorage.Problem($@"{letter}\x", Environment()));
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"the letter was asked: {timer.Elapsed}");
        });
    }

    [Fact]
    public async Task A_folder_that_mounts_a_local_disk_is_walked_through()
    {
        // A second disk mounted at C:\mnt\data reads its target as `Volume{GUID}\`, which the walk refused as a target it
        // could not find (lens 1 run 71). The temporary folder's own volume stands in for the second disk.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "volume mount points");
        var mountPoint = new char[1024];
        var volume = new char[64];
        Assert.True(GetVolumePathName(_home, mountPoint, (uint)mountPoint.Length));
        Assert.True(GetVolumeNameForVolumeMountPoint(new string(mountPoint).TrimEnd('\0'), volume, (uint)volume.Length));
        var mounted = Path.Combine(_home, "mounted");
        await LinkAsync(mounted, new string(volume).TrimEnd('\0'));

        try
        {
            var repository = Path.Combine(mounted, _home[(Path.GetPathRoot(_home)!.Length)..], "src", "orders");
            Directory.CreateDirectory(Path.Combine(_home, "src", "orders"));

            Assert.True(Directory.Exists(repository));
            Assert.False(OffMachineLinks.LeadsOffThisMachine(repository));
            Assert.Null(LocalStorage.Problem(repository, Environment()));
        }
        finally
        {
            // The link alone, before the home folder is deleted: it leads to the whole disk.
            Directory.Delete(mounted);
        }
    }

    [Fact]
    public void A_letter_whose_definition_is_too_long_to_read_at_first_is_still_judged()
    {
        // A definition longer than the first buffer failed as "not defined", and the letter was read (lens 2 run 76).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "drive letters");
        var share = $@"\??\UNC\192.0.2.{Random.Shared.Next(2, 254)}\share" + string.Concat(Enumerable.Repeat(@"\aaaaaaaaa", 110));

        WithLetter(share, letter =>
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();

            Assert.True(OffMachineLinks.LeadsOffThisMachine($@"{letter}\x"));
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"the share was asked: {timer.Elapsed}");
        });
    }

    [Fact]
    public async Task A_symbolic_link_spelled_as_a_volume_is_followed_as_the_relative_link_it_is()
    {
        // `x` -> `Volume{GUID}` is relative to its folder, where a sibling of that name can lead anywhere; only a mount
        // point names a volume (lens 1 run 72).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "volume names");
        var mountPoint = new char[1024];
        var volume = new char[64];
        Assert.True(GetVolumePathName(_home, mountPoint, (uint)mountPoint.Length));
        Assert.True(GetVolumeNameForVolumeMountPoint(new string(mountPoint).TrimEnd('\0'), volume, (uint)volume.Length));
        var name = new string(volume).TrimEnd('\0')[4..].TrimEnd('\\');
        var onedrive = Directory.CreateDirectory(Path.Combine(_home, "OneDrive", "orders")).Parent!.FullName;
        await LinkAsync(Path.Combine(_home, name), onedrive);

        if (!TrySymbolicLink(Path.Combine(_home, "x"), name))
        {
            Assert.Skip("symbolic links need Developer Mode here");
        }

        Assert.Contains("on OneDrive", LocalStorage.Problem(Path.Combine(_home, "x", "orders"), Environment()), StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_to_an_nt_path_is_refused_whatever_stands_beside_it()
    {
        // LinkTarget hands `\\?\GLOBALROOT\Device\Mup\host\s` back as `GLOBALROOT\Device\Mup\host\s`, which reads as
        // relative; folders of that name beside the link made it look local while Windows went to the share (lens 2
        // run 77). A fresh TEST-NET host each run: a lookup that slipped through takes the SMB wait.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "NT paths");
        var host = $"192.0.2.{Random.Shared.Next(2, 254)}";
        Directory.CreateDirectory(Path.Combine(_home, "GLOBALROOT", "Device", "Mup", host, "s", ".git"));

        if (!TrySymbolicLink(Path.Combine(_home, "g2"), $@"\\?\GLOBALROOT\Device\Mup\{host}\s"))
        {
            Assert.Skip("symbolic links need Developer Mode here");
        }

        var timer = System.Diagnostics.Stopwatch.StartNew();

        Assert.True(OffMachineLinks.LeadsOffThisMachine(Path.Combine(_home, "g2", ".git")));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"the share was asked: {timer.Elapsed}");
    }

    [Fact]
    public void A_link_to_a_folder_by_its_volume_path_is_refused_whatever_stands_beside_it()
    {
        // `\\?\Volume{GUID}\Users\…\OneDrive\orders` with a decoy of that relative name beside the link: the walk checked
        // the decoy and admitted a folder on OneDrive (lens 2 run 77).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "volume names");
        var mountPoint = new char[1024];
        var volume = new char[64];
        Assert.True(GetVolumePathName(_home, mountPoint, (uint)mountPoint.Length));
        Assert.True(GetVolumeNameForVolumeMountPoint(new string(mountPoint).TrimEnd('\0'), volume, (uint)volume.Length));
        var onedrive = Directory.CreateDirectory(Path.Combine(_home, "OneDrive", "orders")).Parent!.FullName;
        var underVolume = onedrive[Path.GetPathRoot(onedrive)!.Length..];
        var volumeName = new string(volume).TrimEnd('\0');
        Directory.CreateDirectory(Path.Combine(_home, volumeName[4..].TrimEnd('\\'), underVolume, "orders"));

        if (!TrySymbolicLink(Path.Combine(_home, "code"), volumeName + underVolume))
        {
            Assert.Skip("symbolic links need Developer Mode here");
        }

        Assert.NotNull(LocalStorage.Problem(Path.Combine(_home, "code", "orders"), Environment()));
    }

    [Fact]
    public async Task A_mount_point_at_a_long_path_is_walked_through()
    {
        // The link is read with a `\\?\` path, so a mount point past 260 characters is read too (lens 1 run 73).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "volume mount points");
        var mountPoint = new char[1024];
        var volume = new char[64];
        Assert.True(GetVolumePathName(_home, mountPoint, (uint)mountPoint.Length));
        Assert.True(GetVolumeNameForVolumeMountPoint(new string(mountPoint).TrimEnd('\0'), volume, (uint)volume.Length));
        var deep = Directory.CreateDirectory(Path.Combine([_home, .. Enumerable.Repeat("deeper-folder-name", 15)])).FullName;
        Directory.CreateDirectory(Path.Combine(_home, "src", "orders"));
        var mounted = Path.Combine(deep, "mounted");
        await LinkAsync(mounted, new string(volume).TrimEnd('\0'));

        try
        {
            Assert.True(mounted.Length > 260);
            Assert.Null(LocalStorage.Problem(
                Path.Combine(mounted, _home[(Path.GetPathRoot(_home)!.Length)..], "src", "orders"), Environment()));
        }
        finally
        {
            // The link alone, before the home folder is deleted: it leads to the whole disk.
            Directory.Delete(mounted);
        }
    }

    [Fact]
    public void A_link_to_a_name_with_a_trailing_dot_is_refused_whatever_stands_beside_it()
    {
        // GetFullPath turns `code.` into `code`; Windows following the link does not. A plain `code\orders` beside a
        // link `code.` into OneDrive made a link to `code.` read as local (lens 2 run 78, there with junctions, which
        // mklink will not name with a trailing dot; symbolic links here).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Win32 path rules");
        var work = Directory.CreateDirectory(Path.Combine(_home, "OneDrive", "work", "orders")).Parent!.FullName;
        Directory.CreateDirectory(Path.Combine(_home, "repo", "code", "orders"));

        if (!TrySymbolicLink(@"\\?\" + Path.Combine(_home, "repo", "code."), work)
            || !TrySymbolicLink(Path.Combine(_home, "repo", "j"), "code."))
        {
            Assert.Skip("symbolic links need Developer Mode here");
        }

        // Windows itself goes into OneDrive through `j`, which is what makes the plain folder a decoy.
        File.WriteAllText(Path.Combine(_home, "repo", "j", "orders", "probe.txt"), "x");
        Assert.True(File.Exists(Path.Combine(work, "orders", "probe.txt")));
        Assert.NotNull(LocalStorage.Problem(Path.Combine(_home, "repo", "j", "orders"), Environment()));
    }

    [Fact]
    public async Task A_folder_reached_through_a_mount_of_a_lettered_disk_is_judged_where_it_really_is()
    {
        // A junction to this disk's own volume, then down into OneDrive: the walk ended on the path as written and
        // admitted it (lens 1 run 75). A disk with a letter is walked on from that letter.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "volume mount points");
        var mountPoint = new char[1024];
        var volume = new char[64];
        Assert.True(GetVolumePathName(_home, mountPoint, (uint)mountPoint.Length));
        Assert.True(GetVolumeNameForVolumeMountPoint(new string(mountPoint).TrimEnd('\0'), volume, (uint)volume.Length));
        var onedrive = Directory.CreateDirectory(Path.Combine(_home, "OneDrive", "orders")).Parent!.FullName;
        var mounted = Path.Combine(_home, "repo", "vol");
        Directory.CreateDirectory(Path.GetDirectoryName(mounted)!);
        await LinkAsync(mounted, new string(volume).TrimEnd('\0'));

        try
        {
            var through = Path.Combine(mounted, onedrive[Path.GetPathRoot(onedrive)!.Length..], "orders");

            Assert.True(Directory.Exists(through));
            Assert.Contains("on OneDrive", LocalStorage.Problem(through, Environment()), StringComparison.Ordinal);
        }
        finally
        {
            // The link alone, before the home folder is deleted: it leads to the whole disk.
            Directory.Delete(mounted);
        }
    }

    [Fact]
    public void A_junction_whose_stored_target_holds_a_nul_is_refused_not_thrown()
    {
        // FSCTL_SET_REPARSE_POINT stores what it is given; a NUL in the target made the walk throw out of every scan
        // that met it (lens 1 run 75).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "reparse points");
        var real = Directory.CreateDirectory(Path.Combine(_home, "real")).FullName;
        var junction = Directory.CreateDirectory(Path.Combine(_home, "repo", ".git")).FullName;
        SetMountPoint(junction, $@"\??\{real}" + "\0zz");

        Assert.True(OffMachineLinks.LeadsOffThisMachine(Path.Combine(junction, "config")));
        Assert.NotNull(LocalStorage.Problem(Path.Combine(_home, "repo", ".git"), Environment()));
    }

    // Writes a mount-point reparse buffer by hand, the way a program can without privilege.
    private static void SetMountPoint(string folder, string substitute)
    {
        var name = System.Text.Encoding.Unicode.GetBytes(substitute);
        var buffer = new byte[8 + 8 + name.Length + 4];
        BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)(8 + name.Length + 4)).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);
        BitConverter.GetBytes((ushort)name.Length).CopyTo(buffer, 10);
        BitConverter.GetBytes((ushort)(name.Length + 2)).CopyTo(buffer, 12);
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 14);
        name.CopyTo(buffer, 16);

        using var handle = CreateFile(@"\\?\" + folder, 0x40000000, 0x7, IntPtr.Zero, 3, 0x00200000 | 0x02000000, IntPtr.Zero);
        Assert.False(handle.IsInvalid);
        Assert.True(DeviceIoControl(handle, 0x000900A4, buffer, (uint)buffer.Length, null, 0, out _, IntPtr.Zero));
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code, byte[] input, uint inputSize, byte[]? output, uint outputSize, out uint returned, IntPtr overlapped);

    [Theory]
    [InlineData("code ")]
    [InlineData("code. ")]
    [InlineData("code  ")]
    public void A_path_through_a_part_ending_in_a_space_is_refused(string name)
    {
        // `repo\code \orders`: asking `code ` alone asks plain `code`, which is missing, and the next step went through
        // the real `code ` link into OneDrive (lens 2 run 79).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Win32 path rules");
        var work = Directory.CreateDirectory(Path.Combine(_home, "OneDrive", "work", "orders")).Parent!.FullName;
        Directory.CreateDirectory(Path.Combine(_home, "repo"));

        if (!TrySymbolicLink(@"\\?\" + Path.Combine(_home, "repo", name), work))
        {
            Assert.Skip("symbolic links need Developer Mode here");
        }

        var through = Path.Combine(_home, "repo", name, "orders");

        Assert.True(Directory.Exists(through));
        Assert.True(OffMachineLinks.LeadsOffThisMachine(through));
        Assert.NotNull(LocalStorage.Problem(through, Environment()));
    }

    [Fact]
    public void A_junction_to_a_name_with_a_trailing_dot_is_refused_whatever_stands_beside_it()
    {
        // A junction's target is absolute, `\??\…\code.`; tidied it named the plain `code` beside it while Windows
        // followed `code.` into OneDrive (lens 2 run 78 with a junction; lens 3 run 82 found this branch untested).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Win32 path rules");
        var work = Directory.CreateDirectory(Path.Combine(_home, "OneDrive", "work", "orders")).Parent!.FullName;
        Directory.CreateDirectory(Path.Combine(_home, "repo", "code"));

        if (!TrySymbolicLink(@"\\?\" + Path.Combine(_home, "repo", "code."), work))
        {
            Assert.Skip("symbolic links need Developer Mode here");
        }

        var junction = Directory.CreateDirectory(Path.Combine(_home, "repo", "j")).FullName;
        SetMountPoint(junction, $@"\??\{Path.Combine(_home, "repo", "code.")}");

        Assert.True(Directory.Exists(Path.Combine(junction, "orders")));
        Assert.NotNull(LocalStorage.Problem(junction, Environment()));
    }

    [Fact]
    public void A_junction_whose_target_climbs_with_dots_is_refused()
    {
        // `\??\…\code\..\code` is not tidied by the system that follows it, so the walk does not tidy it either.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Win32 path rules");
        var code = Directory.CreateDirectory(Path.Combine(_home, "repo", "code")).FullName;
        var junction = Directory.CreateDirectory(Path.Combine(_home, "repo", "j")).FullName;
        SetMountPoint(junction, $@"\??\{code}\..\code");

        Assert.True(OffMachineLinks.LeadsOffThisMachine(junction));
    }

    [Fact]
    public void A_subst_letter_for_a_name_with_a_trailing_dot_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "drive letters");
        Directory.CreateDirectory(Path.Combine(_home, "plain"));

        WithLetter($@"\??\{Path.Combine(_home, "plain")}.", letter =>
            Assert.True(OffMachineLinks.LeadsOffThisMachine($@"{letter}\orders")));
    }

    [Fact]
    public void A_subst_letter_for_a_folder_that_is_not_there_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "drive letters");

        WithLetter($@"\??\{Path.Combine(_home, "gone")}", letter =>
            Assert.True(OffMachineLinks.LeadsOffThisMachine($@"{letter}\orders")));
    }

    private static void WithLetter(string definition, Action<string> act)
    {
        // Free means Windows says the letter is not defined; a definition too long for a small buffer is not "free".
        var letter = Enumerable.Range('K', 'Z' - 'K' + 1).Select(c => $"{(char)c}:")
            .First(l => QueryDosDevice(l, new char[65536], 65536) == 0
                && System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == 2);
        Assert.True(DefineDosDevice(RawTargetPath, letter, definition));

        try
        {
            act(letter);
        }
        finally
        {
            DefineDosDevice(RawTargetPath | RemoveDefinition | ExactMatchOnRemove, letter, definition);
        }
    }

    private static bool TrySymbolicLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string fileName, [System.Runtime.InteropServices.Out] char[] volumePathName, uint length);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, [System.Runtime.InteropServices.Out] char[] volumeName, uint length);

    private const uint RawTargetPath = 0x1;
    private const uint RemoveDefinition = 0x2;
    private const uint ExactMatchOnRemove = 0x4;

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "DefineDosDeviceW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DefineDosDevice(uint flags, string deviceName, string targetPath);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string deviceName, [System.Runtime.InteropServices.Out] char[] targetPath, uint max);

    private static async Task LinkAsync(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        // A junction needs no privilege, where a symbolic link on Windows needs Developer Mode.
        using var mklink = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false, RedirectStandardOutput = true,
            })!;
        await mklink.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, mklink.ExitCode);
    }

    [Fact]
    public void Enrolment_refuses_a_repository_that_is_not_on_local_storage()
    {
        var problems = EnrollmentPlan.Validate(
            [new EnrollmentEntry(@"\\192.0.2.9\share\orders", "orders", "Carts and order totals.", false)],
            _ => true);

        Assert.Contains(problems, problem => problem.Contains("only on this machine's own disks", StringComparison.Ordinal));
    }
}
