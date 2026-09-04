using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// The entry that serves the manifest, and reading either kind of entry back.
/// </summary>
/// <remarks>
/// <para>
/// The autostart entry used to carry one explicit store, which meant every project named after
/// the install was "not answering" at the next logon and nothing said why. The manifest-serving
/// entry is the fix, and what this checks is the shape of it: no store, the manifest's path
/// written in, the node switched on, the log beside the manifest rather than beside a store it
/// does not have.
/// </para>
/// <para>
/// Reading an entry back is checked against what <see cref="AutostartPlan"/> writes, because that
/// is the only writer. The installer used to do this with a regular expression over a scheduled
/// task's arguments on one platform; a round trip through the same quoting on every platform is
/// what stops a path with a space in it coming apart on the way back.
/// </para>
/// </remarks>
public sealed class AutostartManifestPlanTests
{
    private const string Manifest = "/tmp/parley/projects.json";

    [Fact]
    public void A_manifest_entry_names_the_manifest_and_no_store()
    {
        var plan = AutostartPlan.DescribeManifest(
            Manifest, executable: "/usr/local/bin/parley", consoleOn: "http://127.0.0.1:5280");

        Assert.StartsWith(
            $"serve --config {Path.GetFullPath(Manifest)}", plan.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("--db", plan.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("--listen", plan.Arguments, StringComparison.Ordinal);

        // The node is always on: with several projects the broker hosts one per catalogued
        // project, so a project catalogued after the install is hosted at the next start.
        Assert.Contains("--with-node", plan.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("--node-config", plan.Arguments, StringComparison.Ordinal);

        Assert.Contains(
            "--with-console --console http://127.0.0.1:5280", plan.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void The_manifest_entrys_log_sits_beside_the_manifest()
    {
        // There is no single store to be beside, and the manifest's directory is the one this
        // machine's configuration already lives in.
        var plan = AutostartPlan.DescribeManifest(Manifest, executable: "parley");

        Assert.Equal(
            Path.GetDirectoryName(Path.GetFullPath(Manifest)),
            Path.GetDirectoryName(plan.LogPath));
    }

    [Fact]
    public void A_manifest_entry_reads_back_as_serving_the_manifest()
    {
        var plan = AutostartPlan.DescribeManifest(
            Manifest, executable: "parley", consoleOn: "http://127.0.0.1:5280");

        var entry = InstalledEntry.Parse(plan.Arguments);

        Assert.NotNull(entry);
        Assert.True(entry.ServesManifest);
        Assert.Null(entry.Store);
        Assert.Equal(Path.GetFullPath(Manifest), entry.Manifest);
        Assert.Equal("http://127.0.0.1:5280", entry.Console);
        Assert.Contains("every project in", entry.Serves, StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicit_entry_reads_back_with_every_path_intact()
    {
        // Paths with spaces on both sides of the round trip, because that is the case the quoting
        // exists for and the one a regular expression got wrong.
        var plan = AutostartPlan.Describe(
            "C:/Some Folder/parley.db",
            "http://127.0.0.1:9999",
            executable: "parley",
            nodeConfig: "C:/Some Folder/node.json",
            consoleOn: "http://127.0.0.1:5280");

        var entry = InstalledEntry.Parse(plan.Arguments);

        Assert.NotNull(entry);
        Assert.False(entry.ServesManifest);
        Assert.Equal(Path.GetFullPath("C:/Some Folder/parley.db"), entry.Store);
        Assert.Equal("http://127.0.0.1:9999", entry.Listen);
        Assert.Equal(Path.GetFullPath("C:/Some Folder/node.json"), entry.NodeConfig);
        Assert.Equal("http://127.0.0.1:5280", entry.Console);
        Assert.Null(entry.Manifest);
    }

    [Fact]
    public void Something_that_is_not_a_serve_command_line_is_not_an_entry()
    {
        // A supervisor entry somebody pointed at something else is not this tool's, and reading
        // it as an entry with no store would offer to name a deployment that does not exist.
        Assert.Null(InstalledEntry.Parse(null));
        Assert.Null(InstalledEntry.Parse(string.Empty));
        Assert.Null(InstalledEntry.Parse("up --node-config node.json"));
    }
}
