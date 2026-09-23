using Parley.Configuration;
using Parley.Core;
using Xunit;
using static Parley.Cli.Tests.Loopback;

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
        new(ProjectName.Create(name), store, endpoint, null);

    [Fact]
    public async Task One_catalogue_named_for_several_projects_is_the_one_they_all_read()
    {
        // **This asserted a refusal until 018, and the refusal's reason is what went.** A catalogue
        // belonged to a project, so one named on the command line beside several projects had nowhere
        // to go — and admitting it would have joined the machine to whichever project happened to be
        // listed first, which is why the check ran before any store was opened or any port bound.
        //
        // There is one catalogue per machine now and `--node-config` names exactly that, so it applies
        // to every project this process serves and there is nothing left to disambiguate. What the old
        // test protected is now unrepresentable rather than guarded.
        ProjectManifest.Write(
            ManifestPath,
            [
                Project("orders", Path.Combine(_directory, "a.db"), $"http://127.0.0.1:{FreePort()}"),
                Project("billing", Path.Combine(_directory, "b.db"), $"http://127.0.0.1:{FreePort()}"),
            ]);

        var catalogue = Path.Combine(_directory, "given-node.json");

        await File.WriteAllTextAsync(
            catalogue, """{"agents":{}}""", TestContext.Current.CancellationToken);

        using var process = ToolProcess.Launch(
            [(ProjectManifest.PathVariable, ManifestPath)],
            "serve", "--with-node", "--node-config", catalogue);

        var log = ToolLog.Of(process);

        try
        {
            await log.UntilAsync(
                l => Hosted(l, "orders") && Hosted(l, "billing"),
                TimeSpan.FromSeconds(20),
                TestContext.Current.CancellationToken);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(5000);
            }
        }

        Assert.True(
            Hosted(log, "orders") && Hosted(log, "billing"),
            $"both projects should host this machine's node from the one catalogue:{Environment.NewLine}{log}");

        // And from the file that was named, rather than from anywhere either project points at.
        Assert.True(
            log.Said(catalogue),
            $"the node was not hosted from the catalogue that was named:{Environment.NewLine}{log}");

        static bool Hosted(ToolLog log, string project) =>
            log.Said($"{project}: hosting this machine's node");
    }

    [Fact]
    public async Task Every_project_served_hosts_a_node_from_the_one_catalogue()
    {
        // **This asserted the opposite until 018: one project offered the node and the other told, by
        // name, that it named no catalogue.** That was the per-project binding working exactly as
        // designed — and it is the defect, not a symptom of one: a checkout is addressable in every
        // project that names it and was startable only in the one whose file happened to hold it.
        //
        // Read from the real tool's output, because the placement lives in the entry point and not in
        // the command an in-process test drives.
        var ct = TestContext.Current.CancellationToken;

        ProjectManifest.Write(
            ManifestPath,
            [
                Project("orders", Path.Combine(_directory, "a.db"), $"http://127.0.0.1:{FreePort()}"),
                Project("billing", Path.Combine(_directory, "b.db"), $"http://127.0.0.1:{FreePort()}"),
            ]);

        using var process = ToolProcess.Launch(
            [(ProjectManifest.PathVariable, ManifestPath)], "serve", "--with-node");

        var log = ToolLog.Of(process);

        try
        {
            await log.UntilAsync(l => Placed(l, "orders") && Placed(l, "billing"), TimeSpan.FromSeconds(20), ct);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(5000);
            }
        }

        Assert.True(Placed(log, "orders"), $"orders was not offered the node. The tool said:{Environment.NewLine}{log}");
        Assert.True(Placed(log, "billing"), $"billing was not offered the node. The tool said:{Environment.NewLine}{log}");

        // Nothing is told it names no catalogue, because nothing names one: the file belongs to the
        // machine and every project served reads it.
        Assert.False(
            log.Said("names no catalogue"),
            $"a project was told it names no catalogue:{Environment.NewLine}{log}");

        static bool Placed(ToolLog log, string project) =>
            log.Said($"{project}: hosting this machine's node");
    }

    [Fact]
    public async Task The_environment_catalogue_survives_the_watcher_restarting_a_project()
    {
        // Lens 1 probe, kept through 018 because the failure it guards is still reachable. A machine
        // whose catalogue is in PARLEY_NODE_CONFIG hosts its node from the variable at start; the
        // watcher then restarts the project on a changed entry, and the restarted project must read
        // the same file.
        //
        // **What made this fragile was a special case that is now gone.** One project served alone
        // resolved its catalogue differently from several, so the machine had to recognise "the lone
        // project" — by name rather than by identity, because the watcher restarts a project as a new
        // record, and recognising it by identity made the restarted one fall to the per-entry rule,
        // name no catalogue, and silently stop starting agents until the next logon. One catalogue per
        // machine is resolved the same way however many projects are served, so neither the count nor
        // the restart can change the answer.
        var ct = TestContext.Current.CancellationToken;

        var catalogue = Path.Combine(_directory, "env-node.json");
        await File.WriteAllTextAsync(catalogue, """{"agents":{}}""", ct);

        var before = Project("orders", Path.Combine(_directory, "a.db"), $"http://127.0.0.1:{FreePort()}");
        var after = Project("orders", Path.Combine(_directory, "a.db"), $"http://127.0.0.1:{FreePort()}");

        ProjectManifest.Write(ManifestPath, [before]);

        using var process = ToolProcess.Launch(
            [(ProjectManifest.PathVariable, ManifestPath), ("PARLEY_NODE_CONFIG", catalogue)],
            "serve", "--with-node");

        var log = ToolLog.Of(process);

        try
        {
            await log.UntilAsync(l => Hosted(l) >= 1, TimeSpan.FromSeconds(20), ct);

            Assert.True(Hosted(log) == 1, $"the node was not hosted from the variable at start:{Environment.NewLine}{log}");

            ProjectManifest.Write(ManifestPath, [after]);

            await log.UntilAsync(l => Hosted(l) >= 2 || Declined(l), TimeSpan.FromSeconds(20), ct);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(5000);
            }
        }

        Assert.False(Declined(log), $"after the restart the lone project was told it names no catalogue:{Environment.NewLine}{log}");
        Assert.True(Hosted(log) == 2, $"the node was not hosted again after the restart:{Environment.NewLine}{log}");

        // Matched case-insensitively because the two spellings are two call sites: a node hosted for a
        // named project is prefixed with that name and opens lower case, and one hosted for a machine
        // serving no project opens the sentence. 018 made every project take the first, and a matcher
        // pinned to the second counted nothing while the node was hosted perfectly well.
        static int Hosted(ToolLog log) =>
            log.Lines().Count(l => l.Contains("osting this machine's node", StringComparison.Ordinal));

        static bool Declined(ToolLog log) => log.Said("names no catalogue");
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
