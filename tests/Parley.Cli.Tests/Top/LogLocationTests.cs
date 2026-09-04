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
    public async Task The_log_view_follows_the_manifest_entrys_log_and_not_the_stale_one_beside_the_store()
    {
        // The post-fix probe on the final tree, end to end through the menu: both files exist,
        // as they do on the machine that switched entries, and only the live one is shown.
        var directory = Path.Combine(Path.GetTempPath(), "parley-loglocation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "store"));
        Directory.CreateDirectory(Path.Combine(directory, "config"));

        try
        {
            var ct = TestContext.Current.CancellationToken;

            var manifest = Path.Combine(directory, "config", "projects.json");
            var project = new ProjectSettings(
                ProjectName.Create("alpha"),
                Path.Combine(directory, "store", "alpha.db"),
                "http://127.0.0.1:1",
                null,
                null);

            Parley.Configuration.ProjectManifest.Write(manifest, [project]);

            await File.WriteAllTextAsync(
                Path.Combine(directory, "store", LogTail.FileName), "STALE LINE FROM THE OLD ENTRY\n", ct);
            await File.WriteAllTextAsync(
                Path.Combine(directory, "config", LogTail.FileName), "LIVE LINE FROM THE MANIFEST ENTRY\n", ct);

            var entry = new InstalledEntry(null, null, null, null, manifest);

            using var console = Driving.Interactive();

            Driving.Pick(console, ProjectList.Choices([project], entry), "alpha");
            Driving.Pick(console, ProjectMenu.Choices(seated: false), ProjectMenu.Choices(seated: false).Single(c => c.Contains("log", StringComparison.Ordinal)));
            console.Input.PushTextWithEnter(string.Empty);   // no filter

            // The view stops on a real-console ReadLine, which returns at once under a test host,
            // and it shows only lines written after it opened -- so what is asserted is the file
            // it announces it is following, which is the whole of the defect: the wrong file.
            Driving.Pick(console, ProjectMenu.Choices(seated: false), ProjectMenu.Back);
            Driving.Pick(console, ProjectList.Choices([project], entry), ProjectList.Leave);

            using var broker = Parley.Client.BrokerDirectory.FromManifest(manifest);
            using var seats = new Parley.Client.Seating.SeatBook(broker.AgentsFor);

            var exit = await TopCommand.DriveAsync(console, broker, seats, ct, entry);

            Assert.Equal(0, exit);

            var following = console.Output[console.Output.IndexOf("Following", StringComparison.Ordinal)..];
            var firstLine = following[..following.IndexOf('\n')];

            Assert.Contains(Path.Combine("config", LogTail.FileName), firstLine, StringComparison.Ordinal);
            Assert.DoesNotContain(Path.Combine("store", LogTail.FileName), firstLine, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
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
