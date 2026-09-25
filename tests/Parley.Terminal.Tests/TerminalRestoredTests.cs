using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That leaving this surface abruptly does not ruin the terminal it was running in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reported by the owner as "the mouse capture issue": <c>[555;93;23M</c> printed at the shell
/// prompt on every mouse movement, for the rest of that window's life.</b> They believed it had
/// been fixed and it never had; the first thing this suite establishes is what actually causes it.
/// </para>
/// <para>
/// Terminal.Gui asks the terminal to report mouse movement on start-up and asks it to stop on
/// shutdown, and <see cref="TerminalCommand"/> deliberately lets Ctrl+C terminate the process
/// without a shutdown — argued there at length, and right about the seat, which a dying connection
/// releases. It is silent about the console, which nothing else puts back.
/// </para>
/// </remarks>
public sealed partial class TerminalRestoredTests
{
    [Fact]
    public void Every_mode_the_toolkit_switches_on_is_switched_off_again()
    {
        // **The list is checked against the toolkit rather than against itself.** A hand-kept list
        // is right on the day it is written and silently wrong at the next upgrade — and the
        // symptom of being wrong is not a failing test, it is somebody's terminal. Scanning the
        // assembly for what it enables is the only version of this check that keeps working.
        var enabled = Enables().Matches(Toolkit())
            .Select(match => int.Parse(match.Groups["mode"].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Distinct()
            .Order()
            .ToArray();

        // Non-vacuity: if the scan finds nothing, every assertion below passes while checking
        // nothing at all — the shape this repository records as "a regex that silently matches
        // nothing makes every other check pass by vacuity".
        Assert.NotEmpty(enabled);

        var restored = ConsoleRestore.Modes.Order().ToArray();

        Assert.Equal(enabled, restored);
    }

    [Fact]
    public void Restoring_writes_the_off_sequence_for_each_of_them()
    {
        var wrote = new StringWriter();

        ConsoleRestore.Restore(wrote);

        var said = wrote.ToString();

        foreach (var mode in ConsoleRestore.Modes)
        {
            Assert.Contains($"[?{mode}l", said, StringComparison.Ordinal);
        }

        // Shown again as well. A toolkit that hid the cursor and died leaves somebody typing blind,
        // which is the same damage arriving by the same route and just as hard to attribute.
        Assert.Contains("[?25h", said, StringComparison.Ordinal);
    }

    [Fact]
    public void It_never_leaves_a_mode_switched_on()
    {
        // The inverse of the test above, and worth its own case: a sequence that enabled something
        // on the way out would be this type causing the fault it exists to undo. `h` is the enable
        // form, `l` the disable.
        var wrote = new StringWriter();

        ConsoleRestore.Restore(wrote);

        Assert.DoesNotContain("h", wrote.ToString().Replace("[?25h", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Disposing_writes_nothing_because_the_toolkit_has_already_done_it()
    {
        // **The ordinary path is the toolkit's and this must not join in.** Terminal.Gui's own
        // shutdown restores the terminal when the process exits normally; a second copy of the
        // sequences would be this type guessing at a job somebody else has finished, and the guess
        // would be wrong the moment the toolkit changes what it restores.
        var wrote = new StringWriter();

        using (ConsoleRestore.Arm(wrote))
        {
            // Nothing: this is the ordinary path, where the surface ran and stopped.
        }

        Assert.Equal(string.Empty, wrote.ToString());
    }

    [Fact]
    public void The_surface_arms_it_before_the_toolkit_starts()
    {
        // Read off the source, because the property is an ordering: armed before `Application` is
        // created, so that a crash inside `Init` is covered too, and — since locals are disposed in
        // reverse — disarmed only after the toolkit's own shutdown has run.
        var source = File.ReadAllText(
            Path.Combine(Root(), "src", "Parley.Terminal", "TerminalCommand.cs"));

        var armed = source.IndexOf("ConsoleRestore.Arm()", StringComparison.Ordinal);
        var created = source.IndexOf("Application.Create(", StringComparison.Ordinal);

        Assert.True(armed >= 0, "the terminal no longer restores the console it was given");
        Assert.True(created >= 0, "the terminal no longer creates an application");
        Assert.True(
            armed < created,
            "the restore is armed after the toolkit starts, so a failure inside Init leaves the "
            + "terminal in whatever state the driver had reached");
    }

    /// <summary>The repository root, found by walking up from the build output.</summary>
    /// <returns>The directory holding <c>src</c>.</returns>
    /// <remarks>
    /// The same walk <c>DeadSurfaceTests</c> makes. Copied rather than shared because this project
    /// links its helpers from <c>Parley.Cli.Tests</c> and <c>RepositoryFile</c> is not among them —
    /// linking a file to reach four lines would be a build change for one method.
    /// </remarks>
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }

    /// <summary>The toolkit's own bytes.</summary>
    /// <returns>Terminal.Gui, as text, in both the encodings a .NET assembly stores strings in.</returns>
    /// <remarks>
    /// Literals in a .NET assembly are UTF-16, and the file also carries ASCII metadata, so both
    /// readings are searched. Reading the loaded assembly's own location rather than a path under
    /// the package cache: that is the build these tests actually run against, which is the one
    /// whose behaviour is being asserted.
    /// </remarks>
    private static string Toolkit()
    {
        // Rooted at the global namespace: this one is `Parley.Terminal.Tests`, so a bare
        // `Terminal.Gui` resolves against `Parley.Terminal` and does not exist.
        var at = typeof(global::Terminal.Gui.App.Application).Assembly.Location;

        Assert.True(File.Exists(at), $"Terminal.Gui is not on disk at {at}");

        var bytes = File.ReadAllBytes(at);

        return Encoding.Unicode.GetString(bytes) + Encoding.Latin1.GetString(bytes);
    }

    /// <summary>A DEC private mode being switched on: <c>?1003h</c>.</summary>
    [GeneratedRegex(@"\?(?<mode>\d{4})h")]
    private static partial Regex Enables();
}
