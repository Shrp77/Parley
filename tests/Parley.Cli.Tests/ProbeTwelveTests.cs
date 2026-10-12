using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Review gate, lens 2, run 1: what a person can put into the values this feature writes into
/// somebody's repository.
/// </summary>
/// <remarks>
/// <para>
/// Every path here edits a file inside a checkout, and three of the values written into it come from
/// outside: the project name, the workspace path, and the <c>owns</c> line. The launch configuration
/// is not only JSON — <c>${VAR:-default}</c> is expanded by the client that reads it, so a value that
/// is safe as JSON can still be unsafe as configuration.
/// </para>
/// <para>
/// The other half is what this feature prints. A refusal quotes a catalogue's path and a role taken
/// from a file, and refusals are read in terminals, so a value able to move the cursor can imitate the
/// lines around it. This repository has fixed that shape twice before — in the transcript's
/// column-zero rule and in the destroy dialog quoting a manifest raw.
/// </para>
/// </remarks>
public sealed class ProbeTwelveTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe12-" + Path.GetRandomFileName());

    public ProbeTwelveTests() => Directory.CreateDirectory(_root);

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

    private static EnrollmentEntry Entry(string workspace, string owns = "Carts and totals.") =>
        new(workspace, "orders", owns, Unattended: false) { Enroll = true };

    [Theory]
    [InlineData("/src/orders}")]
    [InlineData("/src/or}ders")]
    [InlineData("/src/orders}\"")]
    [InlineData("/src/${OTHER}")]
    public void A_workspace_that_could_close_the_expansion_early_does_not(string workspace)
    {
        // **JSON-safe is not configuration-safe.** The workspace is written as
        // `${PARLEY_WORKSPACE:-<path>}`, and the client expands that. A path holding `}` closes the
        // expansion early, so everything after it becomes literal text in the value the agent's
        // server is launched with — and a path is not a value this tooling chooses, it is a directory
        // somebody has on disk.
        var plan = EnrollmentPlan.Describe(Entry(workspace), null, null, false, project: "alpha");

        var written = JsonNode.Parse(plan.LaunchConfiguration!)!
            ["mcpServers"]!["parley-alpha"]!["env"]!["PARLEY_WORKSPACE"]!.GetValue<string>();

        // Two shapes are acceptable and no third is: the value is either the path plainly, or one
        // whole expansion around it. What must never happen is an expansion that closes in the middle
        // and leaves the rest as literal text — the agent would then be launched with a workspace
        // that is a prefix of the real one, silently.
        var slashed = workspace.Replace('\\', '/');

        if (string.Equals(written, slashed, StringComparison.Ordinal))
        {
            return;
        }

        Assert.Equal("${PARLEY_WORKSPACE:-" + slashed + "}", written);
        Assert.DoesNotContain('}', slashed);
        Assert.DoesNotContain('$', slashed);
    }

    [Fact]
    public void A_project_name_from_a_hand_edited_manifest_cannot_shape_the_key()
    {
        // The key is built from a project name. If a hand edit could carry a name the grammar refuses,
        // that name would reach a JSON key — and a key is one of the few places JSON escaping is not
        // the whole story, because the client uses it to namespace tool names.
        //
        // **019 moved where such a name would have to come from, and narrowed it.** It used to be a
        // `name` field in a single manifest, which is a string somebody types. A project's name is its
        // *directory entry* now, so a hand edit has to create a folder — and a folder cannot hold a
        // quote on Windows or a separator anywhere, which removes most of the attack before the reader
        // sees it. What is left is a name the grammar refuses but a filesystem allows.
        foreach (var hostile in new[] { "alpha two", "ALPHA", "9lives", "-leading", "a.b", "évil" })
        {
            var folder = Path.Combine(_root, ProjectFolder.ProjectsDirectoryName, hostile);

            Directory.CreateDirectory(folder);

            File.WriteAllText(
                Path.Combine(folder, ProjectFolder.ConfigurationFileName),
                """{ "endpoint": "http://127.0.0.1:51707" }""");
        }

        var read = ProjectManifest.TryRead(_root, out var projects, out var problem);

        // Either refused outright, or read as something the grammar accepts. What must not happen is a
        // project whose name is not a project name.
        Assert.True(
            !read || projects.All(p => ProjectName.IsValid(p.Name.Value)),
            $"a folder yielded a name the grammar refuses (problem: {problem})");

        // And the refusal names them rather than passing over them, which is FR-027: a folder somebody
        // made in the one directory whose contents are the answer is somebody's mistake, and silence
        // there is how a project goes missing.
        Assert.True(projects.Count == 0 && problem is { Length: > 0 }, "a folder named something the grammar refuses was read as a project, or passed over in silence");
        Assert.Contains("not a project name", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_key_this_writes_is_one_the_grammar_would_accept_back()
    {
        // The round trip, as a property rather than a case list: whatever a key is built from, it must
        // be a key this tooling recognises as its own — otherwise it has written an entry it can never
        // find, update or withdraw again.
        foreach (var name in new[] { "a", "alpha", "alpha-two", "parley", "a1", "z9-x" })
        {
            var key = LaunchKey.For(name);

            Assert.True(LaunchKey.IsOurs(key), $"'{key}' is not a key this tooling owns");
            Assert.Equal(name, LaunchKey.ProjectOf(key));
        }
    }

    [Theory]
    [InlineData("[2J")]
    [InlineData("a\rb")]
    [InlineData("a\nb")]
    [InlineData("‮")]
    public void A_refusal_quoting_a_catalogue_carries_no_control_characters(string mischief)
    {
        // A refusal quotes the path a catalogue records and the one this run was offered — both read out
        // of a file, and refusals are read in terminals. This repository has fixed this shape twice: the
        // transcript's column-zero rule, and the destroy dialog quoting a manifest's endpoint raw.
        //
        // The sentence this probes is 018's copied-repository refusal. It replaced FR-013's, which is
        // where this probe was pointed when it was written; the values are read from the same file and
        // printed in the same place, so what it is asking is unchanged.
        var refusal = Catalogues.Copied($"/tmp/orders{mischief}", $"/tmp/copy{mischief}");

        foreach (var character in refusal)
        {
            Assert.False(
                char.IsControl(character) && character != '\n',
                $"a refusal carried U+{(int)character:X4}: {refusal}");
        }

        Assert.DoesNotContain('‮', refusal);
    }

    [Fact]
    public void An_owns_line_that_could_break_its_roster_row_is_still_refused_per_entry()
    {
        // Not new, and worth one assertion here because this feature multiplied the number of
        // entries an `owns` line can be written into: what was one line per repository is now one
        // per project. A rule applied to the first entry and not to the rest is the shape this
        // probe exists to catch.
        //
        // **It used to assert a length, and 0.33.0 removed the length.** Left as it was it would
        // have gone on passing against nothing -- 500 characters is now perfectly legal -- which is
        // worse than deleting it, because a green probe reads as a checked rule. What still refuses
        // is the character rule, so that is what it drives.
        var problems = EnrollmentPlan.Validate(
            [Entry("/src/orders", "Carts." + (char)0x2028 + "No tax.")],
            _ => true);

        Assert.NotEmpty(problems);

        // And the length half really is gone, which is the other half of the same statement.
        Assert.Empty(EnrollmentPlan.Validate([Entry("/src/orders", new string('x', 500))], _ => true));
    }
}
