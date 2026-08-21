using System.Reflection;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That the operator's operations and the agent's stay apart.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §10.2 opens by warning that an adapter surfacing an operator operation into an
/// agent's tool set would breach §10.1, and the protection it offers is a naming convention:
/// <c>force_conclude</c> is deliberately not <c>conclude</c>. That is real and thin, and the
/// safeguards here get progressively more structural — the operations are not on
/// <c>Broker</c>, and now they are not in the binary an MCP client launches either.
/// </para>
/// <para>
/// What this pins is the last link. <c>SurfaceBoundaryTests</c> checks that a broker has no
/// operator operation to bind; these check that the two hosts are different programs and that
/// their names cannot be confused in a launch configuration.
/// </para>
/// </remarks>
public sealed class SeparationTests
{
    private static IEnumerable<string> PublicMethodsOf(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Where(n => n is not ("Equals" or "GetHashCode" or "ToString"));

    [Fact]
    public void The_operator_commands_are_exactly_the_operations_of_section_10_2()
    {
        // All five of §10.2 now that prune exists. Whichever arrives next, the check that fails
        // is this one, which is the point: adding an operator operation should be a decision
        // about which side of §10.1 it belongs on rather than a question of which class the
        // method was already in.
        string[] expected =
        [
            "AgentsAsync",
            "ExportAsync",
            "ForceConcludeAsync",
            "PruneAsync",
            "ReinstateAsync",
            "RetireAsync"
        ];

        Assert.Equal(expected, PublicMethodsOf(typeof(OperatorCommands)).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void No_operator_command_shares_a_name_with_an_agent_operation()
    {
        // The specific mistake §10.2 describes, one layer out from where SurfaceBoundaryTests
        // catches it. If the operator's listing were called ListAgentsAsync like the broker's,
        // the two would differ only in which object was in hand — and they differ in what they
        // disclose: one carries the workspace path and the adapter configuration, the other is
        // the projection that exists so neither reaches a peer.
        var agentOperations = PublicMethodsOf(typeof(Parley.Broker.Broker)).ToHashSet(StringComparer.Ordinal);

        var collisions = PublicMethodsOf(typeof(OperatorCommands))
            .Where(agentOperations.Contains)
            .ToArray();

        Assert.Empty(collisions);
    }

    [Fact]
    public void The_agent_server_contains_no_operator_operation()
    {
        // The structural half. An MCP client launches the command its configuration names, and
        // parley-mcp has nothing from §10.2 compiled into it — so an adapter cannot surface one
        // by accident, because there is nothing there to surface.
        var server = Assembly.Load("parley-mcp");

        var leaked = server.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.Instance | BindingFlags.Static |
                                          BindingFlags.DeclaredOnly))
            .Where(m => m.Name is "RetireAsync" or "ReinstateAsync" or "ForceConcludeAsync"
                                or "ExportAsync" or "DescribeAgentsAsync" or "PruneAsync")
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .ToArray();

        Assert.Empty(leaked);
    }

    [Fact]
    public void The_two_commands_are_named_apart()
    {
        // parley and parley-mcp, not one binary with a flag. A launch configuration names a
        // command, and a person editing one should not be able to reach the operator surface by
        // getting a flag wrong.
        Assert.NotEqual(
            typeof(OperatorCommands).Assembly.GetName().Name,
            Assembly.Load("parley-mcp").GetName().Name);
    }
}
