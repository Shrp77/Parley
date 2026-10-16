using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That `parley terminal` and `parley web-ui` hand their own flags to the tool they forward to.
/// </summary>
/// <remarks>
/// <para>
/// The surfaces are installed apart from the broker's tool, so the version each reports is the fact somebody needs when
/// they disagree. `parley` must not answer `--version` and `--help` before it looks at the verb, which would make
/// `parley terminal --version` print `parley`'s version and `parley web-ui --help` print `parley`'s manual — while the
/// README says forwarding works identically to typing the tool's own name.
/// </para>
/// <para>
/// Run with no surface on <c>PATH</c>, so the forwarded call is observable without one: a flag that reached the
/// forwarder says the tool is not installed, and a flag answered here prints a version or a manual.
/// </para>
/// </remarks>
public sealed class ForwardedFlagsTests
{
    public static TheoryData<string, string, string> Flags => new()
    {
        { "terminal", "--version", "parley-terminal" },
        { "terminal", "-v", "parley-terminal" },
        { "terminal", "--help", "parley-terminal" },
        { "terminal", "-h", "parley-terminal" },
        { "web-ui", "--version", "parley-web-ui" },
        { "web-ui", "--help", "parley-web-ui" },
    };

    [Theory]
    [MemberData(nameof(Flags))]
    public void A_surfaces_flag_reaches_the_surface(string verb, string flag, string tool)
    {
        var (exitCode, error) = ToolProcess.Run([("PATH", Environment.SystemDirectory)], verb, flag);

        Assert.Equal(69, exitCode);
        Assert.Contains($"provided by {tool}, which is not installed", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tools_own_flags_are_still_its_own()
    {
        // The control: the same flags with no surface named are still answered here.
        var (exitCode, error) = ToolProcess.Run([("PATH", Environment.SystemDirectory)], "--version");

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("not installed", error, StringComparison.Ordinal);
    }
}
