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

    [Fact]
    public async Task Nothing_can_make_serve_open_a_store_this_machine_did_not_name()
    {
        // **Two tests lived here and both were about which of two sources wins.**
        // `A_store_named_beside_a_manifest_is_served_instead_of_it` checked that `--db` and `PARLEY_DB`
        // beat a manifest; `A_store_variable_does_not_replace_a_manifest_named_on_the_command_line`
        // checked that the variable did *not* beat `--config`. Both rules were written after the review
        // gate found the defect each exists to prevent — a machine serving projects while ignoring the
        // store it was told, and a stray variable turning the logon entry into a broker for a test store.
        //
        // **Both questions are gone, because there is one source.** A project's store is derived from its
        // name; nothing names one. So what is asserted is that the flag and the variable are refused
        // rather than honoured, and that what gets served is what the root holds.
        var ct = TestContext.Current.CancellationToken;
        var store = Path.Combine(_directory, "named.db");

        WriteManifest();

        foreach (var attempt in new[]
                 {
                     new[] { "serve", "--db", store },
                     new[] { "serve", "--listen", "http://127.0.0.1:0" },
                 })
        {
            var (code, _, error) = ToolProcess.RunFully([(ProjectManifest.PathVariable, Root)], attempt);

            Assert.Equal(64, code);
            Assert.Contains(attempt[1], error, StringComparison.Ordinal);
            Assert.Contains("parley terminal", error, StringComparison.Ordinal);
        }

        // And the variable that used to replace a manifest is simply not read: the root still decides.
        using var process = ToolProcess.Launch(
            [
                (ProjectManifest.PathVariable, Root),
                (DeploymentEnvironment.DatabaseVariable, store),
            ],
            "serve");

        var log = ToolLog.Of(process);

        try
        {
            await log.UntilAsync(
                l => l.Said("http://127.0.0.1:") || process.HasExited, TimeSpan.FromSeconds(20), ct);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(5000);
            }
        }

        Assert.False(
            File.Exists(store),
            $"a store named by PARLEY_DB was opened. The tool said:{Environment.NewLine}{log}");

        Assert.True(
            log.Said("orders"),
            $"the root's own project was not served. The tool said:{Environment.NewLine}{log}");
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
