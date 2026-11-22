using System.Diagnostics;
using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>That pressing a listed folder that is a link to a share looks nothing up on the share.</summary>
/// <remarks>
/// The browser listed such a row and, when it was pressed, listed what was inside it by path: Windows dialled the share
/// and offered the person's sign-in (review, lens 2 run S2 on 3b752970: 21 seconds, the SMB wait).
/// </remarks>
[Integration]
public sealed class TheBrowserNeverWalksIntoALinkToAShareTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("parley-browse-link-").FullName;

    /// <summary>Removes the folder, links as links.</summary>
    public void Dispose()
    {
        // rmdir removes links as links; a recursive .NET walk would follow them to the share.
        using var rd = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "rmdir", "/s", "/q", _root]) { UseShellExecute = false })!;
        rd.WaitForExit();
    }

    [Theory]
    [InlineData("symlink")]
    [InlineData("junction-chain")]
    public void Pressing_a_listed_link_to_a_share_looks_nothing_up(string kind)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a share lookup is a Windows sign-in");
        // A fresh host each run: Windows answers a host it failed to reach a moment ago at once, which hides the lookup.
        var host = $"198.51.100.{Random.Shared.Next(2, 254)}";
        Directory.CreateDirectory(Path.Combine(_root, "orders", ".git"));
        var docs = Path.Combine(_root, "docs");

        try
        {
            if (kind == "symlink")
            {
                Directory.CreateSymbolicLink(docs, $@"\\{host}\s");
            }
            else
            {
                var stage = Path.Combine(_root, ".stage");
                Directory.CreateDirectory(stage);
                Directory.CreateSymbolicLink(Path.Combine(stage, "s"), $@"\\{host}\s");
                using var mk = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", docs, stage])
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                })!;
                mk.WaitForExit();
                Assert.Equal(0, mk.ExitCode);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("this account cannot make symbolic links");
        }

        using var driving = Driving.Open();
        var browsing = new Browsing(_root);
        driving.Show(browsing);
        var row = Browsing.Indent + Browsing.Plain + "docs";
        Assert.Contains(row, Drawn.Rows(browsing.List));

        if (kind != "symlink")
        {
            driving.Row(browsing.List, row);
            browsing.List.NewKeyDownEvent(Key.Enter);
            row = Browsing.Indent + Browsing.Plain + "s";
            Assert.Contains(row, Drawn.Rows(browsing.List));
        }

        driving.Row(browsing.List, row);
        var waited = Stopwatch.StartNew();
        browsing.List.NewKeyDownEvent(Key.Enter);

        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(5), $"pressing the row waited {waited.Elapsed}, as a lookup on {host} would");

        // And the person is told why nothing opened, rather than left with a press that did nothing.
        Assert.Equal(Parley.Core.LocalPath.OffThisMachine, browsing.Said);
    }
}
