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
/// <b>Somewhere else, it launches nothing and says where the file is.</b> The broker says which machine holds the
/// location; a surface on another machine has nothing on its own disk to open, and a copy would not be the shared
/// file. A path that does not exist here is treated the same way, so a machine
/// name that happens to match cannot launch the wrong thing.
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
public sealed class LocalLauncher(Func<string, bool, bool> open, string machine)
{
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
        ArgumentException.ThrowIfNullOrEmpty(copies);

        if (read.File is not { } file || read.Bytes is not { } bytes)
        {
            return new Launched(false, read.TooLarge
                ? $"{read.File?.Name.Value} is too large to copy here, so nothing was opened."
                : read.Refusal?.Detail ?? "That file is not in the location any more.");
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

        var folder = Path.Combine(copies, file.Sha256.Value);
        var path = Path.Combine(folder, name);

        Directory.CreateDirectory(folder);

        if (!File.Exists(path))
        {
            var writing = Path.Combine(folder, $".{Guid.NewGuid():N}.copying");
            File.WriteAllBytes(writing, bytes.ToArray());
            File.Move(writing, path, overwrite: true);
        }

        return _open(path, false)
            ? new Launched(true, $"Opened a copy of {name}, at {path}.")
            : new Launched(false, $"No program is registered for {Path.GetExtension(path)} files here. The copy is at {path}.");
    }

    /// <summary>Where a surface on this machine keeps the copies it opens.</summary>
    public static string Copies { get; } = Path.Combine(Path.GetTempPath(), "parley-copies");

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
