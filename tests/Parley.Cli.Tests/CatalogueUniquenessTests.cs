using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// One workspace, many launch entries, at most one unattended start.
/// </summary>
/// <remarks>
/// <para>
/// <b>The asymmetry is the feature's one deliberate limit, and the two halves are not the same
/// risk.</b> A launch entry does nothing at all until a person opens the repository in an editor, and
/// a person opens one. A catalogue entry is standing consent for a machine to start a turn with
/// nobody watching, and <c>ResidentAgent</c> starts one with its workspace as the working directory —
/// so two projects both consenting is two models editing one checkout, with no coordination and
/// nobody there to notice.
/// </para>
/// <para>
/// <b>The comparison is the one SPEC.md §4.1.1 defines</b>, which is textual and forbidden from
/// consulting a filesystem. That is why the spelling cases below matter: a repository catalogued as
/// <c>C:/src/x</c> and offered as <c>C:\src\x\</c> is the same repository, and a check that missed it
/// would be a check that passed on exactly the machine it was written for.
/// </para>
/// </remarks>
public sealed class CatalogueUniquenessTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-catalogues-" + Path.GetRandomFileName());

    public CatalogueUniquenessTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a green run over.
        }
    }

    private string Workspace => Path.Combine(_root, "inventory").Replace('\\', '/');

    private string CatalogueFor(string project, string? workspace)
    {
        var path = Path.Combine(_root, $"{project}-node.json");

        File.WriteAllText(
            path,
            workspace is null
                ? """{ "agents": {} }"""
                : $$"""
                    { "agents": { "inventory": { "command": "claude", "workspace": "{{workspace}}" } } }
                    """);

        return path;
    }

    private ProjectSettings Project(string name, string? catalogue) =>
        new(ProjectName.Create(name), Path.Combine(_root, $"{name}.db"), "127.0.0.1:51707", catalogue, null);

    [Fact]
    public void A_workspace_catalogued_in_another_project_is_found()
    {
        var alpha = CatalogueFor("alpha", Workspace);

        var found = Catalogues.Elsewhere(
            [Project("alpha", alpha), Project("beta", CatalogueFor("beta", null))],
            Workspace,
            CatalogueFor("beta", null),
            out _);

        Assert.NotNull(found);
        Assert.Equal("alpha", found.Project);
        Assert.Equal(alpha, found.Catalogue);
        Assert.Equal("inventory", found.Role);
    }

    [Fact]
    public void The_refusal_names_both_projects_worth_of_detail()
    {
        var alpha = CatalogueFor("alpha", Workspace);

        var found = Catalogues.Elsewhere([Project("alpha", alpha)], Workspace, null, out _)!;
        var refusal = Catalogues.Refusal(found);

        Assert.Contains("alpha", refusal, StringComparison.Ordinal);
        Assert.Contains(alpha, refusal, StringComparison.Ordinal);
        Assert.Contains("inventory", refusal, StringComparison.Ordinal);
        Assert.Contains("unattended by only one", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void The_catalogue_being_written_is_not_consulted_against_itself()
    {
        // Re-enrolling a repository in the project that already starts it is not a second consent,
        // and refusing it would refuse every repair.
        var alpha = CatalogueFor("alpha", Workspace);

        Assert.Null(Catalogues.Elsewhere([Project("alpha", alpha)], Workspace, alpha, out var unreadable));
        Assert.Null(unreadable);
    }

    [Fact]
    public void The_same_repository_spelled_differently_is_still_the_same_repository()
    {
        // SPEC.md §4.1.1's comparison rather than a second one written here, which would disagree
        // first about exactly this case.
        var alpha = CatalogueFor("alpha", Workspace);

        Assert.NotNull(Catalogues.Elsewhere(
            [Project("alpha", alpha)],
            Workspace.Replace('/', '\\') + "\\",
            null,
            out _));
    }

    [Fact]
    public void A_different_repository_is_not_a_conflict()
    {
        var alpha = CatalogueFor("alpha", Path.Combine(_root, "orders").Replace('\\', '/'));

        Assert.Null(Catalogues.Elsewhere([Project("alpha", alpha)], Workspace, null, out var unreadable));
        Assert.Null(unreadable);
    }

    [Fact]
    public void A_project_with_no_catalogue_at_all_is_skipped()
    {
        Assert.Null(Catalogues.Elsewhere([Project("alpha", null)], Workspace, null, out var unreadable));
        Assert.Null(unreadable);
    }

    [Fact]
    public void A_catalogue_that_cannot_be_read_says_nothing_rather_than_saying_nothing_is_there()
    {
        // **This test was named for a property it did not check, which is worse than no test.** It
        // asserted only that no consent was found — the same answer the readable-and-empty case
        // gives — so "we could not look" and "nothing is there" were indistinguishable in exactly
        // the assertion written to tell them apart. The second half is the whole name.
        var path = Path.Combine(_root, "broken-node.json");

        File.WriteAllText(path, "{ this is not json");

        Assert.Null(Catalogues.Elsewhere([Project("alpha", path)], Workspace, null, out var unreadable));

        Assert.NotNull(unreadable);
        Assert.Contains("alpha", unreadable, StringComparison.Ordinal);
        Assert.Contains(path, unreadable, StringComparison.Ordinal);
    }

    [Fact]
    public void No_manifest_at_all_is_no_conflict()
    {
        Assert.Null(Catalogues.Elsewhere(null, Workspace, null, out var unreadable));
        Assert.Null(unreadable);
    }

    [Fact]
    public async Task Adding_an_unattended_agent_the_deployment_already_starts_is_refused()
    {
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));

        var alpha = CatalogueFor("alpha", Workspace);
        var beta = CatalogueFor("beta", null);

        var refusal = await Assert.ThrowsAsync<InvalidDataException>(() => Enrolment.AddAsync(
            new EnrollmentEntry(Workspace, "stock", "What is on hand.", Unattended: true)
            {
                Enroll = true,
                OwnsApproved = true,
            },
            "beta",
            beta,
            Path.Combine(Workspace, "CLAUDE.md"),
            TestContext.Current.CancellationToken,
            [Project("alpha", alpha), Project("beta", beta)]));

        Assert.Contains("alpha", refusal.Message, StringComparison.Ordinal);

        // Nothing was written, including the launch entry that would have been perfectly fine on
        // its own: a half-applied enrolment is the state nobody can read afterwards.
        Assert.False(File.Exists(Path.Combine(Workspace, ".mcp.json")));
    }

    [Fact]
    public async Task An_attended_agent_may_answer_in_a_project_that_starts_it_elsewhere()
    {
        // The whole point of the asymmetry. This repository answers in both; only one machine-level
        // consent to run it unattended exists, and that one is untouched.
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));

        var alpha = CatalogueFor("alpha", Workspace);
        var beta = CatalogueFor("beta", null);

        var outcome = await Enrolment.AddAsync(
            new EnrollmentEntry(Workspace, "stock", "What is on hand.", Unattended: false)
            {
                Enroll = true,
                OwnsApproved = true,
            },
            "beta",
            beta,
            Path.Combine(Workspace, "CLAUDE.md"),
            TestContext.Current.CancellationToken,
            [Project("alpha", alpha), Project("beta", beta)]);

        Assert.Contains(Path.Combine(Workspace, ".mcp.json"), outcome.Written);
        Assert.DoesNotContain("inventory", await File.ReadAllTextAsync(beta, TestContext.Current.CancellationToken));
    }
}
