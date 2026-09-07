using System.Diagnostics;
using Parley.Configuration;
using Parley.Core;
using Xunit;
using static Parley.Cli.Tests.Loopback;

namespace Parley.Cli.Tests;

/// <summary>
/// That a role added to a running node's catalogue is announced without restarting anything.
/// </summary>
/// <remarks>
/// <para>
/// The roles a machine can start are sent once, in the join request. Until this existed a role
/// added afterwards was announced never — so an agent enrolled under a running node read as
/// <c>startable</c> to every peer and could not be started by any of them, which is the state
/// SPEC.md §17 question 13 calls misleading, arriving here by a route nobody had noticed.
/// </para>
/// <para>
/// It matters most to the surface: adding an agent from the terminal writes a catalogue entry,
/// and without this the promise that it becomes startable would be false until somebody restarted
/// a process by hand. SC-003 rests on the behaviour checked here.
/// </para>
/// </remarks>
public sealed class NodeCatalogueWatchTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-catalogue-watch-" + Guid.NewGuid().ToString("N"));

    public NodeCatalogueWatchTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort, as elsewhere in this suite.
        }
    }

    private string ManifestPath => Path.Combine(_directory, "projects.json");

    private string CataloguePath => Path.Combine(_directory, "node.json");

    [Fact]
    public async Task A_role_added_to_the_catalogue_is_announced_without_a_restart()
    {
        var ct = TestContext.Current.CancellationToken;

        WriteCatalogue("orders");
        WriteManifest();

        using var process = Serve();
        var log = ToolLog.Of(process);

        try
        {
            Assert.True(
                await log.UntilAsync(l => l.Said("Can start", "orders"), TimeSpan.FromSeconds(40), ct),
                $"the node never announced its first role:{Environment.NewLine}{log}");

            WriteCatalogue("orders", "billing");

            Assert.True(
                await log.UntilAsync(l => l.Said("Rejoining with:"), TimeSpan.FromSeconds(40), ct),
                $"the node did not rejoin after its catalogue changed:{Environment.NewLine}{log}");
        }
        finally
        {
            Stop(process);
        }

        Assert.True(
            log.Said("Rejoining with:", "billing"),
            $"the rejoin did not name the added role:{Environment.NewLine}{log}");

        Assert.True(
            log.Said("Can start", "billing"),
            $"the broker was never told the added role could be started:{Environment.NewLine}{log}");
    }

    [Fact]
    public async Task A_catalogue_rewritten_with_the_same_roles_does_not_rejoin()
    {
        // The probe against the obvious implementation. Watching the file's timestamp rather than
        // its roles rejoins for a reformat, and every rejoin drops the machine's connection for
        // the moment it takes — so anything that rewrites the catalogue on a timer would keep a
        // deployment permanently reconnecting while announcing nothing new.
        var ct = TestContext.Current.CancellationToken;

        WriteCatalogue("orders");
        WriteManifest();

        using var process = Serve();
        var log = ToolLog.Of(process);

        try
        {
            Assert.True(
                await log.UntilAsync(l => l.Said("Can start", "orders"), TimeSpan.FromSeconds(40), ct),
                $"the node never announced its first role:{Environment.NewLine}{log}");

            // Same roles, different bytes: the workspace changed and the whole file rewritten.
            MoveWorkspace("orders", "-moved");

            await log.UntilAsync(l => l.Said("Rejoining with:"), TimeSpan.FromSeconds(8), ct);
        }
        finally
        {
            Stop(process);
        }

        Assert.False(
            log.Said("Rejoining with:"),
            $"the node rejoined for a catalogue whose roles had not changed:{Environment.NewLine}{log}");
    }

    [Fact]
    public async Task A_catalogue_that_cannot_be_read_keeps_the_roles_already_announced()
    {
        // Somebody half-way through saving leaves a moment of invalid JSON. A node that dropped
        // every role because it read during that moment is worse than one that lags a poll: the
        // agents it can start would read as stranded to every peer until the next valid write.
        var ct = TestContext.Current.CancellationToken;

        WriteCatalogue("orders");
        WriteManifest();

        using var process = Serve();
        var log = ToolLog.Of(process);

        try
        {
            Assert.True(
                await log.UntilAsync(l => l.Said("Can start", "orders"), TimeSpan.FromSeconds(40), ct),
                $"the node never announced its first role:{Environment.NewLine}{log}");

            await File.WriteAllTextAsync(CataloguePath, "{ \"agents\": ", ct);

            Assert.True(
                await log.UntilAsync(
                    l => l.Said("could not be read"), TimeSpan.FromSeconds(20), ct),
                $"an unreadable catalogue was never reported:{Environment.NewLine}{log}");
        }
        finally
        {
            Stop(process);
        }

        Assert.True(
            log.Said("Keeping the roles already announced"),
            $"an unreadable catalogue was not reported as kept:{Environment.NewLine}{log}");

        Assert.False(
            log.Said("Rejoining with:"),
            $"an unreadable catalogue caused a rejoin:{Environment.NewLine}{log}");
    }

    private static void Stop(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        process.Kill(entireProcessTree: true);
        _ = process.WaitForExit(5000);
    }

    private Process Serve() =>
        ToolProcess.Launch([(ProjectManifest.PathVariable, ManifestPath)], "serve", "--with-node");

    private void WriteManifest() =>
        ProjectManifest.Write(
            ManifestPath,
            [
                new ProjectSettings(
                    ProjectName.Create("acme"),
                    Path.Combine(_directory, "acme.db"),
                    $"http://127.0.0.1:{FreePort()}",
                    CataloguePath,
                    null),
            ]);

    private void WriteCatalogue(params string[] roles) => WriteCatalogue(roles, string.Empty);

    /// <summary>
    /// Rewrites the catalogue with the same roles and different contents.
    /// </summary>
    /// <param name="role">The role to keep.</param>
    /// <param name="workspaceSuffix">What to append to its workspace, so the bytes differ.</param>
    /// <remarks>
    /// Named rather than overloaded, because it was overloaded and that was a bug: two string
    /// arguments bind to the two-parameter method in preference to the <c>params</c> one, so
    /// <c>WriteCatalogue("orders", "billing")</c> silently meant "keep orders, move its workspace
    /// to billing" and the test waited forty seconds for a rejoin that was correctly never made.
    /// </remarks>
    private void MoveWorkspace(string role, string workspaceSuffix) =>
        WriteCatalogue([role], workspaceSuffix);

    private void WriteCatalogue(string[] roles, string workspaceSuffix)
    {
        var entries = roles.Select(role =>
            $"    \"{role}\": {{ \"command\": \"claude\", \"workspace\": \"{Escaped(role, workspaceSuffix)}\" }}");

        File.WriteAllText(
            CataloguePath,
            "{\n  \"agents\": {\n" + string.Join(",\n", entries) + "\n  }\n}\n");
    }

    private string Escaped(string role, string suffix) =>
        Path.Combine(_directory, role + suffix).Replace("\\", "\\\\", StringComparison.Ordinal);
}
