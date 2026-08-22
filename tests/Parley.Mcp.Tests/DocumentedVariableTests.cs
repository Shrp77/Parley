using System.Reflection;
using System.Text.RegularExpressions;
using Parley.Core;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// That the README names every environment variable a server reads.
/// </summary>
/// <remarks>
/// <para>
/// A launch configuration is the whole of a deployment's setup, and the README's example of one
/// is where every deployment starts. It listed five variables; the server reads seven. The two
/// missing ones were the retention pair, which appeared only under the operator's <c>prune</c>
/// command and therefore read as an operator's concern — while in fact the servers are what
/// apply the policy, on every sweep, by deleting message text.
/// </para>
/// <para>
/// So a deployment that set them where the README implied got the default policy enforced
/// continuously by its agents while <c>parley prune</c> reported the numbers the operator meant.
/// Nothing failed; the log was simply shorter than intended, which is not a symptom anyone
/// notices until they go looking for a discussion that is gone.
/// </para>
/// <para>
/// The same shape as <c>SpecifiedOperationTests</c>, and for the same reason: documentation that
/// tells an agent or an operator what to type is documentation the code can be measured against.
/// This one only asserts that each variable is mentioned somewhere, which is deliberately weak —
/// it cannot judge whether the prose around it is right, and pinning phrasing would fail the
/// build every time somebody improved a sentence.
/// </para>
/// </remarks>
public sealed class DocumentedVariableTests
{
    /// <summary>The README, normalised and located from the test assembly.</summary>
    /// <returns>Its text.</returns>
    private static string Readme() => RepositoryFile.Read("README.md");

    /// <summary>Every variable name the server or the operator's tool reads.</summary>
    /// <returns>The names, from the constants that define them.</returns>
    /// <remarks>
    /// Read from the types rather than from a list written here, so a variable added to a launch
    /// configuration is documented or the build says so. A list would be one more thing to keep
    /// current, which is the failure this exists to catch.
    /// </remarks>
    private static IReadOnlyList<string> Variables()
    {
        var names = new[] { typeof(ServerOptions), typeof(RetentionPolicyEnvironment), typeof(DeploymentEnvironment) }
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(f => f is { IsLiteral: true, FieldType: { } type } && type == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(v => v.StartsWith("PARLEY_", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToArray();

        // Guarded, because a reflection query that matched nothing would make the assertion below
        // pass against a README that documented none of them.
        Assert.True(names.Length >= 5, $"only found {names.Length} variables; the constants moved");

        return names;
    }

    [Fact]
    public void Every_variable_a_server_reads_appears_in_the_readme()
    {
        var readme = Readme();

        var undocumented = Variables()
            .Where(v => !readme.Contains(v, StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(undocumented);
    }

    [Fact]
    public void The_launch_configuration_example_is_the_whole_set()
    {
        // Mentioned somewhere is not the same as shown where a deployment is configured. The
        // retention pair was documented — under the operator's command, which is exactly the
        // place that made it look like somebody else's problem.
        var readme = Readme();
        var example = Regex.Match(readme, @"""mcpServers"".*?\n```", RegexOptions.Singleline);

        Assert.True(example.Success, "the README no longer shows a launch configuration");

        var missing = Variables()
            .Where(v => !example.Value.Contains(v, StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(missing);
    }
}
