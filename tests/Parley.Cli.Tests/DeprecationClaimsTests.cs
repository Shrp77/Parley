using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That nothing a person reads today says a removed spelling still works.
/// </summary>
/// <remarks>
/// Enumerates every current document rather than sampling, because a fix is a claim about every place.
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
}
