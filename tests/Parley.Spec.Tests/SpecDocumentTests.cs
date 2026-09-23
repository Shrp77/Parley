using Xunit;

namespace Parley.Spec.Tests;

/// <summary>
/// Checks the reader itself.
/// </summary>
/// <remarks>
/// A checker nobody checks is the weakest thing in a repository: it reports green whether or
/// not it looked. The extraction has already been wrong twice in ways that produced passing
/// runs — a pattern that matched nothing on one platform, and one that truncated every trigger
/// mid-body — so the parsing gets the same treatment as the document it parses.
/// </remarks>
public sealed class SpecDocumentTests
{
    [Fact]
    public void The_last_section_is_read_to_the_end_of_the_file()
    {
        // Section() stops at the next heading of equal or shallower depth, and the final
        // section has none. Getting this wrong returns an empty string, which every check
        // reading that section would pass against.
        var last = SpecDocument.Section("17");

        Assert.Contains("Open questions", SpecDocument.Text, StringComparison.Ordinal);
        Assert.NotEmpty(last.Trim());
        Assert.Contains("1.", last, StringComparison.Ordinal);
    }

    [Fact]
    public void A_section_containing_a_deeper_heading_is_not_truncated_at_it()
    {
        // §5.3 carries a #### Blocked subheading. A depth test that treated any heading as a
        // boundary would cut the section there and silently hide everything after it -- so the
        // second assertion reads text that sits below that subheading.
        var section = SpecDocument.Section("5.3");

        Assert.Contains("#### Blocked", section, StringComparison.Ordinal);
        Assert.Contains("never means", section, StringComparison.Ordinal);
    }

    [Fact]
    public void A_section_stops_before_the_next_sibling()
    {
        var section = SpecDocument.Section("5.3");

        Assert.DoesNotContain("### 5.4", section, StringComparison.Ordinal);
    }

    [Fact]
    public void Line_endings_are_normalised()
    {
        // The whole extraction is anchored to end-of-line, and this file is CRLF on Windows.
        Assert.DoesNotContain('\r', SpecDocument.Text);
    }

    [Fact]
    public void Every_schema_fragment_is_captured_whole()
    {
        // The truncation that produced a passing run cut each trigger at its first internal
        // semicolon, leaving something that still read as SQL.
        foreach (var (kind, name, sql) in SpecDocument.SchemaObjects)
        {
            Assert.EndsWith(";", sql.TrimEnd(), StringComparison.Ordinal);

            if (string.Equals(kind, "TRIGGER", StringComparison.Ordinal))
            {
                Assert.True(sql.TrimEnd().EndsWith("END;", StringComparison.Ordinal),
                    $"trigger {name} was captured without its END, so it is truncated");
            }
        }
    }

    [Fact]
    public void Every_fragment_yields_a_kind_and_a_name()
    {
        // A fragment whose name fails to parse becomes an empty string, and the duplicate-name
        // check in SpecSchemaTests would then report every such fragment as one collision
        // rather than as unparsed — a failure that points at the document when the fault is
        // here.
        foreach (var (kind, name, sql) in SpecDocument.SchemaObjects)
        {
            Assert.False(string.IsNullOrWhiteSpace(kind), $"no kind parsed from: {Head(sql)}");
            Assert.False(string.IsNullOrWhiteSpace(name), $"no name parsed from: {Head(sql)}");
        }

        // Both index forms appear in the document and must survive extraction. Their relative
        // order in the pattern turns out not to matter, because INDEX cannot match text that
        // reads UNIQUE — verified rather than assumed, since the opposite is easy to believe.
        var names = SpecDocument.SchemaObjects.Select(o => o.Name).ToList();
        Assert.Contains("ux_open_request", names, StringComparer.Ordinal);
        Assert.Contains("ux_message_dedup", names, StringComparer.Ordinal);
    }

    private static string Head(string sql) =>
        sql.Split('\n')[0];
}
