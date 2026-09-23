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
/// Opens a placed file in its registered editor, or a conversation's folder in the file browser, on the machine
/// this surface runs on — and on no other (SPEC.md §10.2, §13).
/// </summary>
/// <remarks>
/// <para>
/// <b>One implementation for the command line, the terminal and the web UI</b>, because the three differ only in
/// where the result is shown, and three launchers would be three chances to open something one of them refuses.
/// </para>
/// <para>
/// <b>Somewhere else, it launches nothing and says where the file is.</b> The broker says which machine holds the
/// location; a surface on another machine has nothing on its own disk to open, and opening a copy would edit
/// something that is not the shared file. A path that does not exist here is treated the same way, so a machine
/// name that happens to match cannot launch the wrong thing.
/// </para>
/// <para>
/// <b>Only <c>.md</c> and <c>.txt</c>, and only a regular file.</b> Nothing else can be placed, so this is the second
/// lock on that door rather than the first: "open with the registered application" runs a <c>.bat</c> on Windows,
/// and a file in the location that got there by some other route must not be one click from running.
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

    /// <summary>Opens a file with the application registered to edit it.</summary>
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

        if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            return new Launched(
                false,
                $"Only .md and .txt files are opened, and {name} is neither, so nothing was launched. It is at {path}.");
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

    private bool IsHere(FileLocation location) =>
        string.Equals(location.Machine, _machine, StringComparison.OrdinalIgnoreCase);

    private static bool OpenWithTheSystem(string path, bool folder)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!folder && TryStart(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "edit" }))
                {
                    return true;
                }

                return TryStart(new ProcessStartInfo(path) { UseShellExecute = true });
            }

            var start = OperatingSystem.IsMacOS()
                ? new ProcessStartInfo("open") { ArgumentList = { folder ? path : "-t", } }
                : new ProcessStartInfo("xdg-open") { ArgumentList = { path } };

            if (OperatingSystem.IsMacOS() && !folder)
            {
                start.ArgumentList.Add(path);
            }

            return TryStart(start);
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
