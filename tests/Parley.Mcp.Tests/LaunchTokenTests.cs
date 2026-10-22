using Parley.Core;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// That the adapter takes the launch token its node put in its environment, and presents nothing when there is none
/// (SPEC.md §11.7).
/// </summary>
public sealed class LaunchTokenTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] set)
    {
        var map = set.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    [Fact]
    public void A_process_its_node_started_carries_the_token_it_was_given()
    {
        var options = ServerOptions.Read(Env(
            (ServerOptions.AgentVariable, "orders@test"),
            (ServerOptions.BrokerVariable, "http://127.0.0.1:1"),
            (LaunchToken.Variable, "abc123")));

        Assert.Equal("abc123", options.LaunchToken);
    }

    [Fact]
    public void A_process_somebody_opened_carries_none()
    {
        var options = ServerOptions.Read(Env(
            (ServerOptions.AgentVariable, "orders@test"),
            (ServerOptions.BrokerVariable, "http://127.0.0.1:1")));

        Assert.Null(options.LaunchToken);
    }
}
