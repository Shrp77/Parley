using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>That no page prints a stored time in UTC while the person reads it as their own.</summary>
/// <remarks>
/// Stored times are UTC, and formatting one straight to text prints the UTC clock with nothing saying so: the
/// conversation list showed 11:38 beside a page stamped 06:44. A time goes through <c>TimeZoneInfo.ConvertTime</c>
/// (or <c>LocalTime</c>) first. The only formats allowed directly are those that say they are UTC: <c>u</c>, which ends
/// in Z, and <c>O</c>/<c>o</c>, which carry the offset.
/// </remarks>
[Integration]
public sealed class TimesAreLocalTests
{
    private static readonly Regex Direct = new(
        """\b\w*Utc\??\.ToString\("(?![uoO]")""", RegexOptions.None, TimeSpan.FromSeconds(5));

    [Fact]
    public void No_page_formats_a_stored_time_without_converting_it()
    {
        var pages = Directory.GetFiles(RepositoryPath("src/Parley.Web"), "*.cshtml", SearchOption.AllDirectories);

        // Non-vacuity: the pages are where they are expected to be.
        Assert.Contains(pages, page => page.EndsWith("_Conversation.cshtml", StringComparison.Ordinal));

        var found = pages
            .SelectMany(page => File.ReadAllLines(page).Select((line, at) => (page, at, line)))
            .Where(entry => Direct.IsMatch(entry.line))
            .Select(entry => $"{Path.GetFileName(entry.page)}:{entry.at + 1}: {entry.line.Trim()}")
            .ToList();

        Assert.True(found.Count == 0, "these print a UTC time as if it were local:\n" + string.Join("\n", found));
    }

    /// <summary>Finds a path in the repository from wherever the tests run.</summary>
    /// <param name="relative">The path, relative to the repository root.</param>
    /// <returns>The absolute path.</returns>
    private static string RepositoryPath(string relative)
    {
        var directory = AppContext.BaseDirectory;

        while (directory is { Length: > 0 }
               && !File.Exists(Path.Combine(directory, "Parley.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);

        return Path.Combine(directory!, relative);
    }
}
