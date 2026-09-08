using Parley.Configuration;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That the host binds the address every other package thinks it binds.
/// </summary>
/// <remarks>
/// The other half of the check in <c>Parley.Top.Tests</c>. The number moved to
/// <c>Parley.Configuration</c> because four things in three packages state it — the host, the
/// broker that starts it, the autostart entry it registers, and the terminal's browser action —
/// and there was nowhere else all four could see. <c>ConsoleHost.LoopbackAddress</c> stayed as a
/// name because this is the assembly that binds it; this is what keeps the name and the number
/// from ever being two answers again. They were, once, and the visible failure was a browser
/// opening a port nothing listened on while the surface reported success.
/// </remarks>
public sealed class WebUiAddressTests
{
    [Fact]
    public void The_host_binds_the_one_address_every_package_reads()
    {
        Assert.Equal(WebUiAddress.Loopback, ConsoleHost.LoopbackAddress);
    }

    [Fact]
    public void The_host_writes_the_number_nowhere_of_its_own()
    {
        var source = RepositorySource("ConsoleHost.cs");

        // The literal may appear exactly once in the repository, and not here.
        Assert.DoesNotContain("127.0.0.1:5280", source, StringComparison.Ordinal);
        Assert.Contains("WebUiAddress.Loopback", source, StringComparison.Ordinal);
    }

    private static string RepositorySource(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
               && !Directory.Exists(Path.Combine(directory.FullName, "src", "Parley.Web")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return File.ReadAllText(Path.Combine(directory!.FullName, "src", "Parley.Web", name));
    }
}
