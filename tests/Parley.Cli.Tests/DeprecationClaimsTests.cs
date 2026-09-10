using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Every statement in the repository about what a version removed, checked against this version.
/// </summary>
/// <remarks>
/// <para>
/// Review gate, lens 3, run 7. Runs 5 and 6 each found one of these and fixed one of these: the
/// README said removed flags still work and named 0.21.0, a guard's exemptions were the wording of a
/// note this release deletes, and CLAUDE.md's architecture section described a verb removed three
/// releases ago. That is one seam, not three findings — **the deprecation date moved twice**, from
/// 0.21.0 to 0.22.0, and every sentence written against the old plan went stale together.
/// </para>
/// <para>
/// So this enumerates rather than samples, which is what lens 2 had to learn four times: a fix is a
/// claim about every place, and finding them one run at a time is how the last one ships.
/// </para>
/// <para>
/// <b>The historical specs are exempt by path and that is not a loophole.</b>
/// <c>specs/001</c>–<c>specs/008</c> are records of what was decided when, and 007 genuinely did say
/// 0.21.0 — editing that would destroy the evidence that the date moved, which 008's own record calls
/// the thing worth keeping. What must be true today is what a person reads today.
/// </para>
/// </remarks>
public sealed class DeprecationClaimsTests
{
    private static readonly string Root =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    /// <summary>The version this build is, taken from the file that decides it.</summary>
    private static string Current()
    {
        var props = File.ReadAllText(Path.Combine(Root, "Directory.Build.props"));

        var match = Regex.Match(props, @"<VersionPrefix>([^<]+)</VersionPrefix>");

        Assert.True(match.Success, "no VersionPrefix in Directory.Build.props");

        return match.Groups[1].Value;
    }

    /// <summary>Everything a person reads today, which is not everything in the repository.</summary>
    private static IEnumerable<(string Path, string Text)> CurrentDocuments()
    {
        var files = new List<string>
        {
            Path.Combine(Root, "README.md"),
            Path.Combine(Root, "CLAUDE.md"),
            Path.Combine(Root, "install.ps1"),
        };

        files.AddRange(Directory.EnumerateFiles(
            Path.Combine(Root, "plugins"), "*.md", SearchOption.AllDirectories));

        foreach (var file in files.Where(File.Exists))
        {
            var text = File.ReadAllText(file).ReplaceLineEndings("\n");

            // **CLAUDE.md is two documents in one file and only one of them is current.** `## Layout`
            // describes what the code *is*; everything after it, under "Active plan" and "Previous",
            // records what was decided and when — and 007 genuinely did say 0.21.0, so editing that
            // would destroy the evidence that the date moved, which 008's own record calls the thing
            // worth keeping. Every stale paragraph this run found was in the first half, which is the
            // half somebody reads to learn how the thing works today.
            if (file.EndsWith("CLAUDE.md", StringComparison.Ordinal))
            {
                var layout = text.IndexOf("\n## Layout", StringComparison.Ordinal);
                var after = text.IndexOf("\n## ", layout + 1, StringComparison.Ordinal);

                Assert.True(layout >= 0 && after > layout, "CLAUDE.md has no ## Layout section");

                text = text[layout..after];
            }

            yield return (Path.GetRelativePath(Root, file), text);
        }
    }

    [Fact]
    public void The_walk_finds_the_documents_it_is_about()
    {
        // The control. Every assertion below is an absence, and an empty walk satisfies all of them.
        var found = CurrentDocuments().Select(document => document.Path).ToArray();

        Assert.Contains("README.md", found);
        Assert.Contains("CLAUDE.md", found);
        Assert.True(found.Length >= 4, $"only {found.Length} documents; the walk is wrong");
    }

    [Fact]
    public void Nothing_a_person_reads_says_a_removed_spelling_still_works()
    {
        // The generalisation of run 5's finding. Not one flag and one document — every sentence in
        // everything current that puts a removed spelling beside a claim that it works.
        string[] removed = ["--with-console", "--no-console", "parley console"];

        var offenders = new List<string>();

        foreach (var (path, text) in CurrentDocuments())
        {
            foreach (var paragraph in text.Split("\n\n"))
            {
                if (!removed.Any(flag => paragraph.Contains(flag, StringComparison.Ordinal)))
                {
                    continue;
                }

                if (paragraph.Contains("still work", StringComparison.OrdinalIgnoreCase)
                    || paragraph.Contains("still runs", StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add(path);
                }
            }
        }

        Assert.True(offenders.Count == 0, "says a removed spelling still works: " + string.Join(", ", offenders));
    }

    [Fact]
    public void No_current_document_says_these_go_at_the_version_they_did_not_go_at()
    {
        // **0.21.0 is the date that moved**, and 008's record is explicit that it is a commitment
        // rather than a second reprieve. A current document naming it is describing a plan that was
        // changed, to somebody who has no way to know that.
        var offenders = new List<string>();

        foreach (var (path, text) in CurrentDocuments())
        {
            foreach (var paragraph in text.Split("\n\n"))
            {
                if (!paragraph.Contains("0.21.0", StringComparison.Ordinal))
                {
                    continue;
                }

                // A paragraph *recording* that the date moved has to be able to name both.
                if (paragraph.Contains("0.22.0", StringComparison.Ordinal)
                    || paragraph.Contains("moved", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                offenders.Add($"{path}: {paragraph.Trim()[..Math.Min(90, paragraph.Trim().Length)]}");
            }
        }

        Assert.True(offenders.Count == 0, "names the version the removal did not happen at:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void The_version_the_documents_claim_is_the_version_this_builds()
    {
        // The other direction: a document naming the removal version has to name *this* one, or the
        // person reading it cannot tell whether their build is before or after.
        var current = Current();

        var claims = CurrentDocuments()
            .SelectMany(document => document.Text.Split("\n\n").Select(part => (document.Path, part)))
            .Where(pair => pair.part.Contains("--with-console", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(claims);

        foreach (var (path, paragraph) in claims)
        {
            Assert.True(
                paragraph.Contains(current, StringComparison.Ordinal),
                $"{path} discusses the removed flags without naming {current}:\n{paragraph.Trim()}");
        }
    }
}
