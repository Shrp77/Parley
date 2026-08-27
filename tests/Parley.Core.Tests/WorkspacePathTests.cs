using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// The comparison behind the collision check of SPEC.md §4.1.1.
/// </summary>
/// <remarks>
/// Both directions are asserted here, because the defect this replaced was wrong in both and
/// each looked like a different bug: one repository refused its own name, and two repositories
/// admitted under one.
/// </remarks>
public sealed class WorkspacePathTests
{
    [Theory]
    [InlineData("C:/repo-infra", @"C:\repo-infra")]
    [InlineData(@"C:\repo\docs", "C:/repo/docs")]
    [InlineData("/src/docs", "/src/docs/")]
    [InlineData("/src/docs", "  /src/docs  ")]
    public void Spellings_of_one_directory_are_the_same_repository(string left, string right) =>
        Assert.True(WorkspacePath.SameRepository(left, right));

    [Theory]
    [InlineData("/src/Archive", "/src/archive")]
    [InlineData("/src/docs", "/src/infra")]
    [InlineData("/src/docs", "/src/docs-old")]
    public void Directories_that_may_genuinely_differ_are_not(string left, string right) =>
        Assert.False(WorkspacePath.SameRepository(left, right));

    [Fact]
    public void A_backslash_in_a_unix_path_is_a_name_and_not_a_separator()
    {
        // A backslash is a legal character in a Unix filename, so these are two directories and
        // must not be merged into one repository. Unifying separators unconditionally -- which
        // is the obvious way to make `C:/repo` match `C:\repo` -- reintroduces exactly the
        // collision the case rule above refuses, in a corner nobody would look at again.
        Assert.False(WorkspacePath.SameRepository(@"/src/a\b", "/src/a/b"));

        // And the Windows case still has to work, which is the whole reason separators are
        // touched at all.
        Assert.True(WorkspacePath.SameRepository(@"C:\src\a", "C:/src/a"));
    }

    [Fact]
    public void A_drive_root_is_not_the_drive()
    {
        // `C:` names that drive's current directory and `C:/` names its root, so trimming the
        // trailing separator off a drive-qualified path changes which directory is meant.
        Assert.False(WorkspacePath.SameRepository("C:", "C:/"));
    }

    [Fact]
    public void Case_is_significant_because_the_broker_cannot_know_the_filesystem()
    {
        // The decision recorded rather than assumed. `/src/Archive` and `/src/archive` are one
        // directory on NTFS and two on ext4, and the broker performing this comparison may not
        // be on either machine. Of the two available errors, refusing a legitimate registration
        // is loud and addressed to somebody who can fix it; admitting a second repository under
        // a name that already belongs to one is silent where it happens and surfaces later as a
        // peer receiving replies about a repository it never asked about.
        Assert.False(WorkspacePath.SameRepository("/src/Archive", "/src/archive"));
    }

    [Theory]
    [InlineData(null, "/src/docs")]
    [InlineData("/src/docs", null)]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "/src/docs")]
    public void An_unreported_workspace_matches_nothing(string? left, string? right) =>
        Assert.False(WorkspacePath.SameRepository(left, right));

    [Fact]
    public void The_root_of_a_unix_filesystem_survives_trimming()
    {
        // A single separator is a real path and trimming trailing separators unconditionally
        // would reduce it to nothing, which then matches every other empty reading.
        Assert.Equal("/", WorkspacePath.Canonical("/"));

        // A lone backslash is not the same path, and this assertion is the one that changed when
        // separators stopped being unified everywhere. On a Unix filesystem it is a one-character
        // filename; treating it as the root because it looks like a separator on another
        // operating system is the merge this comparison refuses to make on a guess.
        Assert.False(WorkspacePath.SameRepository("/", @"\"));
    }
}
