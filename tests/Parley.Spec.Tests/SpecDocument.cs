using System.Text.RegularExpressions;

namespace Parley.Spec.Tests;

/// <summary>
/// SPEC.md, loaded once and parsed into the pieces the checks need.
/// </summary>
/// <remarks>
/// CLAUDE.md calls SPEC.md the primary artifact of this repository, which makes it the one
/// file with no automated gate: the build checks the code, and nothing checked the document.
/// Every defect these tests look for is one that actually reached a merged commit — a section
/// reference to a heading that had been renumbered, a schema fragment that no longer executed,
/// two operations sharing a name across the boundary §10.2 warns about. Reviews caught them
/// only sometimes, and a check that runs only sometimes is a convention.
/// </remarks>
internal static class SpecDocument
{
    private static readonly Lazy<string> Content = new(Load);

    /// <summary>The full text of SPEC.md.</summary>
    public static string Text => Content.Value;

    private static string Load()
    {
        // Walk up from the test assembly rather than assuming a working directory: the test
        // host's current directory differs between `dotnet test`, an IDE runner, and CI.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "SPEC.md");
            if (File.Exists(candidate))
            {
                // Line endings are normalised on load. This file is checked out with CRLF on
                // Windows and LF on the Linux and macOS CI runners, and .NET anchors `$` in
                // multiline mode before a bare line feed only — so a pattern anchored to
                // end-of-line silently matches nothing on one platform and everything on the
                // others. A check that passes on two runners of three is worse than no check.
                return File.ReadAllText(candidate)
                    .Replace("\r\n", "\n", StringComparison.Ordinal);
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"SPEC.md not found in any directory above {AppContext.BaseDirectory}.");
    }

    /// <summary>The wire contract, as text.</summary>
    /// <remarks>
    /// Read as a document rather than through <c>Parley.Transport</c>, which keeps this project's
    /// charter intact: it references neither Core nor Store precisely so a check here cannot drift
    /// into asserting what the code does. The <c>.proto</c> is the contract another implementation
    /// reads, so checking the file is checking the contract.
    /// </remarks>
    public static string Proto => ProtoContent.Value;

    private static readonly Lazy<string> ProtoContent = new(() => LoadBeside("proto/parley.proto"));

    private static string LoadBeside(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate).Replace("\r\n", "\n", StringComparison.Ordinal);
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"{relative} not found in any directory above {AppContext.BaseDirectory}.");
    }

    /// <summary>Numbered headings, as the strings a section reference would use.</summary>
    public static IReadOnlySet<string> Headings { get; } =
        Regex.Matches(Text, @"^#{2,4} (\d+(?:\.\d+)?)[.\s]", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Every distinct section reference in the prose.</summary>
    public static IReadOnlyList<string> SectionReferences { get; } =
        Regex.Matches(Text, @"§(\d+(?:\.\d+)?)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Every DDL statement in the document, in the order it appears.
    /// </summary>
    /// <remarks>
    /// Two shapes, because a trigger cannot use the same terminator as everything else. A
    /// table or index ends at its first semicolon at end-of-line; a trigger body contains
    /// several of those and ends only at <c>END;</c>. Matching triggers with the general
    /// pattern truncates them mid-body into something that still parses as SQL, which is how
    /// this test first passed against a fragment nobody wrote.
    /// </remarks>
    public static IReadOnlyList<string> SchemaFragments { get; } =
        Regex.Matches(
                Text,
                @"^CREATE TRIGGER\b.*?^END;$|^CREATE (?:TABLE|UNIQUE INDEX|INDEX)\b.*?;$",
                RegexOptions.Singleline | RegexOptions.Multiline)
            .Select(m => m.Value)
            .ToList();

    /// <summary>The name each fragment creates, paired with its statement.</summary>
    public static IReadOnlyList<(string Kind, string Name, string Sql)> SchemaObjects { get; } =
        SchemaFragments
            .Select(sql =>
            {
                var m = Regex.Match(sql, @"^CREATE (TABLE|TRIGGER|UNIQUE INDEX|INDEX) (\w+)");
                return (Kind: m.Groups[1].Value, Name: m.Groups[2].Value, Sql: sql);
            })
            .ToList();

    /// <summary>Markdown tables, as lists of their rows.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Tables { get; } =
        Regex.Matches(Text, @"(?:^\|.*\n)+", RegexOptions.Multiline)
            .Select(m => (IReadOnlyList<string>)m.Value
                .Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .ToList();

    /// <summary>The text of one numbered section, up to the next heading of the same depth.</summary>
    public static string Section(string number)
    {
        var start = Regex.Match(Text, $@"^#{{2,4}} {Regex.Escape(number)}[.\s].*$", RegexOptions.Multiline);
        if (!start.Success)
        {
            throw new InvalidOperationException($"No section {number} in SPEC.md.");
        }

        var depth = start.Value.TakeWhile(ch => ch == '#').Count();
        var rest = Text[(start.Index + start.Length)..];
        var next = Regex.Match(rest, $@"^#{{2,{depth}}} ", RegexOptions.Multiline);

        return next.Success ? rest[..next.Index] : rest;
    }
}
