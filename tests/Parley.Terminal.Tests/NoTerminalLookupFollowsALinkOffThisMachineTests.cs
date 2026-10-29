using System.Diagnostics;
using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the terminal's folder browser and its workspace check never look up a path through a link to another machine,
/// and that a link whose target is hidden is still a link to enrolment.
/// </summary>
/// <remarks>
/// <b>Found by review, lens 2 run 72.</b> 0.47.31 guarded the web console's pages and left the terminal's: a folder link
/// an agent made beside its repository had every listing in Add an agent ask the share it names, with the person's
/// sign-in (22.8 s). And <c>Enrolment.Linked</c>, the guard against writing through a link, read a link whose own
/// permissions hide its target as no link. TEST-NET hosts never route, so a lookup that slipped through shows as the
/// SMB wait; each test has its own, because Windows remembers a host it failed to reach.
/// </remarks>
public sealed class NoTerminalLookupFollowsALinkOffThisMachineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-terminal-links-" + Guid.NewGuid().ToString("N"));

    public NoTerminalLookupFollowsALinkOffThisMachineTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string LinkToAShare(string name, bool file = false)
    {
        var link = Path.Combine(_root, name);
        var host = "192.0.2." + Random.Shared.Next(10, 250).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var target = OperatingSystem.IsWindows() ? $@"\\{host}\share\{name}" : $"//{host}/share/{name}";

        try
        {
            if (file)
            {
                File.CreateSymbolicLink(link, target);
            }
            else
            {
                Directory.CreateSymbolicLink(link, target);
            }
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"this machine cannot make a link here: {refused.Message}");
        }

        return link;
    }

    [Fact]
    public void The_folder_browser_never_asks_a_linked_share_whether_it_is_a_repository()
    {
        var link = LinkToAShare("docs");
        var waited = Stopwatch.StartNew();

        Assert.False(Browsing.IsRepository(link));
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(5), $"the browser waited {waited.Elapsed}, as a lookup would");
    }

    [Fact]
    public void A_workspace_reached_through_a_linked_share_is_refused_before_it_is_looked_up()
    {
        var link = LinkToAShare("orders");
        var waited = Stopwatch.StartNew();

        Assert.Equal(LocalPath.OffThisMachine, Adding.WorkspaceProblem(link));
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(5), $"the check waited {waited.Elapsed}, as a lookup would");
    }

    [Fact]
    public void A_link_whose_own_permissions_hide_its_target_is_still_a_link_to_enrolment()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the permissions are Windows'");
        var link = LinkToAShare(".mcp.json", file: true);
        using var icacls = Process.Start(new ProcessStartInfo(
            "icacls", [link, "/L", "/deny", $"{Environment.UserDomainName}\\{Environment.UserName}:(S)"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        icacls.WaitForExit();
        Assert.SkipUnless(icacls.ExitCode == 0, "icacls could not hide the link's target");

        Assert.NotNull(Enrolment.Linked(link));
    }
}
