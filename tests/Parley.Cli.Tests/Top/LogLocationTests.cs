using Parley.Cli.Top;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// Where the log view looks, given what the supervisor runs.
/// </summary>
/// <remarks>
/// Found by reading the feature's own assumption — "the broker's log is found beside the store,
/// as the installer already reports" — after the installer stopped reporting that. An entry
/// serving the manifest logs beside the manifest, and on the machine that switched entries the
/// old log was still beside the old store: the view would have followed a broker that stopped
/// writing hours earlier and called it live.
/// </remarks>
public sealed class LogLocationTests
{
    private static readonly ProjectSettings Alpha = new(
        ProjectName.Create("alpha"),
        Path.Combine(Path.GetTempPath(), "alpha", "alpha.db"),
        "http://127.0.0.1:1",
        null,
        null);

    [Fact]
    public void An_entry_serving_the_manifest_logs_beside_the_manifest()
    {
        var manifest = Path.Combine(Path.GetTempPath(), "config", "projects.json");
        var entry = new InstalledEntry(null, null, null, null, manifest);

        Assert.Equal(
            Path.Combine(Path.GetTempPath(), "config", LogTail.FileName),
            LogTail.For(Alpha, entry));
    }

    [Fact]
    public void An_explicit_entry_and_no_entry_log_beside_the_store()
    {
        var explicitEntry = new InstalledEntry(Alpha.Store, null, null, null, null);

        var beside = Path.Combine(Path.GetTempPath(), "alpha", LogTail.FileName);

        Assert.Equal(beside, LogTail.For(Alpha, explicitEntry));
        Assert.Equal(beside, LogTail.For(Alpha, null));
        Assert.Equal(beside, LogTail.For(Alpha));
    }
}
