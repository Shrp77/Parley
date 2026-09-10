using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That <c>autostart</c> says which projects come up, rather than where the answer is kept.
/// </summary>
/// <remarks>
/// <para>
/// **Found by the owner on a live deployment, immediately after 0.22.0 was merged.** Two projects were
/// being served — the broker log named both, both ports were listening, and the node could start all
/// three of the second project's agents — and both `autostart status` and `autostart install` printed
/// output identical to a one-project machine. `status` answered with a file path and `install` with a
/// rule, *each project whose entry names a catalogue*, so there was no way to tell from either that
/// the second project had come up. The reasonable reading was that it had not.
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

    private string ManifestPath => Path.Combine(_root, "projects.json");

    private void Write(params (string Name, string? Catalogue)[] projects)
    {
        var settings = projects.Select((project, index) => new ProjectSettings(
            ProjectName.Create(project.Name),
            Path.Combine(_root, $"{project.Name}.db"),
            $"http://127.0.0.1:{7100 + index}",
            project.Catalogue,
            null)).ToArray();

        ProjectManifest.Write(ManifestPath, settings);
    }

    [Fact]
    public void Every_project_is_named()
    {
        // The whole point: a two-project machine must not print what a one-project machine prints.
        Write(("alpha", Path.Combine(_root, "alpha-node.json")), ("beta", Path.Combine(_root, "beta-node.json")));

        var lines = AutostartCommand.ServedProjects(ManifestPath);

        Assert.Contains(lines, line => line.Contains("alpha", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("beta", StringComparison.Ordinal));
    }

    [Fact]
    public void Each_project_names_all_three_of_its_files()
    {
        // **A deployment is three files per project and they are rarely together.** The manifest is
        // the only one the entry itself names; the store and the catalogue live inside it, and on the
        // machine that found this they were in different directories from each other and from the
        // manifest. A path somebody has to go and look up is a path they cannot check against what
        // they meant.
        var catalogue = Path.Combine(_root, "alpha-node.json");

        Write(("alpha", catalogue));

        var text = string.Join("\n", AutostartCommand.ServedProjects(ManifestPath));

        Assert.Contains("alpha", text, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(_root, "alpha.db"), text, StringComparison.Ordinal);
        Assert.Contains(catalogue, text, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:7100", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_project_that_starts_agents_is_told_apart_from_one_that_does_not()
    {
        // The distinction the `hosts:` rule states and never applies. A project with no catalogue is
        // served — its broker comes up and answers — and starts nothing here, which is a different
        // fact from not being served at all, and the one that reads as a fault when it is not.
        Write(("alpha", Path.Combine(_root, "alpha-node.json")), ("beta", null));

        var lines = AutostartCommand.ServedProjects(ManifestPath);

        var alpha = Assert.Single(lines, line => line.Contains("alpha-node.json", StringComparison.Ordinal));
        var beta = Assert.Single(lines, line => line.Contains("starts no agents", StringComparison.Ordinal));

        Assert.Contains("alpha-node.json", alpha, StringComparison.Ordinal);
        Assert.Contains("starts no agents for it", beta, StringComparison.Ordinal);
    }

    [Fact]
    public void A_manifest_that_cannot_be_read_says_nothing_here()
    {
        // Silent on purpose: both callers have their own words for an unreadable manifest, and two
        // messages about one file is how they come to disagree about it.
        File.WriteAllText(ManifestPath, "{ this is not json");

        Assert.Empty(AutostartCommand.ServedProjects(ManifestPath));
    }

    [Fact]
    public void The_catalogue_named_is_the_one_the_project_names()
    {
        // Not a guess at a default. A project's catalogue is wherever its manifest entry says, and on
        // the deployment that found this the two projects' catalogues were in different directories
        // entirely — which is precisely why naming the manifest was not enough.
        var elsewhere = Path.Combine(_root, "somewhere", "else.json");

        Write(("alpha", elsewhere));

        Assert.Contains(
            elsewhere,
            string.Join("\n", AutostartCommand.ServedProjects(ManifestPath)),
            StringComparison.Ordinal);
    }
}
