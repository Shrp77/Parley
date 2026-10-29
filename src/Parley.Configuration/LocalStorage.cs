using System.Text.Json;
using Parley.Core;

namespace Parley.Configuration;

/// <summary>
/// Whether a folder is on this machine's own disks: never a network drive, a synced cloud folder or a virtual drive.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's rule, 2026-10-29: only local storage.</b> A repository, a deployment's root (its stores and its file
/// location) and an agent's workspace are refused on OneDrive, Google Drive, Dropbox, iCloud Drive, Box, a network share
/// or any other drive that is not this machine's own. A store on a synced folder can be copied mid-write by the sync
/// client, a placeholder can vanish from under a fetch, and a path there reaches somebody else's servers.
/// </para>
/// <para>
/// Decided from what can be read without opening the folder: its links (<see cref="OffMachineLinks"/>), the kind of
/// drive it is on, and where the sync clients installed on this machine keep their folders. A sync client not named
/// here is not caught; the list is the ones in common use.
/// </para>
/// </remarks>
public static class LocalStorage
{
    /// <summary>Why a folder is not local storage, or <see langword="null"/> when it is.</summary>
    /// <param name="path">The folder, as written.</param>
    /// <param name="read">Reads an environment variable; the process's own by default. A seam for tests.</param>
    /// <returns>One sentence naming what it is on, or <see langword="null"/>.</returns>
    public static string? Problem(string? path, Func<string, string?>? read = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        read ??= Environment.GetEnvironmentVariable;

        if (LocalPath.IsOffThisMachine(path.Trim()))
        {
            return Said(path, "another machine, a device, or a link to one");
        }

        string full;

        try
        {
            full = Path.GetFullPath(path.Trim());
        }
        catch (Exception unusable) when (unusable is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        // `folder::$INDEX_ALLOCATION` is the folder itself to Windows, but not to a comparison of text.
        if (OperatingSystem.IsWindows() && full.IndexOf(':', 2) >= 0)
        {
            return $"{full} names a stream, not a folder. Name a plain folder on this machine.";
        }

        // What a folder is on is decided where it really is: a subst drive letter and a link each lead somewhere the
        // text does not say (review, lens 1 run 69). The drive is asked before the walk reads anything on it, so a
        // network drive is refused without being touched.
        var reached = full;

        for (var round = 0; ; round++)
        {
            var real = Unsubstituted(reached);

            if (DriveProblem(real) is { } drive)
            {
                return Said(Through(full, real), drive);
            }

            if (OffMachineLinks.LeadsOffThisMachine(real, out var walked) || round > 8)
            {
                return Said(path, "another machine, a device, or a link to one");
            }

            if (string.Equals(walked, reached, StringComparison.Ordinal))
            {
                break;
            }

            reached = walked;
        }

        foreach (var where in (string[])[full, reached])
        {
            if (SyncedFolder(where, read) is { } synced)
            {
                return Said(Through(full, where), synced);
            }
        }

        return null;
    }

    private static string Through(string full, string reached) =>
        string.Equals(full, reached, StringComparison.Ordinal) ? full : $"{full}, through a link to {reached},";

    private static string Said(string path, string what) =>
        $"{path} is on {what}. Parley keeps its work only on this machine's own disks: move it to a local folder.";

    // A drive letter `subst` made stands for a folder on another drive; QueryDosDevice names it as `\??\C:\folder`.
    // Reading that name opens nothing.
    private static string Unsubstituted(string full)
    {
        if (!OperatingSystem.IsWindows()
            || full.Length < 2
            || full[1] != ':'
            || !char.IsAsciiLetter(full[0]))
        {
            return full;
        }

        var target = new char[1024];

        try
        {
            if (QueryDosDevice(full[..2], target, (uint)target.Length) == 0)
            {
                return full;
            }
        }
        catch (EntryPointNotFoundException)
        {
            return full;
        }

        var name = new string(target).Split('\0')[0];

        return name.StartsWith(@"\??\", StringComparison.Ordinal) && name.Length > 6 && name[5] == ':'
            ? Path.Join(name[4..], full[2..])
            : full;
    }

    // `DllImport` for the reason Windowless gives, resolved from System32 only so a planted kernel32.dll is never loaded.
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport(
        "kernel32.dll",
        EntryPoint = "QueryDosDeviceW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true)]
    private static extern uint QueryDosDevice(string deviceName, [System.Runtime.InteropServices.Out] char[] targetPath, uint max);

    // The folders the sync clients keep, as each says where it put them.
    private static string? SyncedFolder(string full, Func<string, string?> read)
    {
        var home = read("USERPROFILE") is { Length: > 0 } profile ? profile : read("HOME") ?? string.Empty;
        var roots = new List<(string Root, string Name)>();

        foreach (var variable in (string[])["OneDrive", "OneDriveConsumer", "OneDriveCommercial"])
        {
            if (read(variable) is { Length: > 0 } root)
            {
                roots.Add((root, "OneDrive"));
            }
        }

        if (home.Length > 0)
        {
            roots.Add((Path.Combine(home, "OneDrive"), "OneDrive"));
            roots.Add((Path.Combine(home, "Dropbox"), "Dropbox"));
            roots.Add((Path.Combine(home, "iCloudDrive"), "iCloud Drive"));
            roots.Add((Path.Combine(home, "Box"), "Box"));
            roots.Add((Path.Combine(home, "Box Sync"), "Box"));
            roots.Add((Path.Combine(home, "Google Drive"), "Google Drive"));
            roots.Add((Path.Combine(home, "My Drive"), "Google Drive"));
            roots.Add((Path.Combine(home, "Library", "CloudStorage"), "a cloud storage provider"));
            roots.Add((Path.Combine(home, "Library", "Mobile Documents"), "iCloud Drive"));
            roots.Add((Path.Combine(home, ".gvfs"), "a virtual file system"));
        }

        foreach (var info in (string[])[read("LOCALAPPDATA") ?? string.Empty, read("APPDATA") ?? string.Empty])
        {
            if (info.Length > 0 && DropboxRoots(Path.Combine(info, "Dropbox", "info.json")) is { } dropbox)
            {
                roots.AddRange(dropbox.Select(root => (root, "Dropbox")));
            }
        }

        // GNOME's gvfs and the document portal mount under the user's runtime folder, /run/user/<uid>/gvfs and /doc.
        if (OperatingSystem.IsLinux()
            && full.Split('/') is [_, "run", "user", _, "gvfs" or "doc", ..])
        {
            return "a virtual file system";
        }

        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        foreach (var (root, name) in roots)
        {
            var trimmed = Path.TrimEndingDirectorySeparator(root);

            if (string.Equals(full, trimmed, comparison)
                || full.StartsWith(trimmed + Path.DirectorySeparatorChar, comparison))
            {
                return name;
            }
        }

        return null;
    }

    // Dropbox writes where each of its accounts keeps its folder into info.json.
    private static IEnumerable<string>? DropboxRoots(string info)
    {
        try
        {
            if (!File.Exists(info))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(info));

            // The file is Dropbox's, and a top level that is not an object would throw from EnumerateObject; a bad file
            // there names no folder rather than stopping every check (review, lens 1 run 69).
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return [.. document.RootElement.EnumerateObject()
                .Select(account => account.Value.ValueKind == JsonValueKind.Object
                    && account.Value.TryGetProperty("path", out var folder)
                    && folder.ValueKind == JsonValueKind.String
                        ? folder.GetString()
                        : null)
                .OfType<string>()];
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // The drive the folder is on, by the kind of drive the system says it is. Network drives, and drives a sync client
    // mounts as a letter of its own (Google Drive's G:), are refused; a removable disk is this machine's own.
    private static string? DriveProblem(string full)
    {
        try
        {
            var drive = OperatingSystem.IsWindows()
                ? new DriveInfo(Path.GetPathRoot(full) ?? full)
                : DriveInfo.GetDrives()
                    .Where(d => full.StartsWith(Path.TrimEndingDirectorySeparator(d.Name) + "/", StringComparison.Ordinal)
                        || string.Equals(full, d.Name, StringComparison.Ordinal))
                    .MaxBy(d => d.Name.Length);

            if (drive is null)
            {
                return null;
            }

            if (drive.DriveType == DriveType.Network)
            {
                return "a network drive";
            }

            if (drive.DriveType is DriveType.CDRom or DriveType.NoRootDirectory)
            {
                return "a drive that is not a disk";
            }

            if (OperatingSystem.IsWindows()
                && drive.IsReady
                && drive.VolumeLabel.Contains("Google Drive", StringComparison.OrdinalIgnoreCase))
            {
                return "Google Drive";
            }

            // A FUSE file system is a program's, and the ones in use for this are remote (rclone, sshfs, gvfs);
            // fuseblk is a local disk read through FUSE, such as NTFS.
            if (!OperatingSystem.IsWindows()
                && drive.DriveFormat.StartsWith("fuse.", StringComparison.Ordinal))
            {
                return "a virtual file system";
            }

            return null;
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
