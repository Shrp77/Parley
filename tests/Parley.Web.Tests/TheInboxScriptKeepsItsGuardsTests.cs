using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// Guards in the Inbox's own script, which asks every few seconds whether anything moved and reloads the page when it did.
/// </summary>
/// <remarks>
/// There is no script engine in these tests, so each guard is read from the page's source with its comments taken out and
/// its whitespace folded (<see cref="ScriptSource"/>), and held to the whole statement it is, so taking the guard out, or
/// keeping a fragment of it, fails here.
/// </remarks>
[Unit]
public sealed class TheInboxScriptKeepsItsGuardsTests
{
    private static string Inbox => ScriptSource.Page("src/Parley.Web/Pages/Inbox.cshtml");

    private static string Reload => ScriptSource.Code(Inbox, "inbox-changed");

    [Fact]
    public void No_reload_while_a_field_has_focus()
    {
        // Without the guard the page reloaded under somebody choosing in a list or typing in a field, and what they had
        // chosen was gone (review, lens 3 round 37).
        ScriptSource.Once(
            Reload,
            "var focused = document.activeElement; if (focused && focused.matches(\"input, textarea, select\")) { return true; }",
            "the reload no longer waits while a field has focus");
    }

    [Fact]
    public void No_reload_while_a_row_or_an_exchange_panel_is_open()
    {
        // Without the guard the page reloaded under somebody reading an opened row or an exchange's panel, and closed it
        // (review, lens 3 round 37).
        var open = Assert.Single(Regex.Matches(
            Reload,
            """if \(document\.querySelector\("(?<selector>[^"]*)"\)\) \{ return true; \}""",
            RegexOptions.None,
            TimeSpan.FromSeconds(1)));

        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { "dialog[open]", "details[open]", ".exchange-panel" },
            ScriptSource.Terms(open.Groups["selector"].Value, ","));

        // And they are what the pages draw: a row is a details, and the frame's panel is an exchange-panel.
        Assert.Contains("<details class=\"dgrid-row\"", Inbox, StringComparison.Ordinal);
        Assert.Contains(
            "panel.className = \"exchange-panel\";",
            ScriptSource.Code(ScriptSource.Page("src/Parley.Web/Pages/Shared/_Layout.cshtml"), "button.exchange-ref"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void No_reload_while_a_box_holds_typed_text()
    {
        // Without the guard the page reloaded once the box lost focus, and the words typed in it were gone (review, lens 3
        // round 37).
        ScriptSource.Once(
            Reload,
            "return Array.prototype.some.call(document.querySelectorAll(\"main textarea\"), function (box) { return box.value.trim() !== \"\"; });",
            "the reload no longer waits while a box holds typed text");
    }

    [Fact]
    public void A_reload_only_when_the_digest_changed()
    {
        // Without the guard the page reloaded every five seconds whether anything had moved or not (review, lens 3
        // round 37).
        ScriptSource.Once(Reload, "var seen = note.getAttribute(\"data-digest\");", "the digest drawn is no longer what is compared");
        ScriptSource.Once(
            Reload,
            "if (!said || said.digest === seen) { return; } " +
            "if (busy()) { note.hidden = false; } else { location.replace(note.getAttribute(\"data-url\")); }",
            "the page no longer reloads only when the digest changed, and waits while busy");
        Assert.Contains("data-digest=\"@Model.Digest\"", Inbox, StringComparison.Ordinal);
    }

    [Fact]
    public void No_asking_while_the_tab_is_hidden()
    {
        // Without the guard a tab nobody was looking at asked every five seconds, and a reload landed while it was out of
        // sight (review, lens 3 round 37).
        ScriptSource.Once(Reload, "function ask() { if (asking || document.hidden) { return; }", "the page asks while its tab is hidden");
        ScriptSource.Once(Reload, "document.addEventListener(\"visibilitychange\", ask);", "the page no longer asks when its tab is shown again");
    }
}
