using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Both install scripts register an entry that serves the manifest, whether or not it names a project yet.
/// </summary>
/// <remarks>
/// A reinstall over a machine that names projects used to prompt for a store and register an
/// entry serving that one store, which quietly narrowed a manifest-serving deployment back to a
/// single project. Then the store parameter went altogether: every entry serves the manifest, and
/// a machine that names nothing is waiting for its first project.
/// </remarks>
public sealed class InstallScriptManifestTests
{
    public static TheoryData<string> BothScripts => ["install.ps1", "install.sh"];

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_script_asks_what_is_named_and_serves_the_manifest(string script)
    {
        var text = RepositoryFile.Read(script);

        Assert.Contains("project list", text, StringComparison.Ordinal);
        Assert.Contains("serving every project in", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_words_the_scripts_read_are_the_words_the_command_writes()
    {
        // Lens 3: both scripts parse `parley project list` -- the manifest's path from a
        // "manifest:" line, "reachable" and "not reachable" per project, "No projects
        // configured" for none. That is an interface, and one nothing declared. Named here so
        // that rewording the command's output fails this rather than the next reinstall on
        // somebody else's machine, where the script would fall back to asking for a store and
        // register an entry that narrows the deployment to one project.
        var command = RepositoryFile.Read("src/Parley.Setup/ProjectCommand.cs");

        foreach (var literal in new[] { "manifest: ", "reachable", "not reachable", "No projects configured" })
        {
            Assert.Contains(literal, command, StringComparison.Ordinal);
        }

        foreach (var script in new[] { "install.ps1", "install.sh" })
        {
            var text = RepositoryFile.Read(script);

            Assert.Contains("manifest:", text, StringComparison.Ordinal);
            Assert.Contains("No projects configured", text, StringComparison.Ordinal);
            Assert.Contains("not reachable", text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_script_proves_every_project_answers_rather_than_one_store(string script)
    {
        // The single-store check is `parley registry`, which with several projects and none
        // named refuses rather than answers. The manifest check has to be the one that asks each
        // project's broker.
        var text = RepositoryFile.Read(script);

        Assert.Contains("not reachable", text, StringComparison.Ordinal);
    }
}
