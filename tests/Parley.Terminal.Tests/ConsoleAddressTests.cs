using Parley.Configuration;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the address this surface hands a browser is the address the console listens on.
/// </summary>
/// <remarks>
/// <para>
/// Found in use rather than by a lens, while writing out the walk of <c>quickstart.md</c>: the
/// owner's console was on <c>5280</c> and the terminal's constant said <c>5045</c>, so *Open the
/// web console in a browser* opened a dead port and reported <c>Opened …</c>. Nothing could have
/// caught it, because from the launcher's side the browser started — the failure is entirely in
/// the number.
/// </para>
/// <para>
/// Checked at the source rather than through the action, because the constant is private and
/// making it visible for a test is a worse trade than reading the file: what matters is that the
/// number is not written here a second time, and that is a property of the text.
/// </para>
/// <para>
/// <b>The number now lives in <c>Parley.Configuration</c> rather than in the host that binds it.</b>
/// It had to move: four things state this address — the host, the broker that starts it, the
/// autostart entry, and this surface's browser action — and they are in three packages now, so
/// there was nowhere else all four could see. The half of this check that compares the host's own
/// constant against it lives in <c>Parley.Web.Tests</c>, because there is no web stack on this
/// project's graph and there must not be one.
/// </para>
/// </remarks>
public sealed class ConsoleAddressTests
{
    private static string Shell()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
               && !Directory.Exists(Path.Combine(directory.FullName, "src", "Parley.Terminal")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return File.ReadAllText(
            Path.Combine(directory!.FullName, "src", "Parley.Terminal", "Shell.cs"));
    }

    [Fact]
    public void The_surface_takes_the_address_from_the_console_rather_than_restating_it()
    {
        Assert.Contains("WebUiAddress.Loopback", Shell(), StringComparison.Ordinal);
    }

    [Fact]
    public void No_loopback_address_is_written_out_a_second_time()
    {
        // Any http://127.0.0.1:<port> here is a copy of a number that lives somewhere else, and a
        // copy is what this test exists to have failed on.
        Assert.DoesNotContain("http://127.0.0.1:", Shell(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_check_is_looking_at_something()
    {
        Assert.Equal("http://127.0.0.1:5280", WebUiAddress.Loopback);
        Assert.Contains("OpenConsole", Shell(), StringComparison.Ordinal);
    }
}
