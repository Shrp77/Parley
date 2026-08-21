using System.Reflection;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// That the tool set is the operations SPEC.md §10.1 names, and named the same way.
/// </summary>
/// <remarks>
/// <para>
/// <c>ToolSurfaceTests</c> pins the tool set against a list written in that file, which stops it
/// growing by accident but cannot notice the document and the adapter drifting apart. They did.
/// SPEC.md referred to <c>§10.1's get_messages</c> for a while; the operation is <c>inbox</c>
/// and there has never been a tool by that name. Nothing failed, because nothing related the
/// two — and an agent given the document as its brief calls what the document says.
/// </para>
/// <para>
/// That is the failure this closes, and it is a real one rather than a tidiness argument: it was
/// found by installing the server, pointing two sessions at it, and watching both of them
/// correct the tool names in their own summaries. A specification whose operation names cannot
/// be called is worse than one that omits them.
/// </para>
/// <para>
/// The check runs here rather than in <c>Parley.Spec.Tests</c> deliberately. That project
/// references neither Core nor Store, so a check there cannot drift into asserting what the code
/// does; this is the other direction — the adapter measured against the document — and that
/// belongs beside the adapter.
/// </para>
/// </remarks>
public sealed class SpecifiedOperationTests
{
    /// <summary>The prefix every agent-facing tool carries.</summary>
    /// <remarks>
    /// An MCP client presents tools from every server it has launched in one flat namespace, so
    /// the prefix is what stops <c>ask</c> colliding with somebody else's <c>ask</c>. It is the
    /// only difference permitted between an operation's name and its tool's.
    /// </remarks>
    private const string Prefix = "parley_";

    /// <summary>SPEC.md, located by walking up from the test assembly.</summary>
    /// <remarks>
    /// Line endings are normalised on load, for the reason <c>Parley.Spec.Tests</c> records: the
    /// file is CRLF on Windows and LF on the Linux and macOS runners, and .NET anchors <c>$</c>
    /// before a bare line feed only. A pattern anchored to end-of-line otherwise matches nothing
    /// on one platform of three, and a check that passes on two runners is worse than no check.
    /// </remarks>
    private static string Specification()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "SPEC.md");

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate).Replace("\r\n", "\n", StringComparison.Ordinal);
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"SPEC.md not found in any directory above {AppContext.BaseDirectory}.");
    }

    /// <summary>The operation names in one section's table.</summary>
    /// <param name="section">The heading to read, as it appears in the document.</param>
    /// <param name="next">The heading that ends it.</param>
    /// <returns>The names, without their parameter lists.</returns>
    private static IReadOnlyList<string> Operations(string section, string next)
    {
        var text = Specification();
        var start = text.IndexOf(section, StringComparison.Ordinal);

        Assert.True(start >= 0, $"SPEC.md has no heading '{section}'");

        var end = text.IndexOf(next, start, StringComparison.Ordinal);

        Assert.True(end > start, $"SPEC.md has no heading '{next}' after '{section}'");

        var names = Regex.Matches(text[start..end], @"^\|\s*`([a-z_]+)\(", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToArray();

        // The extraction itself is guarded, because a pattern that silently matches nothing
        // makes every assertion below pass by vacuity — which is how the schema checks in
        // Parley.Spec.Tests were once green while testing nothing at all.
        Assert.True(names.Length > 0, $"no operations parsed from '{section}'; the table shape changed");

        return names;
    }

    private static IEnumerable<string> ToolNames() =>
        typeof(ParleyTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                        BindingFlags.DeclaredOnly)
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.Name!);

    [Fact]
    public void Every_operation_of_section_10_1_is_a_tool_of_the_same_name()
    {
        var expected = Operations("### 10.1", "### 10.2")
            .Select(name => Prefix + name)
            .OrderBy(n => n, StringComparer.Ordinal);

        Assert.Equal(expected, ToolNames().OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void No_operator_operation_is_named_as_a_tool()
    {
        // The §10.2 boundary, checked against the document rather than against a list of names
        // somebody remembered to keep current. force_conclude is deliberately not conclude, and
        // this fails if a tool ever carries an operator operation's name — including one added
        // to §10.2 after this was written.
        var operatorOperations = Operations("### 10.2", "## 11")
            .Select(name => Prefix + name)
            .ToHashSet(StringComparer.Ordinal);

        var leaked = ToolNames().Where(operatorOperations.Contains).ToArray();

        Assert.Empty(leaked);
    }

    [Fact]
    public void The_document_refers_to_operations_by_names_that_exist()
    {
        // The specific defect. Prose that attributes a name to §10.1 or §10.2 is prose an agent
        // reads as an instruction about what to call, so the name has to be one of that
        // section's operations.
        var text = Specification();

        var known = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["10.1"] = Operations("### 10.1", "### 10.2"),
            ["10.2"] = Operations("### 10.2", "## 11")
        };

        var references = Regex.Matches(text, @"§(10\.[12])(?:'s)? `([a-z_]+)`")
            .Select(m => (Section: m.Groups[1].Value, Name: m.Groups[2].Value))
            .ToArray();

        var wrong = references
            .Where(r => !known[r.Section].Contains(r.Name, StringComparer.Ordinal))
            .Select(r => $"§{r.Section} has no operation '{r.Name}'")
            .ToArray();

        Assert.Empty(wrong);
    }
}
