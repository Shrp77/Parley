using System.Reflection;
using Parley.Core;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// Every environment variable a Parley deployment is configured with.
/// </summary>
/// <remarks>
/// <para>
/// Read from the constants that define them rather than from a list written in a test, so a
/// variable added to a launch configuration is documented or the build says so. A list would be
/// one more thing to keep current, which is the failure these checks exist to catch.
/// </para>
/// <para>
/// Shared because two checks want the same set for opposite reasons: the README has to mention
/// all of them, and the plugin's setup command must mention nothing else. A second copy would
/// let those two drift apart, and the pair only means anything while they agree on what the
/// vocabulary is.
/// </para>
/// </remarks>
internal static class DeploymentVariables
{
    /// <summary>The names, in order.</summary>
    /// <returns>Every <c>PARLEY_</c> variable the server or the operator's tool reads.</returns>
    /// <exception cref="Xunit.Sdk.TrueException">The reflection query found almost nothing.</exception>
    public static IReadOnlyList<string> All()
    {
        var names = new[]
            {
                typeof(ServerOptions),
                typeof(RetentionPolicyEnvironment),
                typeof(DeploymentEnvironment),
                typeof(BrokerEndpoint),

                // The node's, which the setup command documents because a person editing
                // node.json needs them. They were missing while nothing outside the adapter
                // read a variable at all, and their absence made the "only variables a server
                // reads" check refuse two names the plugin was right to mention.
                typeof(MachineName),
                typeof(Parley.Configuration.NodeCatalogue),

                // The manifest's, read by all three processes rather than by any one of them.
                // See Machine() for why that makes it a fourth set instead of a fourth entry in
                // one of the existing three.
                typeof(Parley.Configuration.ProjectManifest)
            }
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(v => v.StartsWith("PARLEY_", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToArray();

        // Guarded, because a reflection query that matched nothing would make every assertion
        // built on it pass against documentation that mentioned none of them.
        Assert.True(names.Length >= 5, $"only found {names.Length} variables; the constants moved");

        return names;
    }

    /// <summary>
    /// The variables an agent's launch configuration carries.
    /// </summary>
    /// <returns>The names an MCP server reads from its own environment.</returns>
    /// <remarks>
    /// The set split when the broker became a separate process, and the split is the point
    /// rather than an inconvenience for these tests. A launch configuration says what this
    /// repository is and where to find the broker; how often the sweep runs, how long the log is
    /// kept and how deep clarification may go describe the deployment, and a deployment has one
    /// of each. Setting them per repository meant a store could be swept on three cadences and
    /// pruned to two floors, with nothing anywhere to say which was in force.
    /// </remarks>
    public static IReadOnlyList<string> Adapter() =>
    [
        .. All().Where(v =>
            !Broker().Contains(v, StringComparer.Ordinal)
            && !Node().Contains(v, StringComparer.Ordinal)
            && !Machine().Contains(v, StringComparer.Ordinal))
    ];

    /// <summary>
    /// The variables this machine's node reads.
    /// </summary>
    /// <returns>The names `parley up` reads from its own environment.</returns>
    /// <remarks>
    /// A third set, because there is a third process. What a node reads describes the machine
    /// it runs on — which agents are here and how to start them — and belongs in neither an
    /// agent's launch configuration nor the broker's.
    /// </remarks>
    public static IReadOnlyList<string> Node() =>
    [
        Parley.Configuration.NodeCatalogue.PathVariable
    ];

    /// <summary>
    /// The variables that describe the machine rather than any process on it.
    /// </summary>
    /// <returns>The names every Parley process reads the same answer from.</returns>
    /// <remarks>
    /// A fourth set, and the first one that is not a process. Where the project manifest lives is
    /// a fact about the machine: the broker, the node and every adapter on it must resolve the
    /// same file or they are configured for different deployments while appearing to agree. So it
    /// belongs in none of the other three, and specifically not in a launch configuration —
    /// putting it in an agent's `.mcp.json` would invite one repository to point at a manifest of
    /// its own, which is the disagreement the manifest was introduced to end.
    /// </remarks>
    public static IReadOnlyList<string> Machine() =>
    [
        Parley.Configuration.ProjectManifest.PathVariable
    ];

    /// <summary>
    /// The variables the broker process reads.
    /// </summary>
    /// <returns>The names `parley serve` reads from its own environment.</returns>
    public static IReadOnlyList<string> Broker() =>
    [
        RetentionPolicyEnvironment.IdleVariable,
        RetentionPolicyEnvironment.MaximumAgeVariable,
        "PARLEY_TICK_SECONDS"
    ];
}
