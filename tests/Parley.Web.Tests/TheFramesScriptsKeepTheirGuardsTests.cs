using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// Guards in the frame's own scripts: the timed reload, the tidying banner and an exchange reference's panel.
/// </summary>
/// <remarks>
/// There is no script engine in these tests, so each is read from the frame's source, as
/// <c>OneMessageViewAndOneComposerTests</c> reads it, and held to the exact condition the guard is, so taking the guard
/// out fails here.
/// </remarks>
[Unit]
public sealed class TheFramesScriptsKeepTheirGuardsTests
{
    private static string Layout =>
        File.ReadAllText(RepositoryPath("src/Parley.Web/Pages/Shared/_Layout.cshtml"));

    [Fact]
    public void The_timed_reload_waits_while_a_create_card_is_open()
    {
        // Without the guard the timer reloaded the page under somebody typing in an opened "create" card, and what they had
        // typed was gone (review, lens 3 round 34).
        var script = Script("meta[name=parley-reload]");

        var waits = Regex.Match(
            script,
            """if \(document\.querySelector\("(?<selector>[^"]*)"\)\) \{ tick\(\); \} else \{ location\.replace\(""",
            RegexOptions.None,
            TimeSpan.FromSeconds(1));

        Assert.True(waits.Success, "the reload no longer waits on anything before it replaces the page");

        var selectors = waits.Groups["selector"].Value.Split(',', StringSplitOptions.TrimEntries);

        Assert.Contains("dialog[open]", selectors);
        Assert.Contains("details.view-create[open]", selectors);

        // And the card it waits on is the one the pages draw.
        Assert.Contains("<details class=\"view-card view-create\"", Page("src/Parley.Web/Pages/Index.cshtml"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_tidying_banner_is_hidden_again_when_a_rewrite_ends()
    {
        // Without the guard the banner, once shown, stayed on screen saying a rewrite was under way after it had ended
        // (review, lens 3 round 34).
        var script = Script("document.querySelector('[data-tidying]')");

        Assert.Matches(new Regex(@"shown\.hidden = said\.length === 0;", RegexOptions.None, TimeSpan.FromSeconds(1)), script);
        Assert.DoesNotMatch(new Regex(@"shown\.hidden = (false|true)\b", RegexOptions.None, TimeSpan.FromSeconds(1)), script);
    }

    [Fact]
    public void A_second_press_on_an_exchange_reference_closes_its_panel()
    {
        // Without the guard every press on "exchange #N" added another panel under the line, and none could be closed
        // (review, lens 3 round 34).
        // Read with its whitespace folded to single spaces, because how the source is indented is not what it does.
        var script = Regex.Replace(Script("button.exchange-ref"), @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1));

        const string Closes =
            "var open = button.getAttribute(\"data-panel\"); " +
            "if (open) { " +
            "var shown = document.getElementById(open); " +
            "if (shown) { shown.remove(); } " +
            "button.removeAttribute(\"data-panel\"); " +
            "button.setAttribute(\"aria-expanded\", \"false\"); " +
            "return; " +
            "}";

        var closes = script.IndexOf(Closes, StringComparison.Ordinal);

        Assert.True(closes >= 0, "a press on a reference with its panel open no longer closes the panel and stops");

        // The close comes before a panel is made, and the panel made is the one a second press reads back.
        var opens = script.IndexOf("button.setAttribute(\"data-panel\", panel.id);", StringComparison.Ordinal);

        Assert.True(opens > closes, "the panel a press opens is not recorded where a second press looks for it");
    }

    [Fact]
    public void A_thumbnail_that_will_not_load_is_replaced_by_its_tile()
    {
        // Without the guard the tile was added beside the broken thumbnail rather than in its place, so the broken picture
        // stayed on the page, still a link to bytes the console refuses (review, lens 3 round 35).
        // Read with comments taken out and whitespace folded to single spaces, because neither is what the script does.
        var viewer = Page("src/Parley.Web/Pages/Conversations/_Viewer.cshtml");
        var script = Assert.Single(
            Regex.Matches(viewer, @"<script>(?<body>.*?)</script>", RegexOptions.Singleline, TimeSpan.FromSeconds(5)),
            match => match.Groups["body"].Value.Contains("thumb-tile", StringComparison.Ordinal)).Groups["body"].Value;
        var code = Regex.Replace(script, @"/\*[\s\S]*?\*/|(?<![:""'])//[^\n]*", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(2));
        code = Regex.Replace(code, @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1));

        const string Replaces =
            "document.addEventListener(\"error\", function (e) { " +
            "var img = e.target; " +
            "if (!img || img.tagName !== \"IMG\" || !img.closest || !img.closest(\"a.thumb\")) { return; } " +
            "var link = img.closest(\"a.thumb\"); " +
            "var tile = document.createElement(\"p\"); " +
            "tile.className = \"thumb-tile\"; " +
            "tile.textContent = (img.alt || \"This file\") + \": not a readable image.\"; " +
            "link.replaceWith(tile); " +
            "}, true);";

        Assert.True(code.Contains(Replaces, StringComparison.Ordinal), "a thumbnail that fails to load is no longer replaced by its tile");
    }

    /// <summary>The one inline script in the frame that holds a piece of text.</summary>
    /// <param name="holding">The text.</param>
    /// <returns>The script's body.</returns>
    private static string Script(string holding)
    {
        var scripts = Regex.Matches(Layout, @"<script>(?<body>.*?)</script>", RegexOptions.Singleline, TimeSpan.FromSeconds(5))
            .Select(match => match.Groups["body"].Value)
            .Where(body => body.Contains(holding, StringComparison.Ordinal))
            .ToList();

        return Assert.Single(scripts);
    }

    private static string Page(string relative) => File.ReadAllText(RepositoryPath(relative));

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
