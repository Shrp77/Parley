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
                typeof(DeploymentEnvironment)
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
}
