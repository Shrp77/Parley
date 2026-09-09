using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Review gate, lens 2, run 23: two answers to "is this the same catalogue?".
/// </summary>
/// <remarks>
/// <para>
/// FR-013 rests on two separate comparisons of one question, written in two projects.
/// <c>ProjectManifest.Collisions</c> refuses two projects naming one catalogue — consent to run
/// unattended is per project because the catalogue is — and <c>Catalogues.Elsewhere</c> skips the
/// catalogue it is about to write, so that re-enrolling a repository in the project that already
/// starts it is not read as a second consent.
/// </para>
/// <para>
/// They are the same question and they are not the same code. Where they disagree, one of two things
/// happens: a manifest passes validation while the guard treats its two catalogues as one, or the
/// guard skips a file the manifest thinks is a different project's — and the second is the one that
/// grants a second unattended start. This repository has recorded the shape twice already, in
/// <c>WorkspacePath</c> and in <c>LaunchKey</c>: one rule with two implementations survives until
/// somebody edits one of them.
/// </para>
/// </remarks>
public sealed class ProbeThirtyFourTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe34-" + Path.GetRandomFileName());

    public ProbeThirtyFourTests()
    {
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));
        Directory.CreateDirectory(Path.Combine(_root, "conf"));
    }

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

    private string Workspace => Path.Combine(_root, "orders");

    private string Catalogue(string spelling) => Path.Combine(_root, "conf", spelling);

    private ProjectSettings Project(string name, string? catalogue) =>
        new(ProjectName.Create(name), Path.Combine(_root, $"{name}.db"),
            $"http://127.0.0.1:{7000 + name.Length}", catalogue, null);

    /// <summary>Catalogues alpha's node with this workspace, under a role a node could start.</summary>
    private async Task ListAsync(string catalogue)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(catalogue)!);

        await File.WriteAllTextAsync(
            catalogue,
            $$"""
              { "agents": { "orders": { "command": "claude",
                  "workspace": "{{Workspace.Replace('\\', '/')}}" } } }
              """,
            TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("node.json", "./node.json")]
    [InlineData("node.json", "../conf/node.json")]
    [InlineData("node.json", "node.json")]
    public async Task One_file_spelled_two_ways_is_one_catalogue_to_both_of_them(
        string first, string second)
    {
        // The case that decides it. Alpha's catalogue already starts this workspace; beta names the
        // same file by another spelling. `Collisions` must refuse the manifest, and if it somehow
        // does not, `Elsewhere` must not skip the file as "the one being written" — the two answers
        // have to point the same way.
        var alpha = Catalogue(first);
        var beta = Catalogue(second);

        await ListAsync(alpha);

        var manifest = new[] { Project("alpha", alpha), Project("beta", beta) };

        var refused = ProjectManifest.Collisions(manifest).Count > 0;

        var found = Catalogues.Elsewhere(manifest, Workspace, beta, out var unreadable);

        Assert.True(
            refused || found is not null || unreadable is not null,
            $"'{first}' and '{second}' are one file, and neither the manifest nor the guard said so");
    }

    [Fact]
    public async Task A_genuinely_different_catalogue_is_still_a_conflict_the_guard_reports()
    {
        // The control in the other direction: two projects with two real catalogues is the ordinary
        // deployment, and the workspace catalogued in one must still be found when enrolling into
        // the other. A comparison that called everything the same would satisfy the theory above.
        var alpha = Catalogue("alpha-node.json");
        var beta = Catalogue("beta-node.json");

        await ListAsync(alpha);

        var found = Catalogues.Elsewhere(
            [Project("alpha", alpha), Project("beta", beta)], Workspace, beta, out _);

        Assert.NotNull(found);
        Assert.Equal("alpha", found.Project);
    }

    [Fact]
    public async Task Re_enrolling_into_the_project_that_already_starts_it_is_not_a_second_consent()
    {
        // The other control, and the reason the skip exists at all: the catalogue being written is
        // this project's own, so finding this workspace in it is finding what this enrolment put
        // there. A rule that stopped skipping would refuse every repair.
        var alpha = Catalogue("alpha-node.json");

        await ListAsync(alpha);

        Assert.Null(Catalogues.Elsewhere([Project("alpha", alpha)], Workspace, alpha, out var unreadable));
        Assert.Null(unreadable);
    }
}
