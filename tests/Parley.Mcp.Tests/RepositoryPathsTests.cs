using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// That a fetch or a placement names a place inside the agent's own repository and never one reached through a link.
/// </summary>
/// <remarks>
/// Windows keeps a name ending in a space as written, but asking it alone asks the name without the space: the link
/// check looked at a missing `docs` while the write went through the real `docs ` link to a folder outside the
/// repository (review, lens 2 run 80).
/// </remarks>
[Integration]
public sealed class RepositoryPathsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("parley-repo-").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("parley-outside-").FullName;

    public void Dispose()
    {
        foreach (var folder in (string[])[_root, _outside])
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public void A_path_through_a_link_is_refused()
    {
        Assert.SkipUnless(TryLink(Path.Combine(_root, "docs"), _outside), "symbolic links need Developer Mode here");

        Assert.Contains("passes through a link", RepositoryPaths.Resolve(_root, Path.Combine("docs", "notes.md"), out _), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("docs ")]
    [InlineData("docs  ")]
    [InlineData("docs\u3000")]
    public void A_path_through_a_folder_ending_in_a_space_is_refused(string name)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Win32 path rules");
        Assert.SkipUnless(TryLink(@"\\?\" + Path.Combine(_root, name), _outside), "symbolic links need Developer Mode here");

        Assert.NotNull(RepositoryPaths.Resolve(_root, Path.Combine(name, "notes.md"), out _));
    }

    [Fact]
    public void A_plain_folder_inside_the_repository_is_usable()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));

        Assert.Null(RepositoryPaths.Resolve(_root, Path.Combine("docs", "notes.md"), out var resolved));
        Assert.Equal(Path.Combine(_root, "docs", "notes.md"), resolved);
    }

    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
