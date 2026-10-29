using System.Diagnostics;
using Parley.Configuration;
using Parley.Setup;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That a folder link to another machine, inside a path that reads as local, is never followed by a lookup.
/// </summary>
/// <remarks>
/// <b>Found by review, lens 1 run 67.</b> 0.47.30 refused a path written as a share, but an agent can make a link in its
/// own repository, <c>docs</c> to <c>\\host\share</c>, and a link to the Enrol page naming <c>…\docs</c> passed the text
/// check; the lookup followed the link and opened a session to the host with the person's sign-in. The folder browser
/// did the same for any folder holding such a link, checking each child for <c>.git</c>. The host is TEST-NET, which
/// never routes, so a lookup that slipped through shows as the 21-second SMB wait.
/// </remarks>
public sealed class NoLinkLeadsALookupOffThisMachineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-links-" + Guid.NewGuid().ToString("N"));

    public NoLinkLeadsALookupOffThisMachineTests() => Directory.CreateDirectory(_root);

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

    private string LinkToAShare(string name)
    {
        var link = Path.Combine(_root, name);

        try
        {
            // A host of its own for each test: Windows remembers a host it failed to reach, so a second test against the
            // same one would come back at once even if it did look the share up.
            var host = "192.0.2." + Random.Shared.Next(10, 250).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Directory.CreateSymbolicLink(link, OperatingSystem.IsWindows() ? $@"\\{host}\share\{name}" : $"//{host}/share/{name}");
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"this machine cannot make a folder link here: {refused.Message}");
        }

        return link;
    }

    [Fact]
    public void A_path_through_a_link_to_a_share_leads_off_this_machine()
    {
        var link = LinkToAShare("docs");

        Assert.True(OffMachineLinks.LeadsOffThisMachine(link));
        Assert.True(OffMachineLinks.LeadsOffThisMachine(Path.Combine(link, "deeper", ".git")));
        Assert.False(OffMachineLinks.LeadsOffThisMachine(_root));
    }

    [Fact]
    public void A_link_to_the_redirector_by_its_device_name_leads_off_this_machine()
    {
        // Review, lens 1 run 68: .NET hands back \\?\GLOBALROOT\Device\Mup\host as the relative-looking
        // GLOBALROOT\Device\Mup\host, which Windows still follows to the network.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the NT namespace is Windows'");
        var link = Path.Combine(_root, "g2");
        var host = "192.0.2." + Random.Shared.Next(10, 250).ToString(System.Globalization.CultureInfo.InvariantCulture);

        try
        {
            Directory.CreateSymbolicLink(link, $@"\\?\GLOBALROOT\Device\Mup\{host}\s");
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"this machine cannot make a folder link here: {refused.Message}");
        }

        var waited = Stopwatch.StartNew();

        Assert.True(OffMachineLinks.LeadsOffThisMachine(Path.Combine(link, ".git")));
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(5), $"the check waited {waited.Elapsed}, as a lookup would");
    }

    [Fact]
    public void A_link_whose_own_permissions_hide_its_target_is_never_walked_through()
    {
        // Review, lens 1 run 68: with its target unreadable the link read as a plain folder, and the walk asked the path
        // below it, which followed the link to the share: the check itself was the lookup.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the permissions are Windows'");
        var link = LinkToAShare("c8");
        using var icacls = Process.Start(new ProcessStartInfo("icacls", [link, "/L", "/inheritance:r", "/grant:r", "*S-1-1-0:(X)"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        icacls.WaitForExit();
        Assert.SkipUnless(icacls.ExitCode == 0, "icacls could not hide the link's target");

        var waited = Stopwatch.StartNew();

        Assert.True(OffMachineLinks.LeadsOffThisMachine(Path.Combine(link, ".git")));
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(5), $"the check waited {waited.Elapsed}, as a lookup would");
    }

    [Fact]
    public void A_link_to_a_local_folder_with_nothing_below_it_is_still_this_machine()
    {
        var target = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(target);
        var link = Path.Combine(_root, "docs");

        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"this machine cannot make a folder link here: {refused.Message}");
        }

        Assert.False(OffMachineLinks.LeadsOffThisMachine(link));
        Assert.False(OffMachineLinks.LeadsOffThisMachine(Path.Combine(link, ".git")));
    }

    [Fact]
    public async Task The_enrol_page_refuses_a_workspace_reached_through_such_a_link()
    {
        var link = LinkToAShare("docs");
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();
        var waited = Stopwatch.StartNew();

        var body = await client.GetStringAsync(
            new Uri("/Projects/Enrol?project=alpha&role=x&workspace=" + Uri.EscapeDataString(link), UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Contains("names another machine or a device", body, StringComparison.Ordinal);
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(5), $"the page waited {waited.Elapsed}, as a lookup would");
    }

    [Fact]
    public async Task The_folder_browser_lists_a_folder_holding_such_a_link_without_looking_into_it()
    {
        _ = LinkToAShare("docs");
        Directory.CreateDirectory(Path.Combine(_root, "orders", ".git"));
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();
        var waited = Stopwatch.StartNew();

        var body = await client.GetStringAsync(
            new Uri("/?handler=Folders&path=" + Uri.EscapeDataString(_root), UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Contains("\"orders\"", body, StringComparison.Ordinal);
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(5), $"the browser waited {waited.Elapsed}, as a lookup would");
    }

    [Fact]
    public void A_scan_for_repositories_passes_over_such_a_link_unread()
    {
        _ = LinkToAShare("docs");
        Directory.CreateDirectory(Path.Combine(_root, "orders", ".git"));
        var waited = Stopwatch.StartNew();

        var found = EnrollCommand.Find(_root);

        Assert.Equal("orders", Path.GetFileName(Assert.Single(found).Workspace));
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(5), $"the scan waited {waited.Elapsed}, as a lookup would");
    }
}
