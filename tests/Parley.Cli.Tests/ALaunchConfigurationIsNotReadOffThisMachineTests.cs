using System.Text.Json.Nodes;
using Parley.Configuration;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>That a repository's own files behind a link to another machine are refused, not looked up.</summary>
/// <remarks>
/// Found by review, lens 2 runs 125 and 126. The node reads every catalogued repository's <c>.mcp.json</c> on each join
/// and each start, the terminal on each draw of its agents list, every <c>parley</c> command the one in the repository it
/// is typed in, and enrolment a declined instructions file and every listed repository's launch configuration. One
/// replaced by a link to a share made Windows sign in to that host as the person, for about 21 seconds each time. Each
/// case dials a host no other test dials, so one answered at once has not merely failed lately.
/// </remarks>
[Integration]
public sealed class ALaunchConfigurationIsNotReadOffThisMachineTests : IDisposable
{
    private readonly DirectoryInfo _workspace = Directory.CreateTempSubdirectory("parley-launch-off-machine-");
    private readonly DirectoryInfo _elsewhere = Directory.CreateTempSubdirectory("parley-launch-off-machine-catalogue-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        // The links, never what they reach: each is deleted as itself, before anything recursive runs.
        foreach (var file in _workspace.GetFiles())
        {
            file.Delete();
        }

        foreach (var folder in _workspace.GetDirectories())
        {
            if (folder.LinkTarget is null)
            {
                folder.Delete(recursive: true);
            }
            else
            {
                folder.Delete();
            }
        }

        _workspace.Delete();
        _elsewhere.Delete(recursive: true);
    }

    [Fact]
    public void A_launch_configuration_linked_to_a_share_is_refused_at_once()
    {
        LinkOrSkip(LaunchConfiguration.FileName, @"\\192.0.2.215\s\m.json");

        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.False(LaunchConfiguration.TryRead(_workspace.FullName, out var configuration, out var problem));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"the path was looked up: {watch.Elapsed}");
        Assert.Contains("another machine", problem, StringComparison.Ordinal);
        Assert.Same(LaunchConfiguration.None, configuration);
    }

    [Fact]
    public void A_command_typed_in_the_repository_does_not_look_its_launch_configuration_up()
    {
        _ = _workspace.CreateSubdirectory(".git");
        LinkOrSkip(LaunchConfiguration.FileName, @"\\192.0.2.216\s\m.json");

        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Empty(RepositoryProjects.Read(_workspace.FullName));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"the path was looked up: {watch.Elapsed}");
    }

    [Fact]
    public async Task Enrolment_does_not_look_up_an_instructions_file_linked_to_a_share()
    {
        _ = _workspace.CreateSubdirectory(".git");
        LinkOrSkip("CLAUDE.md", @"\\192.0.2.217\s\c.md");

        var watch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            _ = await Enrolment.AddAsync(
                new EnrollmentEntry(_workspace.FullName, "orders", null, Unattended: true) { Enroll = true },
                "acme",
                Path.Combine(_elsewhere.FullName, "node.json"),
                Path.Combine(_workspace.FullName, "CLAUDE.md"),
                Ct);
        }
        catch (InvalidDataException)
        {
            // A refusal is an answer too; what matters is how long it took to reach one.
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"the path was looked up: {watch.Elapsed}");
    }

    [Fact]
    public void A_listed_repositorys_launch_configuration_linked_to_a_share_is_not_looked_up()
    {
        LinkOrSkip(LaunchConfiguration.FileName, @"\\192.0.2.218\s\m.json");

        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Null(Enrolment.CopyRefusal(new JsonObject(), _workspace.FullName));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"the path was looked up: {watch.Elapsed}");
    }

    [Fact]
    public async Task Describing_an_enrolment_does_not_look_up_an_instructions_file_linked_to_a_share()
    {
        _ = _workspace.CreateSubdirectory(".git");
        LinkOrSkip("CLAUDE.md", @"\\192.0.2.219\s\c.md");

        var watch = System.Diagnostics.Stopwatch.StartNew();

        _ = await EnrollCommand.EnrolAsync(
            [new EnrollmentEntry(_workspace.FullName, "orders", null, Unattended: true) { Enroll = true }],
            describe: true,
            new Resolved(Path.Combine(_elsewhere.FullName, "node.json"), ValueSource.Argument),
            TextWriter.Null,
            TextWriter.Null,
            cancellationToken: Ct);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"the path was looked up: {watch.Elapsed}");
    }

    private void LinkOrSkip(string name, string target)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "A share path is a Windows path.");

        try
        {
            _ = File.CreateSymbolicLink(Path.Combine(_workspace.FullName, name), target);
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This machine does not let this user make a symbolic link (Windows without Developer Mode).");
        }
    }
}
