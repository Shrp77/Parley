using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That the broker the <c>serve</c> command runs wakes agents for the work they owe when it starts (SPEC.md §14).
/// </summary>
/// <remarks>
/// The owner's decision of 2026-11-18. The mechanism is tested in the broker's resume tests; this holds that the
/// command passes the policy, since a broker built without options is <c>never</c> and a lost argument would
/// leave in-flight work idle after every restart with every other test green.
/// </remarks>
[Integration]
public sealed class AServedBrokerWakesTheAgentsThatOweWorkTests
{
    private static readonly string Root =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void Serve_opens_its_broker_with_the_always_policy()
    {
        var source = File.ReadAllText(Path.Combine(Root, "src", "Parley.Cli", "ServeCommand.cs"));

        Assert.Contains("BrokerOptions.Default with { ResumePolicy = Core.ResumePolicy.Always }", source, StringComparison.Ordinal);
    }
}
