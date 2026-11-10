using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>A page's inline scripts read as code: comments taken out and whitespace folded to single spaces.</summary>
/// <remarks>
/// There is no script engine in these tests, so a script's guard is held by reading its source. Neither a comment nor how
/// the source is indented is what a script does, so both go before a statement is matched, and a guard that is commented
/// out no longer counts as there.
/// </remarks>
internal static class ScriptSource
{
    /// <summary>The text of a file in the repository.</summary>
    /// <param name="relative">The path from the repository's root.</param>
    /// <returns>The file's text.</returns>
    public static string Page(string relative) => File.ReadAllText(RepositoryPath(relative));

    /// <summary>The one inline script in a page whose code holds a piece of text, as code.</summary>
    /// <param name="page">The page's source.</param>
    /// <param name="holding">The text, which must be in the code and not only in a comment.</param>
    /// <returns>The script's code, comments out and whitespace folded.</returns>
    public static string Code(string page, string holding)
    {
        var scripts = Regex.Matches(page, @"<script>(?<body>.*?)</script>", RegexOptions.Singleline, TimeSpan.FromSeconds(5))
            .Select(match => Fold(match.Groups["body"].Value))
            .Where(code => code.Contains(holding, StringComparison.Ordinal))
            .ToList();

        return Assert.Single(scripts);
    }

    /// <summary>Script source with its comments taken out and its whitespace folded to single spaces.</summary>
    /// <param name="script">The source.</param>
    /// <returns>The code.</returns>
    public static string Fold(string script)
    {
        var code = Regex.Replace(script, @"/\*[\s\S]*?\*/|(?<![:""'])//[^\n]*", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(2));

        return Regex.Replace(code, @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(2));
    }

    /// <summary>The terms of a condition joined by one operator, as a set.</summary>
    /// <param name="condition">The condition.</param>
    /// <param name="joiner">The operator, <c>||</c> or <c>&amp;&amp;</c>.</param>
    /// <returns>Each term, trimmed.</returns>
    public static HashSet<string> Terms(string condition, string joiner) =>
        condition.Split(joiner, StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);

    /// <summary>Where a statement is in code, failing when it is absent or there more than once.</summary>
    /// <param name="code">The code.</param>
    /// <param name="statement">The statement, whitespace folded.</param>
    /// <param name="missing">What it means that the statement is gone.</param>
    /// <returns>Its index.</returns>
    public static int Once(string code, string statement, string missing)
    {
        var at = code.IndexOf(statement, StringComparison.Ordinal);

        Assert.True(at >= 0, missing);
        Assert.True(code.IndexOf(statement, at + 1, StringComparison.Ordinal) < 0, "the statement is there more than once: " + statement);

        return at;
    }

    private static string RepositoryPath(string relative)
    {
        var directory = AppContext.BaseDirectory;

        while (directory is { Length: > 0 } && !File.Exists(Path.Combine(directory, "Parley.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);

        return Path.Combine(directory!, relative);
    }
}
