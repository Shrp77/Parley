using System.Diagnostics;
using System.Net;
using Parley.Core;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That no page looks up a path naming another machine, because on Windows the lookup itself offers the person's
/// credentials to that machine.
/// </summary>
/// <remarks>
/// <b>Found by review, lens 2 run 70.</b> An agent's message renders as live links. A link to the Enrol page or the
/// folder browser naming <c>\\host\share</c> had the person's own click make the console ask that host whether the
/// folder existed: a 21-second SMB attempt to a host the agent chose, carrying the person's sign-in. The address is
/// TEST-NET, which never routes, so a lookup that slipped through shows as a long wait rather than reaching anybody.
/// </remarks>
public sealed class NoPathIsLookedUpOnAnotherMachineTests
{
    private const string Share = @"\\192.0.2.1\share\repo";

    [Theory]
    [InlineData("/Projects/Enrol?project=alpha&role=x&workspace=")]
    [InlineData("/Projects/Enrol?project=alpha&folder=")]
    public async Task The_enrol_page_refuses_a_share_without_asking_it_anything(string page)
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();
        var waited = Stopwatch.StartNew();

        var body = await client.GetStringAsync(
            new Uri(page + Uri.EscapeDataString(Share), UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Contains("names another machine or a device", body, StringComparison.Ordinal);
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(5), $"the page waited {waited.Elapsed}, as a lookup would");
    }

    [Fact]
    public async Task The_folder_browser_refuses_a_share_without_asking_it_anything()
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();
        var waited = Stopwatch.StartNew();

        using var response = await client.GetAsync(
            new Uri("/?handler=Folders&path=" + Uri.EscapeDataString(Share), UriKind.Relative),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("another machine", body, StringComparison.Ordinal);
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(5), $"the browser waited {waited.Elapsed}, as a lookup would");
    }

    [Theory]
    [InlineData(@"\\host\share", true)]
    [InlineData("//host/share", true)]
    [InlineData(@"\/host\share", true)]
    [InlineData(@"\\?\C:\repo", true)]
    [InlineData(@"\\.\pipe\x", true)]
    [InlineData(@"\??\UNC\host\share", true)]
    [InlineData(@"  \\host\share", true)]
    [InlineData(@"C:\src\orders", false)]
    [InlineData("/home/someone/orders", false)]
    [InlineData("orders", false)]
    [InlineData(null, false)]
    public void A_path_off_this_machine_is_known_by_its_text(string? path, bool off)
    {
        Assert.Equal(off, LocalPath.IsOffThisMachine(path));
    }
}
