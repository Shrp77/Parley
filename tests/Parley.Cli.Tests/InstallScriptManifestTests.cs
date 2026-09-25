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
        // Lens 3: both scripts parse `parley project list` -- the root's path from a labelled line,
        // whether each project answered, and "No projects configured" for none. That is an
        // interface, and one nothing declared. Named here so that rewording the command's output
        // fails this rather than the next reinstall on somebody else's machine.
        //
        // **It was passing on a comment.** It read the whole of `ProjectCommand.cs` for
        // "manifest: ", which appears in a remark forty lines below the listing about something
        // else entirely -- while the listing itself printed `projects:` and the scripts matched
        // `^manifest:`. So the scrape returned nothing on every reinstall, the line naming what was
        // about to be served never printed, and the guard declaring the interface said it was fine.
        // A check answered by a comment is a check that has not run.
        //
        // Executable lines only now, on both sides.
        var command = Executable(RepositoryFile.Read("src/Parley.Setup/ProjectCommand.cs"));

        foreach (var literal in new[] { "root:", "answering", "not answering", "No projects configured" })
        {
            Assert.Contains(literal, command, StringComparison.Ordinal);
        }

        foreach (var script in new[] { "install.ps1", "install.sh" })
        {
            var text = Executable(RepositoryFile.Read(script));

            Assert.Contains("root:", text, StringComparison.Ordinal);
            Assert.Contains("No projects configured", text, StringComparison.Ordinal);
            Assert.Contains("not answering", text, StringComparison.Ordinal);
        }
    }

    /// <summary>A file without its comment lines, so a guard cannot be answered by prose.</summary>
    /// <param name="text">The file.</param>
    /// <returns>Its executable lines, joined.</returns>
    /// <remarks>
    /// Covers <c>//</c> and <c>#</c>, because this reads one C# file and two shell scripts. The
    /// PowerShell block comment is not stripped: nothing in this check appears inside one, and a
    /// half-right stripper would be worse than an honest line filter.
    /// </remarks>
    private static string Executable(string text) =>
        string.Join(
            '\n',
            text.Split('\n').Where(line =>
            {
                var trimmed = line.TrimStart();

                return !trimmed.StartsWith("//", StringComparison.Ordinal)
                    && !trimmed.StartsWith('#')
                    && !trimmed.StartsWith("///", StringComparison.Ordinal);
            }));

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void Each_script_proves_every_project_answers_rather_than_one_store(string script)
    {
        // The single-store check is `parley registry`, which with several projects and none
        // named refuses rather than answers. The readiness check has to be the one that asks each
        // project's broker — which is `project list`, whose per-project verdict is `answering` or
        // `not answering` since FR-023 split it from whether anything can address the endpoint.
        var text = Executable(RepositoryFile.Read(script));

        Assert.Contains("not answering", text, StringComparison.Ordinal);
    }
}
