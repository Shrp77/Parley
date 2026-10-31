using Parley.Configuration;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>That a repository's launch configuration behind a link to another machine is refused, not looked up.</summary>
/// <remarks>
/// Found by review, lens 2 run 125. The node reads every catalogued repository's <c>.mcp.json</c> on each join and each
/// start, the terminal on each draw of its agents list, and enrolment over every other catalogued repository. One
/// replaced by a link to a share made Windows sign in to that host as the person, for about 21 seconds each time.
/// </remarks>
[Integration]
public sealed class ALaunchConfigurationIsNotReadOffThisMachineTests : IDisposable
{
    private readonly DirectoryInfo _workspace = Directory.CreateTempSubdirectory("parley-launch-off-machine-");

    public void Dispose()
    {
        var link = Path.Combine(_workspace.FullName, LaunchConfiguration.FileName);

        // The link, never what it reaches.
        if (File.Exists(link) || new FileInfo(link).LinkTarget is not null)
        {
            File.Delete(link);
        }

        _workspace.Delete(recursive: false);
    }

    [Fact]
    public void A_launch_configuration_linked_to_a_share_is_refused_at_once()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "A share path is a Windows path.");

        try
        {
            // A host no other test dials, so one answered at once has not merely failed lately.
            _ = File.CreateSymbolicLink(Path.Combine(_workspace.FullName, LaunchConfiguration.FileName), @"\\192.0.2.215\s\m.json");
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This machine does not let this user make a symbolic link (Windows without Developer Mode).");
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.False(LaunchConfiguration.TryRead(_workspace.FullName, out var configuration, out var problem));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"the path was looked up: {watch.Elapsed}");
        Assert.Contains("another machine", problem, StringComparison.Ordinal);
        Assert.Same(LaunchConfiguration.None, configuration);
    }
}
