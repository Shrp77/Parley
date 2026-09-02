using Parley.Cli.Top;
using Spectre.Console.Testing;
using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// What the browser-launch action will and will not hand to the platform.
/// </summary>
/// <remarks>
/// On Windows the address goes to the shell, which launches whatever handler its scheme is
/// registered to. Today the only caller passes a constant, so this guards the second caller rather
/// than the first — and the second caller is the one that opens an address which came from a
/// manifest or a broker.
/// </remarks>
public sealed class LaunchTests
{
    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-msdt:/id")]
    [InlineData("not an address at all")]
    public void Only_web_addresses_are_opened(string hostile)
    {
        using var console = new TestConsole();

        Assert.False(Launch.Open(console, hostile));
        Assert.Contains("not an http or https address", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_refusal_is_not_the_only_thing_this_can_do()
    {
        // Non-vacuous: an http address gets past the guard. Whether a browser actually starts is
        // the platform's business and is not asserted — there is none on a CI runner, and
        // Launch.Open reports that rather than throwing, which is the behaviour FR-014 needs.
        using var console = new TestConsole();

        Launch.Open(console, "http://127.0.0.1:5300");

        Assert.DoesNotContain(
            "not an http or https address", console.Output, StringComparison.Ordinal);
    }
}
