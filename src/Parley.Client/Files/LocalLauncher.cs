using System.ComponentModel;
using System.Diagnostics;
using Parley.Core;

namespace Parley.Client.Files;

/// <summary>
/// What opening a file or a folder came to.
/// </summary>
/// <param name="Opened">Whether anything was launched.</param>
/// <param name="Said">What to tell the person, in either case.</param>
public sealed record Launched(bool Opened, string Said);

/// <summary>
/// Opens a placed file to read in the application registered for it, or a conversation's folder in the file browser,
/// on the machine this surface runs on — and on no other (SPEC.md §10.2, §13).
/// </summary>
/// <remarks>
/// <para>
/// <b>One implementation for the command line, the terminal and the web UI</b>, because the three differ only in
/// where the result is shown, and three launchers would be three chances to open something one of them refuses.
/// </para>
/// <para>
/// <b>Somewhere else, <see cref="OpenFile"/> launches nothing and says where the file is.</b> The broker says which
/// machine holds the location, and a surface on another machine has nothing on its own disk to open in place. A path
/// that does not exist here is treated the same way, so a machine name that happens to match cannot launch the wrong
/// thing. Every surface opens a file through <see cref="OpenPlacedAsync"/>, which on another machine opens a copy read
/// through the broker (<see cref="OpenCopy"/>): the same file, because a placed file never changes. A folder is opened
/// only in place.
/// </para>
/// <para>
/// <b>Only what can be placed, and only a regular file.</b> Nothing else can be placed, so this is the second
/// lock on that door rather than the first: "open with the registered application" runs a <c>.bat</c> on Windows,
/// and a file in the location that got there by some other route must not be one click from running.
/// </para>
/// <para>
/// <b>To read, not to edit.</b> A placed file is never changed (§9.5), so it is opened as the system opens it rather
/// than with an edit verb; bytes saved over it make every later fetch of it refused.
/// </para>
/// <para>
/// A seam, like the terminal's browser opener, because a test suite that opened an editor on every run would open
/// one on the machine of whoever ran it.
/// </para>
/// </remarks>
/// <param name="open">Launches a path: <see langword="true"/> for a folder. Returns whether something was launched.</param>
/// <param name="machine">This machine's name, as the broker spells machines.</param>
/// <param name="copies">Where copies go, for a test; <see langword="null"/> is <see cref="Copies"/>, this user's own.</param>
public sealed class LocalLauncher(Func<string, bool, bool> open, string machine, string? copies = null)
{
    private readonly string _copies = copies ?? Copies;

    private readonly Func<string, bool, bool> _open = open ?? throw new ArgumentNullException(nameof(open));

    private readonly string _machine = machine ?? throw new ArgumentNullException(nameof(machine));

    /// <summary>The launcher that really opens things, on the machine this process runs on.</summary>
    public static LocalLauncher ForThisMachine { get; } = new(OpenWithTheSystem, MachineName.Current);

    /// <summary>Opens a file to read, with the application registered for it.</summary>
    /// <param name="location">Where the broker says the file is.</param>
    /// <returns>Whether it was opened, and what to say.</returns>
    public Launched OpenFile(FileLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);

        var path = location.Path;
        var name = Path.GetFileName(path);

        if (!IsHere(location) || !File.Exists(path))
        {
            return new Launched(false, $"{name} is on machine {location.Machine}, at {path}.");
        }

        if (!SharedFileName.HasAllowedExtension(path))
        {
            return new Launched(
                false,
                $"Only {SharedFileName.AllowedExtensionsInWords} files are opened, and {name} is none of those, so nothing was launched. It is at {path}.");
        }

        if (new FileInfo(path).LinkTarget is not null)
        {
            return new Launched(false, $"{name} is a link rather than a file, so nothing was launched. It is at {path}.");
        }

        return _open(path, false)
            ? new Launched(true, $"Opened {path}.")
            : new Launched(false, $"No editor is registered for {Path.GetExtension(path)} files here. The file is at {path}.");
    }

    /// <summary>Opens a folder in the file browser.</summary>
    /// <param name="location">Where the broker says the folder is.</param>
    /// <returns>Whether it was opened, and what to say.</returns>
    public Launched OpenFolder(FileLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);

        var path = location.Path;

        if (!IsHere(location) || !Directory.Exists(path))
        {
            return new Launched(false, $"The files are on machine {location.Machine}, in {path}.");
        }

        return _open(path, true)
            ? new Launched(true, $"Opened {path}.")
            : new Launched(false, $"Nothing here could open a folder. The files are in {path}.");
    }

    /// <summary>Whether the broker's machine, as a location names it, is this one.</summary>
    /// <param name="location">Where the broker says something is.</param>
    /// <returns>Whether it is on this machine.</returns>
    public bool IsOnThisMachine(FileLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);

        return IsHere(location);
    }

    /// <summary>Opens a copy of a placed file, read through the broker, when the file is on another machine.</summary>
    /// <param name="read">The read: the file and its whole content.</param>
    /// <param name="copies">The folder copies go in, a folder of this surface's own.</param>
    /// <returns>Whether it was opened, and what to say.</returns>
    /// <remarks>
    /// <para>
    /// <b>A copy is the same file, because a placed file never changes</b> (SPEC.md §9.5): its name and hash say which
    /// bytes, and the read checked them. So a surface at the end of a tunnel can show the person an agent's image, which
    /// the terminal has no other way to do (§10.2).
    /// </para>
    /// <para>
    /// The same locks as opening in place: only a name that can be placed, and an image only when its leading bytes are
    /// its format, so a file named <c>.png</c> that is something else is never handed to whatever opens images. Each copy
    /// sits in a folder named by its hash, so two files of one name never meet and a copy is never written twice.
    /// </para>
    /// </remarks>
    public Launched OpenCopy(SharedFileRead read, string copies)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(copies);

        if (read.File is not { } file || read.Bytes is not { } bytes)
        {
            return new Launched(false, read.TooLarge
                ? $"{read.File?.Name.Value} is too large to copy here, so nothing was opened."
                : read.Refusal is { Detail: { Length: > 0 } detail }
                    ? detail
                    : read.Refusal?.Rejection switch
                    {
                        // The bytes read were not the placed file's: say so, rather than that the file is gone.
                        FileRejection.Changed or FileRejection.Tampered =>
                            $"{read.File?.Name.Value ?? "That file"} changed since it was placed, so nothing was copied or opened.",
                        _ => "That file is not in the location any more.",
                    });
        }

        var name = file.Name.Value;

        if (!SharedFileName.HasAllowedExtension(name))
        {
            return new Launched(false, $"Only {SharedFileName.AllowedExtensionsInWords} files are opened, and {name} is none of those.");
        }

        if (ImageSignature.IsImageName(name) && !ImageSignature.Matches(name, bytes.Span))
        {
            return new Launched(false, $"{name} is not a readable image, so nothing was opened.");
        }

        if (copies.Length == 0)
        {
            return new Launched(false, "This account has no folder of its own to keep a copy in, so nothing was opened.");
        }

        var folder = Path.Combine(copies, file.Sha256.Value);
        var path = Path.Combine(folder, name);

        try
        {
            return WriteAndOpen(copies, folder, path, name, file.Sha256.Value, bytes);
        }
        catch (Exception failed) when (failed is IOException or UnauthorizedAccessException)
        {
            return new Launched(false, $"No copy of {name} could be written in {folder}, so nothing was opened: {failed.Message}");
        }
    }

    private Launched WriteAndOpen(string copies, string folder, string path, string name, string sha256, ReadOnlyMemory<byte> bytes)
    {
        // **Nothing is written through a folder somebody else could have made.** Each folder on the way is made private
        // and refused when it is a link, or (off Windows) when anyone but this user can write in it: another account on
        // the machine that made the folder first could otherwise point it at the person's own repository, and the copy
        // would land there under its placed name -- CLAUDE.md is one.
        if ((Unsafe(copies) ?? Unsafe(folder)) is { } refused)
        {
            return new Launched(false, refused);
        }

        // **A copy already there is opened only when it is still the placed file.** Anything else under its name is
        // replaced, so what is opened is always the bytes the read checked.
        if (!IsTheCopy(path, sha256))
        {
            var writing = Path.Combine(folder, $".{Guid.NewGuid():N}.copying");
            File.WriteAllBytes(writing, bytes.ToArray());
            File.Move(writing, path, overwrite: true);
        }

        return _open(path, false)
            ? new Launched(true, $"Opened a copy of {name}, at {path}.")
            : new Launched(false, $"No program is registered for {Path.GetExtension(path)} files here. The copy is at {path}.");
    }

    /// <summary>
    /// Opens a placed file: in place on the broker's machine, or a checked copy read through the broker anywhere else.
    /// </summary>
    /// <param name="broker">The operator's client.</param>
    /// <param name="conversation">The file's conversation; a copy cannot be read without it.</param>
    /// <param name="name">The file's name.</param>
    /// <param name="expected">The hash the person saw, when known: a file placed again since reads as changed.</param>
    /// <param name="location">Where the broker said the file is.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>Whether it was opened, and what to say.</returns>
    /// <remarks>
    /// The one way every surface opens a file (SPEC.md §10.2), so the web console and the command line at the end of a
    /// tunnel open what the terminal does, rather than saying where a file is on a machine the person is not at.
    /// </remarks>
    public async Task<Launched> OpenPlacedAsync(
        IParleyBrokerClient broker,
        string? conversation,
        string name,
        ContentHash? expected,
        FileLocation location,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(location);

        if (IsHere(location))
        {
            return OpenFile(location);
        }

        if (string.IsNullOrEmpty(conversation))
        {
            // A copy is read by conversation and name. One file of this name in the whole location is the one meant, as
            // the broker's own lookup said by finding it; its hash comes with it.
            var matching = new List<SharedFile>();
            string? next = null;

            do
            {
                var listing = await broker.ListSharedFilesAsync(null, FileLimits.MaximumPageSize, next, cancellationToken)
                    .ConfigureAwait(false);

                matching.AddRange(listing.Page.Files.Where(f => string.Equals(f.Name.Value, name, StringComparison.OrdinalIgnoreCase)));
                next = listing.Page.NextPageToken;
            }
            while (next is not null);

            if (matching.Count != 1)
            {
                return new Launched(false, $"{name} is on {location.Machine}. Name its conversation to open a copy of it here.");
            }

            conversation = matching[0].ConversationId;
            expected ??= matching[0].Sha256;
        }

        var read = await broker.ReadSharedFileAsync(conversation, name, expected, Placements.CopyLimitBytes, cancellationToken)
            .ConfigureAwait(false);

        return OpenCopy(read, _copies);
    }

    /// <summary>Where a surface on this machine keeps the copies it opens: this user's own application data.</summary>
    /// <remarks>
    /// Not the temp folder, which on Linux is <c>/tmp</c>, shared by every account on the machine.
    /// Looked up without creating anything, so a home folder that cannot be written costs the copies and nothing else: a
    /// lookup that threw here would stop the whole type loading, and with it every surface that opens a file. An empty
    /// answer leaves no copies folder at all, never the shared temp folder.
    /// </remarks>
    public static string Copies { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify) is { Length: > 0 } own
            ? Path.Combine(own, "parley", "copies")
            : string.Empty;

    /// <summary>Makes a folder this user's alone, or says why it cannot be written through.</summary>
    /// <param name="folder">The folder.</param>
    /// <returns>Null when it is safe to write in, or what to say.</returns>
    private static string? Unsafe(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder);
        }
        else
        {
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var info = new DirectoryInfo(folder);

        if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return $"{folder} is a link, so no copy was written through it and nothing was opened.";
        }

        if (!OperatingSystem.IsWindows()
            && (File.GetUnixFileMode(folder) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            return $"{folder} can be written by other accounts on this machine, so no copy was written in it and nothing was opened.";
        }

        return null;
    }

    /// <summary>Whether a file is already the copy of the placed file with this hash, and not a link.</summary>
    /// <param name="path">Where the copy goes.</param>
    /// <param name="sha256">The placed file's hash, lowercase hex.</param>
    /// <returns>True when it may be opened as it is.</returns>
    private static bool IsTheCopy(string path, string sha256)
    {
        var info = new FileInfo(path);

        if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return false;
        }

        try
        {
            using var stream = info.OpenRead();

            return string.Equals(
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)),
                sha256,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private bool IsHere(FileLocation location) =>
        string.Equals(location.Machine, _machine, StringComparison.OrdinalIgnoreCase);

    private static bool OpenWithTheSystem(string path, bool folder)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return TryStart(new ProcessStartInfo(path) { UseShellExecute = true });
            }

            return TryStart(OperatingSystem.IsMacOS()
                ? new ProcessStartInfo("open") { ArgumentList = { path } }
                : new ProcessStartInfo("xdg-open") { ArgumentList = { path } });
        }
        catch (Exception unavailable) when (unavailable is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryStart(ProcessStartInfo start)
    {
        try
        {
            using var process = Process.Start(start);
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
