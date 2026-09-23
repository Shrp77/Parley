using Parley.Configuration;
using Parley.Core;
using Xunit;
using static Parley.Cli.Tests.Loopback;

namespace Parley.Cli.Tests;

/// <summary>
/// What `parley serve` serves when a store is named beside a manifest, and which catalogue hosts a lone project's node.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both were found by the gate over the installer change, and neither was visible to the suite</b>, because every test
/// that starts a broker by explicit store isolates its manifest — which is exactly the arrangement in which the store is
/// honoured. On a machine whose manifest names projects, `serve --db` and <c>PARLEY_DB</c> served the manifest and ignored
/// the store, while the help and the README said they serve one store instead of it.
/// </para>
/// <para>
/// The second is the waiting path's own. A machine with one project hosts its node with the resolution `parley up` has,
/// so <c>PARLEY_NODE_CONFIG</c> wins over the entry — but a project named while serve waited was treated as a manifest
/// being served and hosted from its entry instead, so the same machine changed catalogues at its next start.
/// </para>
/// </remarks>
public sealed class ServeSelectionTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-selection-" + Guid.NewGuid().ToString("N"));

    public ServeSelectionTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    // **The root rather than the manifest file.** `--config` and PARLEY_CONFIG name the directory
    // this machine keeps its Parley files in; the projects are the folders under it.
    private string Root => _directory;

    private void WriteManifest(string? catalogue = null) =>
        ProjectManifest.Write(
            Root,
            [
                new ProjectSettings(
                    ProjectName.Create("orders"),
                    Path.Combine(_directory, "manifest.db"),
                    $"http://127.0.0.1:{FreePort()}",
                    null),
            ]);

    public static TheoryData<bool> ByVariable => [false, true];

    [Theory]
    [MemberData(nameof(ByVariable))]
    public async Task A_store_named_beside_a_manifest_is_served_instead_of_it(bool byVariable)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Path.Combine(_directory, "named.db");

        WriteManifest();

        using var process = byVariable
            ? ToolProcess.Launch(
                [(ProjectManifest.PathVariable, Root), (DeploymentEnvironment.DatabaseVariable, store)],
                "serve", "--listen", "http://127.0.0.1:0")
            : ToolProcess.Launch(
                [(ProjectManifest.PathVariable, Root)],
                "serve", "--db", store, "--listen", "http://127.0.0.1:0");

        var log = ToolLog.Of(process);

        try
        {
            await log.UntilAsync(l => l.Said("http://127.0.0.1:") || process.HasExited, TimeSpan.FromSeconds(20), ct);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(5000);
            }
        }

        Assert.True(File.Exists(store), $"the named store was not opened. The tool said:{Environment.NewLine}{log}");
        Assert.False(File.Exists(Path.Combine(_directory, "manifest.db")), $"the manifest's project was served. The tool said:{Environment.NewLine}{log}");
        Assert.False(log.Said("orders"), $"the manifest's project was named. The tool said:{Environment.NewLine}{log}");
    }

    [Fact]
    public async Task A_store_variable_does_not_replace_a_manifest_named_on_the_command_line()
    {
        // The logon entry names its manifest with --config. The first version of the rule above let a PARLEY_DB left in
        // the environment turn that entry into a broker for the leftover store, serving none of the manifest's projects.
        var ct = TestContext.Current.CancellationToken;
        var leftover = Path.Combine(_directory, "leftover.db");

        WriteManifest();

        using var process = ToolProcess.Launch(
            [(ProjectManifest.PathVariable, Root), (DeploymentEnvironment.DatabaseVariable, leftover)],
            "serve", "--config", Root);

        var log = ToolLog.Of(process);

        try
        {
            await log.UntilAsync(l => l.Said("orders on http://127.0.0.1:") || process.HasExited, TimeSpan.FromSeconds(20), ct);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(5000);
            }
        }

        Assert.True(log.Said("orders on http://127.0.0.1:"), $"the manifest's project was not served. The tool said:{Environment.NewLine}{log}");
        Assert.False(File.Exists(leftover), $"the leftover store was opened. The tool said:{Environment.NewLine}{log}");
    }

    [Fact]
    public async Task A_lone_project_hosts_from_the_same_catalogue_whether_it_was_named_before_the_start_or_after()
    {
        var ct = TestContext.Current.CancellationToken;
        var fromVariable = Path.Combine(_directory, "variable-node.json");
        var fromEntry = Path.Combine(_directory, "entry-node.json");

        (string Name, string Value)[] environment =
        [
            (ProjectManifest.PathVariable, Root),
            (NodeCatalogue.PathVariable, fromVariable),
        ];

        // Named while serve waits: the machine that the installer leaves, its first project named afterwards.
        var namedAfter = await HostedFromAsync(environment, () => WriteManifest(fromEntry), ct);

        // The same manifest already there: that machine's next start.
        var namedBefore = await HostedFromAsync(environment, () => { }, ct);

        Assert.Contains(fromVariable, namedBefore, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(fromVariable, namedAfter, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> HostedFromAsync(
        (string Name, string Value)[] environment, Action whileRunning, CancellationToken ct)
    {
        using var process = ToolProcess.Launch(environment, "serve", "--config", Root, "--with-node");

        var log = ToolLog.Of(process);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            whileRunning();
            await log.UntilAsync(l => l.Said("hosting this machine's node") || l.Said("Hosting this machine's node"), TimeSpan.FromSeconds(30), ct);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(5000);
            }
        }

        var said = log.ToString();

        Assert.True(
            said.Contains("osting this machine's node", StringComparison.Ordinal),
            $"no node was hosted. The tool said:{Environment.NewLine}{said}");

        return said;
    }
}
