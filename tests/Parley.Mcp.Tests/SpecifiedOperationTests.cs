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
/// That is the failure this closes, and it is a real one rather than a tidiness argument: sessions
/// pointed at the server would correct the tool names in their own summaries. A specification whose operation names cannot
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

    /// <summary>SPEC.md, normalised and located from the test assembly.</summary>
    /// <returns>Its text.</returns>
    private static string Specification() => RepositoryFile.Read("SPEC.md");

    /// <summary>The operation names in one section's table.</summary>
    /// <param name="section">The heading to read, as it appears in the document.</param>
    /// <returns>The names, without their parameter lists.</returns>
    /// <remarks>
    /// The section ends at the next heading of any depth rather than at a named one. Bounding
    /// §10.2 by <c>## 11</c> would have silently swallowed a §10.3 into it, and the operations
    /// of a section that does not exist yet would then have been read as operator operations —
    /// which is the boundary the whole file is about.
    /// </remarks>
    private static IReadOnlyList<string> Operations(string section)
    {
        var text = Specification();
        var start = text.IndexOf(section, StringComparison.Ordinal);

        Assert.True(start >= 0, $"SPEC.md has no heading '{section}'");

        var after = Regex.Match(text[(start + section.Length)..], @"^#{2,4} ", RegexOptions.Multiline);

        Assert.True(after.Success, $"'{section}' is not followed by another heading");

        var body = text.Substring(start + section.Length, after.Index);

        var names = Regex.Matches(body, @"^\|\s*`([a-z_]+)\(", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToArray();

        // The extraction is guarded twice, because a pattern that silently matches nothing makes
        // every assertion below pass by vacuity — which is how the schema checks in
        // Parley.Spec.Tests were once green while testing nothing at all.
        //
        // Once that anything parsed, and once that everything did: a table row whose name did not
        // match would otherwise drop one operation quietly, and dropping one is exactly how a
        // tool and its specification come to disagree without failing.
        var rows = Regex.Matches(body, @"^\|\s*`", RegexOptions.Multiline).Count;

        Assert.True(names.Length > 0, $"no operations parsed from '{section}'; the table shape changed");
        Assert.Equal(rows, names.Length);

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
        var expected = Operations("### 10.1")
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
        var operatorOperations = Operations("### 10.2")
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
            ["10.1"] = Operations("### 10.1"),
            ["10.2"] = Operations("### 10.2")
        };

        // Both apostrophes, because the document is written in prose and a typographic one is a
        // keystroke away; and up to three characters of gap, because \s covers the newline this
        // document's line wrapping will eventually put between the section and the name. A
        // pattern that required exactly one space would stop covering anything the first time a
        // sentence grew by a word, and would pass while doing it.
        //
        // Three characters, and not more, is what keeps it to attributions. "§10.1's `inbox`" and
        // "§10.1 `ask`" fit; "§10.1 and the `messages` table" does not, and must not, because a
        // table is not an operation and failing on one would be a false alarm against ordinary
        // prose.
        var references = Regex.Matches(text, @"§(10\.[12])(?:['\u2019]s)?\s{0,3}`([a-z_]+)`")
            .Select(m => (Section: m.Groups[1].Value, Name: m.Groups[2].Value))
            .ToArray();

        var wrong = references
            .Where(r => !known[r.Section].Contains(r.Name, StringComparer.Ordinal))
            .Select(r => $"§{r.Section} has no operation '{r.Name}'")
            .ToArray();

        Assert.Empty(wrong);
    }

    [Fact]
    public void The_check_above_is_reading_a_document_that_names_those_sections()
    {
        // What this guards is the scanning, not the prose. The check above is conditional by
        // nature — if nothing attributes a name to §10.1 or §10.2 it has nothing to say — and a
        // conditional check whose condition silently stopped matching would be green while
        // covering nothing at all.
        //
        // So it asserts the fragile half works: that "§10.1" and "§10.2" are findable in the
        // loaded text. That is what breaks if the section symbol is mangled by an encoding, if
        // the sections are renumbered, or if the file fails to load — and it does not pin any
        // one sentence, so rewording the prose cannot fail the build.
        var text = Specification();

        Assert.True(
            Regex.Matches(text, @"§10\.[12]").Count >= 5,
            "SPEC.md barely mentions §10.1 or §10.2; the reference check is scanning nothing");
    }

    [Fact]
    public void The_protocol_version_the_code_reports_is_the_one_the_document_declares()
    {
        // Three numbers live in this repository and only one of them is the protocol's. The
        // packages carry a version that moves when anything ships; the storage schema carries
        // user_version; and this is what another implementation builds against, which is why the
        // code has to be able to state it without SPEC.md beside it.
        //
        // Stating it from a constant means the constant can be wrong, and a broker announcing a
        // protocol version it does not implement is worse than one that announces nothing. So
        // the two are bound: the document's header is the authority and this fails when the code
        // disagrees with it.
        var declared = Regex.Match(Specification(), @"^\*\*Version:\*\* (\d+\.\d+)", RegexOptions.Multiline);

        Assert.True(declared.Success, "SPEC.md no longer declares a version in its header");
        Assert.Equal(Parley.Core.ProtocolVersion.Current, declared.Groups[1].Value);
    }

    [Fact]
    public void The_protocol_version_has_no_patch_level()
    {
        // A protocol has no patch level: either a change is visible to something on the other
        // side of the boundary or it is not a protocol change. A third number here would invite
        // the packages' version to be copied in, which is the confusion the separate constant
        // exists to prevent.
        Assert.Matches(@"^\d+\.\d+$", Parley.Core.ProtocolVersion.Current);
    }
}
