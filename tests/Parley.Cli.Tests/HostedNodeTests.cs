using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That one supervisor entry can bring a machine all the way up, and refuses when it cannot.
/// </summary>
/// <remarks>
/// <para>
/// A deployment is a broker and this machine's node. They were two processes needing two things
/// to start them, and on the platform where that is a scheduled task it was two entries — one
/// installed by <c>autostart</c> and one nobody had written, so the node was started by hand or
/// not at all. A machine whose broker is up and whose node is not looks entirely healthy from
/// the outside: every agent on it reads as <c>startable</c> (SPEC.md §14), and questions to them
/// wait out their window and are ghosted.
/// </para>
/// <para>
/// <b>Hosting is opt-in and refused rather than guessed.</b> §4.1 lets one process serve many
/// projects and names a catalogue per project, so a process serving three brokers hosts a node in
/// each project whose entry names one. What it will not do is take one catalogue from the command
/// line and pick a project for it: that would join this machine to whichever deployment happened
/// to be listed first, which is one nobody named.
/// </para>
/// </remarks>
public sealed class HostedNodeTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-hosted-" + Guid.NewGuid().ToString("N"));

    public HostedNodeTests() => Directory.CreateDirectory(_directory);

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

    private string ManifestPath => Path.Combine(_directory, "projects.json");

    private static ProjectSettings Project(string name, string store, string endpoint) =>
        new(ProjectName.Create(name), store, endpoint, null, null);

    [Fact]
    public void One_catalogue_offered_to_several_projects_is_refused_rather_than_placed()
    {
        // The refusal has to name the fix, because the person meeting it has just installed
        // something and is not going to read §4.1 to find out why one flag was rejected. And it
        // must come before anything is opened -- no store touched, no port bound -- because the
        // failure this prevents is not an error message: it is a machine quietly joined to
        // whichever project happened to be listed first. The ports are ones nothing listens on,
        // so a refusal that came too late would show up as a bound port rather than pass by luck.
        ProjectManifest.Write(
            ManifestPath,
            [
                Project("orders", Path.Combine(_directory, "a.db"), "http://127.0.0.1:1"),
                Project("billing", Path.Combine(_directory, "b.db"), "http://127.0.0.1:2"),
            ]);

        var catalogue = Path.Combine(_directory, "node.json");

        var (code, error) = ToolProcess.Run(
            [(ProjectManifest.PathVariable, ManifestPath)],
            "serve", "--with-node", "--node-config", catalogue);

        Assert.Equal(78, code);
        Assert.Contains("--node-config", error, StringComparison.Ordinal);
        Assert.Contains("--project", error, StringComparison.Ordinal);
        Assert.Contains("manifest entry", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_directory, "a.db")), "the first project's store was opened");
        Assert.False(File.Exists(Path.Combine(_directory, "b.db")), "the second project's store was opened");
    }

    [Fact]
    public async Task Serving_several_projects_hosts_a_node_where_a_catalogue_is_named()
    {
        // The rule the refusal above is the other half of: with several projects, each one whose
        // entry names a catalogue gets this machine's node and each one that does not is told so,
        // by name. Read from the real tool's output, because the placement lives in the entry
        // point and not in the command the in-process test drives. The catalogue file does not
        // exist, and need not: what is asserted is where the node was placed, and a node that
        // then stops for want of its file is contained and says so on the next line.
        var ct = TestContext.Current.CancellationToken;

        ProjectManifest.Write(
            ManifestPath,
            [
                new ProjectSettings(
                    ProjectName.Create("orders"),
                    Path.Combine(_directory, "a.db"),
                    $"http://127.0.0.1:{FreePort()}",
                    Path.Combine(_directory, "node.json"),
                    null),
                Project("billing", Path.Combine(_directory, "b.db"), $"http://127.0.0.1:{FreePort()}"),
            ]);

        using var process = ToolProcess.Launch(
            [(ProjectManifest.PathVariable, ManifestPath)], "serve", "--with-node");

        var seen = new List<string>();

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);

            while (DateTime.UtcNow < deadline && !(Placed(seen) && Declined(seen)))
            {
                var line = await process.StandardOutput.ReadLineAsync(ct).AsTask()
                    .WaitAsync(deadline - DateTime.UtcNow, ct);

                if (line is null)
                {
                    break;
                }

                seen.Add(line);
            }
        }
        catch (TimeoutException)
        {
            // Asserted on below, with everything that was read.
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(5000);
            }
        }

        var said = string.Join(Environment.NewLine, seen);

        Assert.True(Placed(seen), $"orders was not offered the node. The tool said:{Environment.NewLine}{said}");
        Assert.True(Declined(seen), $"billing was not told it names no catalogue. The tool said:{Environment.NewLine}{said}");

        static bool Placed(List<string> lines) =>
            lines.Exists(l => l.Contains("orders: hosting this machine's node", StringComparison.Ordinal));

        static bool Declined(List<string> lines) =>
            lines.Exists(l => l.Contains("billing: names no catalogue", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_lone_projects_environment_catalogue_survives_the_watcher_restarting_it()
    {
        // Lens 1 probe. A machine with one project and its catalogue in PARLEY_NODE_CONFIG --
        // the documented resolution for one project, and this machine's own arrangement --
        // hosts its node from the variable at start. The watcher then restarts the project on a
        // changed entry. If "the one project" is recognised by object identity, the restarted
        // one is a different record, falls to the per-entry rule, names no catalogue, and the
        // machine silently stops starting agents until the next logon.
        var ct = TestContext.Current.CancellationToken;

        var catalogue = Path.Combine(_directory, "env-node.json");
        await File.WriteAllTextAsync(catalogue, """{"agents":{}}""", ct);

        var before = Project("orders", Path.Combine(_directory, "a.db"), $"http://127.0.0.1:{FreePort()}");
        var after = Project("orders", Path.Combine(_directory, "a.db"), $"http://127.0.0.1:{FreePort()}");

        ProjectManifest.Write(ManifestPath, [before]);

        using var process = ToolProcess.Launch(
            [(ProjectManifest.PathVariable, ManifestPath), ("PARLEY_NODE_CONFIG", catalogue)],
            "serve", "--with-node");

        var seen = new List<string>();

        try
        {
            await ReadUntilAsync(process, seen, lines => Hosted(lines) >= 1, TimeSpan.FromSeconds(20), ct);

            Assert.True(Hosted(seen) == 1, $"the node was not hosted from the variable at start:{Environment.NewLine}{string.Join(Environment.NewLine, seen)}");

            ProjectManifest.Write(ManifestPath, [after]);

            await ReadUntilAsync(process, seen, lines => Hosted(lines) >= 2 || Declined(lines), TimeSpan.FromSeconds(20), ct);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(5000);
            }
        }

        var said = string.Join(Environment.NewLine, seen);

        Assert.False(Declined(seen), $"after the restart the lone project was told it names no catalogue:{Environment.NewLine}{said}");
        Assert.True(Hosted(seen) == 2, $"the node was not hosted again after the restart:{Environment.NewLine}{said}");

        static int Hosted(List<string> lines) =>
            lines.Count(l => l.Contains("Hosting this machine's node", StringComparison.Ordinal));

        static bool Declined(List<string> lines) =>
            lines.Exists(l => l.Contains("names no catalogue", StringComparison.Ordinal));
    }

    private static async Task ReadUntilAsync(
        System.Diagnostics.Process process,
        List<string> seen,
        Func<List<string>, bool> done,
        TimeSpan within,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + within;

        try
        {
            while (DateTime.UtcNow < deadline && !done(seen))
            {
                var line = await process.StandardOutput.ReadLineAsync(ct).AsTask()
                    .WaitAsync(deadline - DateTime.UtcNow, ct);

                if (line is null)
                {
                    break;
                }

                seen.Add(line);
            }
        }
        catch (TimeoutException)
        {
            // Asserted on by the caller, with everything that was read.
        }
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }

    [Theory]
    [InlineData("registry")]
    [InlineData("up")]
    [InlineData("prune")]
    public void A_verb_that_cannot_host_a_node_refuses_the_flag_rather_than_ignoring_it(string verb)
    {
        // Options are taken above the verb, so the entry point's refusal of unknown flags cannot
        // see one that was legitimately taken for a different command: every verb accepts every
        // option. `parley up --with-node` is the case that matters -- the flag names hosting a
        // node and `up` is the command that runs one -- and it used to do precisely what typing
        // nothing does, exit zero, and say nothing.
        var (code, error) = ToolProcess.Run(
            [(ProjectManifest.PathVariable, ManifestPath)], verb, "--with-node");

        Assert.Equal(64, code);
        Assert.Contains("--with-node", error, StringComparison.Ordinal);
        Assert.Contains("serve", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_node_is_told_which_catalogue_to_read_rather_than_finding_one()
    {
        // The defect this file's feature was found by, guarded at the source because the
        // behaviour needs a broker and a logon to reproduce.
        //
        // `--node-config` is taken centrally, like every other option. It was honoured by
        // `enroll` and ignored by the node, which called a reader that consulted
        // PARLEY_NODE_CONFIG itself and fell back to the working directory. Both resolutions were
        // defensible; having two is the defect, and it surfaced under a supervisor -- no shell
        // environment, no working directory worth relying on -- as a machine that joined
        // successfully and could start nothing.
        var node = RepositoryFile.Read("src/Parley.Cli/NodeCommand.cs");

        Assert.DoesNotContain("GetEnvironmentVariable", node, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadFromConfiguredPath", node, StringComparison.Ordinal);

        // And is handed one: a node that resolved nothing and was given nothing would read the
        // same either way.
        Assert.Contains("NodeCatalogue.ReadFrom(catalogueFile)", node, StringComparison.Ordinal);
    }

    [Fact]
    public void A_machine_that_can_start_nothing_says_which_file_it_read()
    {
        // An absent catalogue and an empty one are both legitimate and produce the same word.
        // Without the path, the one failure that cannot otherwise be told apart -- a supervisor
        // started somewhere the file is not -- reads exactly like a machine nobody has listed any
        // agents on yet, which is the state most machines are in and nobody investigates.
        var node = RepositoryFile.Read("src/Parley.Cli/NodeCommand.cs");

        Assert.Contains("no agents in {catalogueFile}", node, StringComparison.Ordinal);
    }
}
