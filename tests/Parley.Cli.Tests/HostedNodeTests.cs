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
/// projects, and §4.1.1 gives a machine one identity — so a process serving three brokers cannot
/// work out which deployment this machine belongs to, and picking the first would join it to one
/// nobody named.
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
    public void Serving_several_projects_refuses_to_host_a_node_rather_than_choosing_one()
    {
        // The refusal has to name the fix, because the person meeting it has just installed
        // something and is not going to read §4.1.1 to find out why one flag was rejected.
        ProjectManifest.Write(
            ManifestPath,
            [
                Project("orders", Path.Combine(_directory, "a.db"), "http://127.0.0.1:51707"),
                Project("billing", Path.Combine(_directory, "b.db"), "http://127.0.0.1:51708"),
            ]);

        var (code, error) = ToolProcess.Run(
            [(ProjectManifest.PathVariable, ManifestPath)], "serve", "--with-node");

        Assert.Equal(78, code);
        Assert.Contains("--project", error, StringComparison.Ordinal);
        Assert.Contains("--with-node", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Hosting_a_node_never_silently_serves_a_deployment_that_was_not_named()
    {
        // The failure this prevents is not an error message: it is a machine quietly joined to
        // whichever project happened to be listed first, answering for a deployment nobody asked
        // it to join. So the refusal must come before anything is opened -- no store touched, no
        // port bound -- which is what the exit code being a configuration error asserts.
        ProjectManifest.Write(
            ManifestPath,
            [
                Project("orders", Path.Combine(_directory, "a.db"), "http://127.0.0.1:51707"),
                Project("billing", Path.Combine(_directory, "b.db"), "http://127.0.0.1:51708"),
            ]);

        var (code, _) = ToolProcess.Run(
            [(ProjectManifest.PathVariable, ManifestPath)], "serve", "--with-node");

        Assert.Equal(78, code);
        Assert.False(File.Exists(Path.Combine(_directory, "a.db")), "the first project's store was opened");
        Assert.False(File.Exists(Path.Combine(_directory, "b.db")), "the second project's store was opened");
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
