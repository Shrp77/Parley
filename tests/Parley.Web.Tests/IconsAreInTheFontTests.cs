using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>That every icon a page names is one the console's icon font holds.</summary>
/// <remarks>
/// <para>
/// The font is Material Symbols cut down to the icons the console used when it was cut, and an icon is written as its
/// name, drawn as a ligature. A name the font does not hold draws its first letters, clipped to one em, which passes for
/// a glyph until somebody looks: 037's Media button showed a "P" for <c>photo_library</c>, a large image's tile an "I"
/// for <c>image</c>, and the box's fold an "E" for <c>expand_less</c>.
/// </para>
/// <para>
/// The list below is what the font was seen to draw (rendered and looked at, 2026-11-09). Reading the font itself needs a
/// Brotli decoder this project does not carry, so a new icon is added here only after it has been seen to draw, or the
/// font is cut again with it.
/// </para>
/// </remarks>
[Integration]
public sealed class IconsAreInTheFontTests
{
    private static readonly HashSet<string> InTheFont = new(StringComparer.Ordinal)
    {
        "add", "attach_file", "chat_bubble", "check", "chevron_left", "chevron_right", "close", "description", "download",
        "expand_more", "grid_view", "group", "inbox", "info", "map", "monitor_heart", "notifications", "open_in_new",
        "remove", "settings", "tune", "warning",
    };

    private static readonly Regex[] Named =
    [
        // <span class="icon" aria-hidden="true">name</span>
        new("""class="[^"]*\bicon\b[^"]*"[^>]*>\s*([a-z][a-z0-9_]*)\s*<""", RegexOptions.None, TimeSpan.FromSeconds(2)),

        // A pseudo-element's ligature: content: "name" / "";
        new(@"content:\s*""([a-z][a-z0-9_]*)""", RegexOptions.None, TimeSpan.FromSeconds(2)),

        // The rail's entries: (href, "Label", "icon", current)
        new("""\(\s*[^,()]+,\s*"[^"]*",\s*"([a-z][a-z0-9_]*)",""", RegexOptions.None, TimeSpan.FromSeconds(2)),

        // A script that sets an icon's text, whatever the variable is called (icon, mark, glyph).
        new(@"\b(?:icon|mark|glyph)\w*\.textContent\s*=\s*[^;]*?""([a-z][a-z0-9_]*)""", RegexOptions.None, TimeSpan.FromSeconds(2)),
    ];

    [Fact]
    public void Every_icon_a_page_names_is_in_the_font()
    {
        var pages = Directory.GetFiles(RepositoryPath("src/Parley.Web"), "*.cshtml", SearchOption.AllDirectories);
        var used = pages
            .SelectMany(page => Named.SelectMany(pattern => pattern.Matches(File.ReadAllText(page)))
                .Select(match => (Page: Path.GetFileName(page), Name: match.Groups[1].Value)))
            .ToList();

        // Non-vacuity: the scan finds icons in each form they are written in.
        Assert.Contains(used, one => one.Name == "attach_file");
        Assert.Contains(used, one => one.Name == "grid_view");
        Assert.Contains(used, one => one.Name == "expand_more");

        var missing = used.Where(one => !InTheFont.Contains(one.Name)).Select(one => $"{one.Page}: {one.Name}").Distinct().ToList();

        Assert.True(missing.Count == 0, "these draw clipped letters, not icons: " + string.Join(", ", missing));
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
