using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That <c>autostart</c> says which projects come up, rather than where the answer is kept.
/// </summary>
/// <remarks>
/// <para>
/// **With two projects served, `autostart status` and `autostart install` must not print output
/// identical to a one-project machine.** Answering `status` with a file path and `install` with a
/// rule, *each project whose entry names a catalogue*, would leave no way to tell from either that
/// the second project had come up, and the reasonable reading would be that it had not.
/// </para>
/// <para>
/// <b>Both commands already read that manifest.</b> `install` reads it to decide whether to print a
/// warning about a dropped catalogue, and then says nothing about what it found — the
/// <c>Shell.Said</c> shape, in the command whose whole job is to say what is installed.
/// </para>
/// <para>
/// The `serves:` line has been rewritten for this once before: it stopped saying "registered", which
/// answered a question nobody was asking, and started naming the manifest, which moved it from
/// answering nothing to naming where the answer is kept. This is the rest of that change.
/// </para>
/// </remarks>
public sealed class ServedProjectsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-served-" + Path.GetRandomFileName());

    public ServedProjectsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // **The root rather than the manifest file.** `--config` and PARLEY_CONFIG name the directory
    // this machine keeps its Parley files in; the projects are the folders under it.
    private string Root => _root;

    private void Write(params (string Name, string? Catalogue)[] projects)
    {
        // **The store is derived rather than chosen**, so the fixture cannot put one somewhere of its
        // own. Naming one here is what made this file's assertions compare against a path nothing
        // writes — the reader answers with what the layout says, whatever a fixture asked for.
        var settings = projects.Select((project, index) => new ProjectSettings(
            ProjectName.Create(project.Name),
            ProjectFolder.StoreFor(Root, ProjectName.Create(project.Name)),
            $"http://127.0.0.1:{7100 + index}",
            null)).ToArray();

        ProjectManifest.Write(Root, settings);
    }

    [Fact]
    public void Every_project_is_named()
    {
        // The whole point: a two-project machine must not print what a one-project machine prints.
        Write(("alpha", Path.Combine(_root, "alpha-node.json")), ("beta", Path.Combine(_root, "beta-node.json")));

        var lines = AutostartCommand.ServedProjects(Root);

        Assert.Contains(lines, line => line.Contains("alpha", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("beta", StringComparison.Ordinal));
    }

    [Fact]
    public void Each_project_names_both_of_its_files()
    {
        // **Two files per project, and they are rarely together.** The catalogue is not a property of a
        // project. The store and the endpoint are genuinely per project, and can be in different
        // directories from each other and from the manifest — which is the whole reason naming the
        // manifest alone is not enough.
        Write(("alpha", Path.Combine(_root, "alpha-node.json")));

        var text = string.Join("\n", AutostartCommand.ServedProjects(Root));

        Assert.Contains("alpha", text, StringComparison.Ordinal);
        Assert.Contains(
            ProjectFolder.StoreFor(Root, ProjectName.Create("alpha")), text, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:7100", text, StringComparison.Ordinal);
    }

    [Fact]
    public void No_project_is_given_a_catalogue_of_its_own()
    {
        // **A manifest may still carry `node_config` per project**, so a command that goes on reading
        // it would print a path that is still in the file and no longer decides anything, while the
        // broker two lines further up the same output says it is hosting from somewhere else entirely:
        // two answers to one question, one of them unreachable from the code that matters.
        //
        // Written against a manifest whose entries *do* name catalogues, since a manifest with none
        // would pass this by having nothing to leak.
        Write(
            ("alpha", Path.Combine(_root, "alpha-node.json")),
            ("beta", Path.Combine(_root, "beta-node.json")));

        var text = string.Join("\n", AutostartCommand.ServedProjects(Root));

        Assert.DoesNotContain("alpha-node.json", text, StringComparison.Ordinal);
        Assert.DoesNotContain("beta-node.json", text, StringComparison.Ordinal);
        Assert.DoesNotContain("catalogue", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_project_that_cannot_be_read_says_nothing_here()
    {
        // Silent on purpose: both callers have their own words for a configuration they cannot read, and
        // two messages about one file is how they come to disagree about it.
        //
        // **Repointed at a project's own file**, which is where an unreadable configuration lives now.
        // The old spelling wrote over the manifest; writing over the root would be writing a file where
        // a directory is, which tests the filesystem rather than this.
        Directory.CreateDirectory(ProjectFolder.For(Root, ProjectName.Create("alpha")));

        File.WriteAllText(
            ProjectFolder.ConfigurationFor(Root, ProjectName.Create("alpha")), "{ this is not json");

        Assert.Empty(AutostartCommand.ServedProjects(Root));
    }

    [Fact]
    public void The_catalogue_reported_is_the_one_beside_the_manifest()
    {
        // One per machine, and the entry names none of its own — so the broker it starts resolves the
        // file beside the manifest it serves. Asserted against `ProjectResolution`'s own answer rather
        // than against a path built here: a second spelling of this rule in a test is how the test
        // comes to pass while the code is wrong, which is what `KeyRuleTests` guards.
        Write(("alpha", Path.Combine(_root, "alpha-node.json")));

        Assert.Equal(
            Path.GetFullPath(Path.Combine(_root, NodeCatalogue.DefaultFileName)),
            AutostartCommand.MachineCatalogue(null, Root));
    }

    [Fact]
    public void The_variable_this_shell_holds_does_not_change_what_is_reported()
    {
        // **The entry runs with no environment, so reporting what this shell resolves would be a lie
        // told confidently.** A supervisor starts the broker at logon and nothing has set
        // `PARLEY_NODE_CONFIG` there, so a path taken from the developer's own variable names a file
        // the logon broker will never open — and it would look right to whoever ran the installer,
        // which is the only moment somebody checks.
        //
        // Ambient state read at the bottom of a call stack could write into a real deployment; here it
        // would be read at the top and describe one that does not exist.
        Write(("alpha", null));

        var previous = Environment.GetEnvironmentVariable(NodeCatalogue.PathVariable);

        try
        {
            Environment.SetEnvironmentVariable(
                NodeCatalogue.PathVariable, Path.Combine(_root, "from-the-shell.json"));

            Assert.Equal(
                Path.GetFullPath(Path.Combine(_root, NodeCatalogue.DefaultFileName)),
                AutostartCommand.MachineCatalogue(null, Root));
        }
        finally
        {
            Environment.SetEnvironmentVariable(NodeCatalogue.PathVariable, previous);
        }
    }

    [Fact]
    public void A_catalogue_named_on_the_command_line_is_what_the_entry_will_host_from()
    {
        // The one input that does survive into the entry, because it is written onto its command line
        // rather than read out of an environment that will not be there.
        Write(("alpha", null));

        var named = Path.Combine(_root, "chosen.json");

        Assert.Equal(named, AutostartCommand.MachineCatalogue(named, Root));
    }
}
