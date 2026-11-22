using Parley.Configuration;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>Two guards in Parley.Configuration that every test passed without.</summary>
[Integration]
public sealed class ConfigurationGuardsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-configuration-guards-" + Guid.NewGuid().ToString("N"));
    private readonly string _share = $@"\\192.0.2.{Random.Shared.Next(2, 254)}\share";

    public ConfigurationGuardsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            var link = Path.Combine(_root, "sharelink");

            if (Directory.Exists(link) || File.Exists(link))
            {
                Directory.Delete(link);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void A_folder_with_no_launch_configuration_reads_as_saying_nothing_never_as_unreadable()
    {
        // "Could not be read" and "says nothing" are different answers, and a node reports the first: a repository with no
        // .mcp.json (most folders) must not be reported as one it could not read.
        Assert.True(LaunchConfiguration.TryRead(_root, out var declared, out var problem), problem);

        Assert.Null(problem);
        Assert.Same(LaunchConfiguration.None, declared);
    }

    [Fact]
    public void A_folder_typed_with_a_tilde_behind_a_link_to_a_share_is_judged_without_looking_it_up()
    {
        // Path.GetFullPath expands short names whenever the text holds `~`, walking the real path and following the link to
        // the share before anything has read a part (review, lens 2 round 14). LocalStorage makes the path full as text.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "links to shares are a Windows sign-in");

        var link = Path.Combine(_root, "sharelink");

        try
        {
            Directory.CreateSymbolicLink(link, _share);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("this account cannot make symbolic links");
        }

        var timer = System.Diagnostics.Stopwatch.StartNew();

        Assert.NotNull(LocalStorage.Problem(Path.Combine(link, "x~y")));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(4), $"took {timer.Elapsed}: a short name was expanded through the link to the share");
    }
}
