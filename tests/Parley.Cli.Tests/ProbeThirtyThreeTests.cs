using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Review gate, lens 2, run 22: the release's own operational risk.
/// </summary>
/// <remarks>
/// <para>
/// This is the version that removes <c>--with-console</c>, and the owner's scheduled task holds it
/// right now. 007 designed the deprecation around exactly that — <i>stop writing, stop accepting, keep
/// reading</i> — and 008 moved the date on the grounds that an operator whose broker will not start at
/// logon cannot tell which half did it.
/// </para>
/// <para>
/// The broker does start: every verb silently ignores options it has no use for, so the entry keeps
/// working and quietly loses its web UI. That is the safer of the two failures and it is still a
/// change nothing announces. <c>InstalledEntry</c> exists to read an entry back so
/// <c>autostart status</c> can say what is installed, which makes it the one place that can notice.
/// </para>
/// </remarks>
public sealed class ProbeThirtyThreeTests
{
    /// <summary>An entry as it was written before this version, as a live machine still holds it.</summary>
    private const string Legacy =
        "serve --db C:\\parley\\a.db --listen http://127.0.0.1:7000 --with-console --console http://127.0.0.1:5099";

    private const string Current =
        "serve --config C:\\parley\\projects.json --with-web-ui --web-ui http://127.0.0.1:5099";

    [Fact]
    public void An_entry_naming_a_removed_flag_is_reported_as_such()
    {
        // The whole of what an operator gets otherwise is an absence: the web UI stops appearing and
        // nothing anywhere connects that to an upgrade. `autostart status` is the command whose job
        // is to say what is installed, so it is where this belongs.
        var entry = InstalledEntry.Parse(Legacy)!;

        Assert.NotEmpty(entry.Removed);
        Assert.Contains("--with-console", entry.Removed);
    }

    [Fact]
    public void The_web_ui_address_is_still_read_from_the_old_spelling()
    {
        // Unchanged and must be: `--console` is *read* for ever, which is the half of the
        // deprecation that keeps `autostart status` honest about entries written before 0.19.0.
        var entry = InstalledEntry.Parse(Legacy)!;

        Assert.Equal("http://127.0.0.1:5099", entry.Console);
    }

    [Fact]
    public void An_entry_written_by_this_version_names_nothing_removed()
    {
        // The control. A check that flagged every entry would be worth nothing, and would flag the
        // one a reinstall had just written.
        Assert.Empty(InstalledEntry.Parse(Current)!.Removed);
    }
}
