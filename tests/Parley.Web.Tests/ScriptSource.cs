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
    /// <summary>The text of a file in the repository; for the layout, with the stylesheets it links.</summary>
    /// <param name="relative">The path from the repository's root.</param>
    /// <returns>The file's text.</returns>
    /// <remarks>
    /// The layout's styles were one inline block until feature 042 moved them into <c>Brand/tokens.css</c> and
    /// <c>Brand/components.css</c>, which the layout links. Checks of the frame's rules still read "the layout", so the
    /// layout is read with its stylesheets appended in the spelling the block had: Razor's <c>@@</c> for an at-rule, and
    /// the block's eight-space indent. Each check then holds the same rule it held, wherever the rule now lives.
    /// </remarks>
    public static string Page(string relative)
    {
        var text = File.ReadAllText(RepositoryPath(relative));

        return relative.EndsWith("Shared/_Layout.cshtml", StringComparison.Ordinal) ? text + Environment.NewLine + Styles() : text;
    }

    /// <summary>A served page with the stylesheets it links appended, each fetched from the same console.</summary>
    /// <param name="client">A client of the console that served the page.</param>
    /// <param name="page">The page as served.</param>
    /// <returns>The page, then each linked stylesheet as served.</returns>
    /// <remarks>
    /// A check of what a person's browser gets is a check of the page and its stylesheets together, so a rule is found
    /// whether it is inline or linked, and a linked stylesheet that is not served fails the fetch.
    /// </remarks>
    public static async Task<string> WithStylesAsync(HttpClient client, string page)
    {
        ArgumentNullException.ThrowIfNull(client);

        var served = new System.Text.StringBuilder(page);

        foreach (Match link in Regex.Matches(page, "<link rel=\"stylesheet\" href=\"([^\"]+)\"", RegexOptions.None, TimeSpan.FromSeconds(2)))
        {
            var href = System.Net.WebUtility.HtmlDecode(link.Groups[1].Value);

            _ = served.AppendLine().Append(await client.GetStringAsync(new Uri(href, UriKind.Relative), TestContext.Current.CancellationToken));
        }

        return served.ToString();
    }

    /// <summary>The console's stylesheets, in the spelling of the layout's former inline block.</summary>
    /// <returns>tokens.css then components.css, each line indented eight spaces, with <c>@</c> written <c>@@</c>.</returns>
    public static string Styles() =>
        string.Join(
            Environment.NewLine,
            new[] { "src/Parley.Web/Brand/tokens.css", "src/Parley.Web/Brand/components.css" }
                .SelectMany(sheet => File.ReadAllLines(RepositoryPath(sheet)))
                .Select(line => (line.Length == 0 ? line : "        " + line).Replace("@", "@@", StringComparison.Ordinal)));

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

    /// <summary>The whole body of the one function declared under a name in code, between its outer braces.</summary>
    /// <param name="code">The code, as <see cref="Code"/> gives it.</param>
    /// <param name="name">The function's name.</param>
    /// <returns>The body, trimmed, without its outer braces.</returns>
    /// <remarks>
    /// Pinning a statement leaves room around it: a line added before the guard, or a function that returns early, keeps
    /// the statement and defeats it. Pinning the whole body leaves none. Braces inside string literals are skipped.
    /// </remarks>
    public static string Body(string code, string name)
    {
        var at = Once(code, "function " + name + "(", "the function " + name + " is gone");
        var open = code.IndexOf('{', code.IndexOf(')', at));

        Assert.True(open >= 0, "the function " + name + " has no body");

        var depth = 0;
        char? quote = null;

        for (var i = open; i < code.Length; i++)
        {
            var c = code[i];

            if (quote is { } q)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == q)
                {
                    quote = null;
                }

                continue;
            }

            switch (c)
            {
                case '"' or '\'' or '`':
                    quote = c;
                    break;
                case '{':
                    depth++;
                    break;
                case '}' when --depth == 0:
                    return code[(open + 1)..i].Trim();
            }
        }

        Assert.Fail("the function " + name + " never closes");

        return string.Empty;
    }

    /// <summary>Every assignment to a variable in code, each from its name to the end of its statement.</summary>
    /// <param name="code">The code, as <see cref="Code"/> gives it.</param>
    /// <param name="variable">The variable's name.</param>
    /// <returns>Each assignment in the order written, such as <c>seen = note.getAttribute("data-digest")</c>.</returns>
    /// <remarks>
    /// A guard that compares against a variable is defeated by a second assignment anywhere, without touching the statement
    /// pinned, so a test holds the whole list. A property of the same name (<c>x.seen = …</c>) is not the variable, and a
    /// comparison is not an assignment.
    /// </remarks>
    public static IReadOnlyList<string> Assignments(string code, string variable)
    {
        var name = Regex.Escape(variable);
        var found = new List<string>();

        foreach (Match match in Regex.Matches(
                     code,
                     $@"(?<![\w$.]){name}\s*(?:[-+*/%&|^]|<<|>>>?|\?\?|&&|\|\|)?=(?!=)|(?<![\w$.])(?:\+\+|--){name}(?![\w$])|(?<![\w$.]){name}\s*(?:\+\+|--)",
                     RegexOptions.None,
                     TimeSpan.FromSeconds(2)))
        {
            var end = code.IndexOfAny([';', '}'], match.Index);

            found.Add((end < 0 ? code[match.Index..] : code[match.Index..end]).Trim());
        }

        return found;
    }

    public static string RepositoryPath(string relative)
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
