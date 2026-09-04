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

        // Through Open rather than through the predicate, so this covers the guard being wired
        // in and not only the guard being correct. Safe to call: the refusal path is the one
        // path that hands nothing to the platform.
        Assert.False(Launch.Open(console, hostile));
        Assert.Contains("not an http or https address", console.Output, StringComparison.Ordinal);
        Assert.False(Launch.IsWebAddress(hostile));
    }

    [Theory]
    [InlineData("http://127.0.0.1:5280")]
    [InlineData("https://example.invalid/console")]
    public void The_refusal_is_not_the_only_thing_this_can_do(string ordinary)
    {
        // Non-vacuous: a web address gets past the guard, so the refusals above are a guard doing
        // its job rather than a method that refuses everything.
        //
        // Asserted on the predicate, deliberately. This called Open, on the reasoning that
        // whether a browser starts is the platform's business and there is none on a CI runner —
        // and so it opened a real tab every time somebody ran the suite on a machine that has
        // one, which is every machine this is developed on.
        Assert.True(Launch.IsWebAddress(ordinary));
    }
}
