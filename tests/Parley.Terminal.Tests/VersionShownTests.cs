using Grpc.Net.Client;
using Parley.Client;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the terminal says which build it is, on the screen rather than only behind <c>--version</c>.
/// </summary>
/// <remarks>
/// Asked for by the owner while a fix was being installed over a running terminal, with no way to tell from the screen
/// which build was drawing it. On a wide terminal the status line says it; on a narrow one, where the status line keeps
/// only the focus and <c>?</c>, the actions menu does.
/// </remarks>
public sealed class VersionShownTests
{
    [Fact]
    public void The_label_is_this_tools_own_build()
    {
        Assert.Equal($"parley-terminal {BuildIdentity.Version}", Shell.VersionLabel);
        Assert.Matches(@"^\d+\.\d+\.\d+", BuildIdentity.Version);
    }

    [Fact]
    public void A_wide_terminal_names_the_build_on_the_status_line()
    {
        using var driving = Driving.Open(width: 200, height: 30);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        shell.Lay(200);
        driving.App.LayoutAndDraw(true);

        Assert.EndsWith(Shell.VersionLabel, shell.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void A_narrow_terminal_drops_the_build_before_anything_the_line_exists_for()
    {
        using var driving = Driving.Open(width: 40, height: 30);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        shell.Lay(40);
        driving.App.LayoutAndDraw(true);

        Assert.DoesNotContain(Shell.VersionLabel, shell.Status, StringComparison.Ordinal);
        Assert.Contains(Shell.ActionsKey.ToString(), shell.Status, StringComparison.Ordinal);
    }

    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null, null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));
}
