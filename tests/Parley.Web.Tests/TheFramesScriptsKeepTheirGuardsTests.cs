using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// Guards in the frame's own scripts: the timed reload, the tidying banner and an exchange reference's panel.
/// </summary>
/// <remarks>
/// There is no script engine in these tests, so each is read from the frame's source through <see cref="ScriptSource"/>,
/// with comments taken out and whitespace folded, and held to the exact condition the guard is, so taking the guard out,
/// or commenting it out behind a weaker line, fails here.
/// </remarks>
[Integration]
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
            """if \(document\.querySelector\("(?<selector>[^"]*)"\)\) \{ tick\(\); \} else \{ keep\(\); location\.replace\(""",
            RegexOptions.None,
            TimeSpan.FromSeconds(1));

        Assert.True(waits.Success, "the reload no longer waits on anything before it replaces the page");

        var selectors = waits.Groups["selector"].Value.Split(',', StringSplitOptions.TrimEntries);

        Assert.Contains("dialog[open]", selectors);
        Assert.Contains("details.view-create[open]", selectors);
        Assert.Contains("details.filters-fold[open]", selectors);

        // And the card it waits on is the one the pages draw: since feature 042 a page's create panel opens from the rail.
        var index = Page("src/Parley.Web/Pages/Index.cshtml");
        Assert.Contains("<details class=\"view-create rail-act", index, StringComparison.Ordinal);
        Assert.Contains("<details class=\"filters-fold\"", index, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tidying_banner_is_hidden_again_when_a_rewrite_ends()
    {
        // Without the guard the banner, once shown, stayed on screen saying a rewrite was under way after it had ended
        // (review, lens 3 round 34).
        var script = Script("document.querySelector('[data-tidying]')");

        Assert.Matches(new Regex(@"shown\.hidden = said\.length === 0;", RegexOptions.None, TimeSpan.FromSeconds(1)), script);
        Assert.DoesNotMatch(new Regex(@"shown\.hidden = (false|true)\b", RegexOptions.None, TimeSpan.FromSeconds(1)), script);

        // And nothing else sets it, so a second assignment beside the pinned one cannot undo it.
        Assert.Equal(["shown.hidden = said.length === 0"], ScriptSource.Assignments(script, "shown.hidden"));
    }

    [Fact]
    public void A_second_press_on_an_exchange_reference_closes_its_panel()
    {
        // Without the guard every press on "exchange #N" added another panel under the line, and none could be closed
        // (review, lens 3 round 34).
        var script = Script("button.exchange-ref");

        const string Closes =
            "var open = button.getAttribute(\"data-panel\"); " +
            "if (open) { " +
            "var shown = document.getElementById(open); " +
            "if (shown) { shown.remove(); } " +
            "button.removeAttribute(\"data-panel\"); " +
            "button.setAttribute(\"aria-expanded\", \"false\"); " +
            "return; " +
            "}";

        var closes = ScriptSource.Once(script, Closes, "a press on a reference with its panel open no longer closes the panel and stops");

        // And what the close reads is what the button recorded, with nothing set over it first.
        Assert.Equal(["open = button.getAttribute(\"data-panel\")"], ScriptSource.Assignments(script, "open"));

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

    [Fact]
    public void An_exchange_that_cannot_be_read_says_so_in_its_panel()
    {
        // Without the guard a refused or failed read put the error page's text in the panel, or left it saying "Reading
        // exchange #N…" for good (review, lens 3 round 37).
        var code = ScriptSource.Code(Layout, "button.exchange-ref");

        ScriptSource.Once(
            code,
            ".then(function (answer) { if (!answer.ok) { throw new Error(String(answer.status)); } return answer.text(); }) " +
            ".then(function (html) { panel.innerHTML = html; }) " +
            ".catch(function () { panel.textContent = \"Exchange #\" + number + \" could not be read. Press it again to close this.\"; });",
            "a failed read of an exchange no longer says it could not be read");
    }

    [Fact]
    public void An_exchange_panel_goes_after_the_line_its_reference_is_on()
    {
        // Without the guard the panel went after the button's direct parent, inside a line of bold or a link, splitting the
        // sentence the reference was in (review, lens 3 round 37).
        var code = ScriptSource.Code(Layout, "button.exchange-ref");

        ScriptSource.Once(
            code,
            "var line = button.closest(\"p, li, blockquote, td, th, h1, h2, h3, h4, h5, h6\") || button.parentElement;",
            "the panel's place is no longer the line the reference is on");
        ScriptSource.Once(code, "line.insertAdjacentElement(\"afterend\", panel);", "the panel no longer goes after its line");
    }

    [Fact]
    public void The_panels_line_is_chosen_in_one_place()
    {
        // Without this a later assignment to line, such as the button's direct parent again, put the panel inside a line of
        // bold or a link with the pinned statement untouched (review, lens 3 round 38).
        var code = ScriptSource.Code(Layout, "button.exchange-ref");

        Assert.Equal(
            ["line = button.closest(\"p, li, blockquote, td, th, h1, h2, h3, h4, h5, h6\") || button.parentElement"],
            ScriptSource.Assignments(code, "line"));
    }

    /// <summary>The one inline script in the frame whose code holds a piece of text, as code.</summary>
    /// <param name="holding">The text, which must be in the code and not only in a comment.</param>
    /// <returns>The script's code, comments out and whitespace folded.</returns>
    private static string Script(string holding) => ScriptSource.Code(Layout, holding);

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
