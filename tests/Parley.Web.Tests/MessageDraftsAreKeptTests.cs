using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>A message half written is kept in the browser until it is sent (ComposerBox).</summary>
/// <remarks>
/// <para>
/// <b>The owner, 2026-11-13:</b> a reply half written in the Inbox was gone after they answered another message first and
/// came back to it. "Preserve the input across reloads / navigation / whatever... clear on send."
/// </para>
/// <para>
/// There is no script engine in these tests, so the frame's draft script is read from source with its comments taken out
/// and its whitespace folded (<see cref="ScriptSource"/>), and each guard is held to the whole statement it is.
/// </para>
/// </remarks>
[Integration]
public sealed class MessageDraftsAreKeptTests
{
    private static string Drafts =>
        ScriptSource.Code(ScriptSource.Page("src/Parley.Web/Pages/Shared/_Layout.cshtml"), "var prefix = \"parley-draft:\";");

    [Fact]
    public void Every_message_box_is_drawn_with_a_draft_key()
    {
        // Drawn by what the box is, not by a list of pages: a box that takes files is a message box (SPEC.md §5.6), and
        // a message box without a key loses its words the way the Inbox's reply did.
        var pages = Directory.GetFiles(ScriptSource.RepositoryPath("src/Parley.Web/Pages"), "*.cshtml", SearchOption.AllDirectories);
        var boxes = pages
            .SelectMany(path => Regex.Matches(
                    File.ReadAllText(path),
                    @"new Parley\.Web\.ComposerBox\((?:[^()]|\((?:[^()]|\([^()]*\))*\))*\)",
                    RegexOptions.None,
                    TimeSpan.FromSeconds(2))
                .Select(match => (Page: Path.GetFileName(path), Box: match.Value)))
            .Where(found => found.Box.Contains("Attach: true", StringComparison.Ordinal)
                            || found.Box.Contains("Attach: id == owed[0]", StringComparison.Ordinal))
            .ToList();

        Assert.True(boxes.Count >= 5, $"only {boxes.Count} message boxes were found; the pattern no longer reads them");
        Assert.Empty(boxes.Where(found => !found.Box.Contains("Draft: $\"", StringComparison.Ordinal)).Select(found => found.Page));
    }

    [Fact]
    public void The_composer_draws_the_key_on_its_box()
    {
        var composer = ScriptSource.Page("src/Parley.Web/Pages/Shared/_Composer.cshtml");

        Assert.Matches("<textarea data-composer [^>]*data-draft=\"@Model.Draft\"", composer);
    }

    [Fact]
    public void A_box_drawn_with_words_keeps_them_and_only_an_empty_one_gets_its_draft_back()
    {
        // Without the first guard a refused send, which the server draws with what was sent, was overwritten by an older
        // draft. Without the second the draft was lost the moment the box was drawn empty.
        ScriptSource.Once(
            Drafts,
            "if (box.value.trim() !== \"\") { write(key, box.value); return; }",
            "a box drawn with words no longer keeps them over an older draft");
        ScriptSource.Once(Drafts, "box.value = text;", "an empty box no longer gets its draft back");
        ScriptSource.Once(Drafts, "if (row) { row.open = true; }", "a draft put back in a closed Inbox row is no longer shown");
    }

    [Fact]
    public void Typing_saves_and_an_empty_box_saves_nothing()
    {
        ScriptSource.Once(
            Drafts,
            "if (box instanceof HTMLTextAreaElement && box.hasAttribute(\"data-draft\")) { write(box.getAttribute(\"data-draft\"), box.value); }",
            "typing in a message box no longer saves it");
        ScriptSource.Once(
            Drafts,
            "if (text.trim() === \"\") { store.removeItem(prefix + key); }",
            "a box emptied by hand no longer drops its draft");
    }

    [Fact]
    public void A_send_drops_the_draft_and_a_send_the_page_stopped_does_not()
    {
        // The conversation page stops a send with files and nobody picked; that was never sent, and its words stay.
        ScriptSource.Once(
            Drafts,
            "if (e.defaultPrevented || !(e.target instanceof HTMLFormElement)) { return; }",
            "a send the page stopped now drops the draft");
        ScriptSource.Once(
            Drafts,
            "e.target.querySelectorAll(\"textarea[data-draft]\").forEach(function (box) { write(box.getAttribute(\"data-draft\"), \"\"); });",
            "a send no longer drops the form's drafts");
    }

    [Fact]
    public void Storage_that_refuses_leaves_the_page_working()
    {
        // A private window or blocked site data throws on the first touch; the page then works as it did before drafts.
        ScriptSource.Once(
            Drafts,
            "try { store = window.localStorage; store.getItem(prefix); } catch (e) { return; }",
            "a refused storage now breaks the page's script");
    }
}
