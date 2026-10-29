using System.Text.Json;

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

        if (OffMachineLinks.LeadsOffThisMachine(path))
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

        if (SyncedFolder(full, read) is { } synced)
        {
            return Said(full, synced);
        }

        return DriveProblem(full) is { } drive ? Said(full, drive) : null;
    }

    private static string Said(string path, string what) =>
        $"{path} is on {what}. Parley keeps its work only on this machine's own disks: move it to a local folder.";

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
