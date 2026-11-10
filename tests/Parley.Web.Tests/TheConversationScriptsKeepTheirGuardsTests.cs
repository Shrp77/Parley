using System.Globalization;
using System.Text.RegularExpressions;
using Parley.Web.Pages.Conversations;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// Guards in the conversation page's scripts: hiding the composer, following one thread, attaching files and the chips
/// that say who a message goes to.
/// </summary>
/// <remarks>
/// There is no script engine in these tests, so each guard is read from the page's source with its comments taken out and
/// its whitespace folded (<see cref="ScriptSource"/>), and held to the whole statement or the exact set of conditions it
/// is, so taking the guard out, or keeping a fragment of it, fails here.
/// </remarks>
[Unit]
public sealed class TheConversationScriptsKeepTheirGuardsTests
{
    private static string Detail => ScriptSource.Page("src/Parley.Web/Pages/Conversations/Detail.cshtml");

    /// <summary>The page's main script, which holds the composer, the filter, attaching and the chips.</summary>
    private static string Main => ScriptSource.Code(Detail, "parley:compose");

    /// <summary>The recipients tree's script, which draws the To line and its chips.</summary>
    private static string Tree => ScriptSource.Code(Detail, "window.parleyTree = (function () {");

    [Fact]
    public void A_refused_send_keeps_the_composer_open()
    {
        // Without the guard a composer the person had hidden came back hidden after a refused send, hiding what went wrong
        // and the words still to be sent (review, lens 3 round 37).
        var code = ScriptSource.Code(Detail, "data-keep-open");

        ScriptSource.Once(
            code,
            "if (document.getElementById(\"composer\").getAttribute(\"data-keep-open\") !== \"true\" " +
            "&& window.localStorage.getItem(\"parley-convo-composer\") === \"closed\") { " +
            "document.getElementById(\"composer\").classList.add(\"composer-closed\"); }",
            "the composer is hidden from the stored choice without asking whether the page must keep it open");

        // And the server marks a refused send as one to keep open.
        Assert.Contains(
            "data-keep-open=\"@(Model.SendProblem is { Length: > 0 } ||",
            Detail,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Attached_files_count_as_a_kept_draft()
    {
        // Without the attachments in the count a hidden composer holding only files said "Write", as if nothing were waiting
        // in it (review, lens 3 round 37).
        var kept = Regex.Match(Main, @"var kept = (?<condition>[^;]*); toggle\.setAttribute\(""aria-expanded""", RegexOptions.None, TimeSpan.FromSeconds(1));

        Assert.True(kept.Success, "the composer no longer works out whether a draft is kept");
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "box.value.trim() !== \"\"", "reply.value !== \"\"", "form.querySelector(\".attachment\") !== null",
            },
            ScriptSource.Terms(kept.Groups["condition"].Value, "||"));

        ScriptSource.Once(
            Main,
            "words.textContent = closed() ? (kept ? \"Write (draft kept)\" : \"Write\") : \"Hide\";",
            "the hidden composer's button no longer says a draft is kept");
    }

    [Fact]
    public void Replying_or_pasting_opens_a_hidden_composer()
    {
        // Without the guard "Reply to this" or a paste into a hidden composer chose a reply or attached a file nobody could
        // see (review, lens 3 round 37).
        ScriptSource.Once(
            Main,
            "document.addEventListener(\"parley:compose\", function () { if (closed()) { set(false); } });",
            "a reply or an attachment no longer opens a hidden composer");

        // And both say so: a reply chosen, and every file added.
        ScriptSource.Once(
            Main,
            "outcome.hidden = button.getAttribute(\"data-request\") !== \"1\"; document.dispatchEvent(new CustomEvent(\"parley:compose\"));",
            "choosing a reply no longer asks for the composer to open");
        ScriptSource.Once(
            Main,
            "function addAll(chosen) { say(\"\"); chosen.forEach(add); sync(); document.dispatchEvent(new CustomEvent(\"parley:compose\")); }",
            "adding files no longer asks for the composer to open");
    }

    [Fact]
    public void Hiding_the_composer_is_remembered_as_hidden()
    {
        // Without the guard the choice was stored as "open" whichever way it went, so a hidden composer came back open on
        // the next page (review, lens 3 round 37).
        ScriptSource.Once(
            Main,
            "try { window.localStorage.setItem(\"parley-convo-composer\", hide ? \"closed\" : \"open\"); } catch (e) { }",
            "the composer's hidden state is no longer stored as the person chose it");
        ScriptSource.Once(
            Main,
            "toggle.addEventListener(\"click\", function () { var hide = !closed(); set(hide);",
            "the toggle no longer stores the state it turns the composer to");
    }

    [Fact]
    public void A_second_press_on_a_thread_shows_the_whole_conversation_again()
    {
        // Without the guard a second press on a thread set the same thread again, and only the filter's Clear led back to
        // the whole conversation (review, lens 3 round 37).
        ScriptSource.Once(
            Main,
            "thread = thread.length > 0 ? [] : (button.getAttribute(\"data-exchanges\") || \"\").split(\" \"); run();",
            "a press on a thread no longer returns to the whole conversation when a thread is shown");
    }

    [Fact]
    public void A_thread_shown_keeps_only_its_exchanges()
    {
        // Without the branch a press on a thread changed nothing in the transcript (review, lens 3 round 37).
        var branch = ScriptSource.Once(
            Main,
            "if (thread.length > 0) { return (turn.getAttribute(\"data-exchanges\") || \"\").split(\" \").some(function (x) { return thread.indexOf(x) >= 0; }); }",
            "the transcript filter no longer keeps only the thread's exchanges");
        var agents = ScriptSource.Once(
            Main,
            "if (only.checked && from !== human && to.indexOf(human) < 0) { return false; }",
            "the filter's own test was not found");

        Assert.True(branch < agents, "the thread is no longer decided before the other filters");
    }

    [Fact]
    public void The_thread_button_reads_show_everything_while_a_thread_is_shown()
    {
        // Without the guard the button kept saying "Thread (N)" while the thread was shown, so the way back was not named
        // (review, lens 3 round 37).
        ScriptSource.Once(
            Main,
            "b.textContent = thread.length > 0 ? \"Show everything\" : b.getAttribute(\"data-label\");",
            "the thread button no longer says how to see everything");
        Assert.Contains("class=\"thread-focus\"", ScriptSource.Page("src/Parley.Web/Pages/Conversations/_Conversation.cshtml"), StringComparison.Ordinal);
        Assert.Contains("data-label=\"Thread (@threadSize)\"", ScriptSource.Page("src/Parley.Web/Pages/Conversations/_Conversation.cshtml"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_thread_shown_counts_as_a_filter()
    {
        // Without the thread in the count a thread hid most of the transcript with no "Showing N of M" and no Clear
        // (review, lens 3 round 37).
        var active = Regex.Match(Main, @"var active = (?<condition>[^;]*);", RegexOptions.None, TimeSpan.FromSeconds(1));

        Assert.True(active.Success, "the filter no longer works out whether it is active");
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { "only.checked", "picked.length > 0", "thread.length > 0" },
            ScriptSource.Terms(active.Groups["condition"].Value, "||"));
    }

    [Fact]
    public void Only_a_file_of_a_kind_that_may_be_placed_is_attached()
    {
        // Without the guard a file of any kind was attached, and the send was refused with every file to choose again
        // (review, lens 3 round 37).
        var check = ScriptSource.Once(
            Main,
            "if (types.indexOf(extension(file.name)) < 0) { say(file.name + \" was not attached. Only \" + list.getAttribute(\"data-types-in-words\") + \" files can be.\"); return; }",
            "a file of a kind that may not be placed is attached");

        Assert.True(check < Main.IndexOf("files.push(file);", StringComparison.Ordinal), "the kind is no longer checked before the file is kept");
        ScriptSource.Once(Main, "var types = (list.getAttribute(\"data-types\") || \"\").split(\" \");", "the kinds are no longer read from the page");
    }

    [Fact]
    public void A_files_kind_is_read_from_the_end_of_its_name()
    {
        // Held whole: an extension() that returned ".md" whatever the name let a file of any kind through the pinned check
        // above, and the send was refused (review, lens 3 round 38).
        Assert.Equal(
            "var at = name.lastIndexOf(\".\"); return at < 0 ? \"\" : name.slice(at).toLowerCase();",
            ScriptSource.Body(Main, "extension"));
    }

    [Fact]
    public void Only_a_drag_that_carries_files_is_taken()
    {
        // Held whole: a carriesFiles() that answered true for every drag stopped text being dragged into the box, with the
        // pinned drag handlers untouched (review, lens 3 round 38).
        Assert.Equal(
            "return e.dataTransfer && [].indexOf.call(e.dataTransfer.types || [], \"Files\") >= 0;",
            ScriptSource.Body(Main, "carriesFiles"));
    }

    [Fact]
    public void A_second_file_of_the_same_name_is_not_attached()
    {
        // Without the guard two files of one name were attached and the second replaced the first when placed (review,
        // lens 3 round 37).
        var check = ScriptSource.Once(
            Main,
            "if (files.some(function (f) { return f.name === file.name; })) { say(file.name + \" is already attached.\"); return; }",
            "a file whose name is already attached is attached again");

        Assert.True(check < Main.IndexOf("files.push(file);", StringComparison.Ordinal), "the name is no longer checked before the file is kept");
    }

    [Fact]
    public void The_box_is_not_required_when_files_are_attached()
    {
        // Without the guard the browser refused to send files with no words, saying the box must be filled in (review,
        // lens 3 round 37).
        ScriptSource.Once(
            Main,
            "box.required = list.querySelector(\".attachment\") === null;",
            "the box no longer stops being required when files are attached");
    }

    [Fact]
    public void A_long_paste_becomes_a_file_over_the_servers_threshold()
    {
        // Without the guard a paste of any length went into the box as words, past what the page says becomes a file
        // (review, lens 3 round 37).
        ScriptSource.Once(
            Main,
            "if (text.length > limit) { e.preventDefault(); addAll([new File([text], pastedName(\".txt\"), { type: \"text/plain\" })]); return; }",
            "a paste over the threshold no longer becomes a file");
        ScriptSource.Once(
            Main,
            "var limit = Number(list.getAttribute(\"data-paste-limit\"));",
            "the threshold is no longer read from the page");

        // The page carries the server's number, and the operating guide says the same number.
        Assert.Contains("data-paste-limit=\"@Parley.Web.Pages.Conversations.DetailModel.PasteBecomesAFileOver\"", Detail, StringComparison.Ordinal);

        var guide = Regex.Replace(ScriptSource.Page("docs/operating.md"), @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1));

        Assert.Contains(
            $"Pasted text longer than {DetailModel.PasteBecomesAFileOver.ToString("N0", CultureInfo.InvariantCulture)} characters becomes a file too",
            guide,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Words_come_first_when_the_clipboard_holds_text_and_an_image()
    {
        // Without the guard a spreadsheet's cells pasted as a picture of themselves rather than as the words (review, lens 3
        // round 37).
        var words = ScriptSource.Once(Main, "if (text.length > 0) { return; }", "a paste holding words no longer stops before its files");
        var limit = Main.IndexOf("if (text.length > limit)", StringComparison.Ordinal);
        var files = Main.IndexOf("var pasted = [].slice.call(clip.files || []);", StringComparison.Ordinal);

        Assert.True(limit >= 0 && files >= 0, "the paste handler was not found");
        Assert.True(limit < words && words < files, "the words are no longer taken before the clipboard's files");
    }

    [Fact]
    public void A_pasted_image_gets_a_fresh_name()
    {
        // Without the guard every pasted image kept the clipboard's own name, "image.png", and the second was refused as
        // already attached (review, lens 3 round 37).
        ScriptSource.Once(
            Main,
            "addAll(pasted.map(function (f) { return images[f.type] ? new File([f], pastedName(images[f.type]), { type: f.type }) : f; }));",
            "a pasted image no longer gets a name of its own");
        ScriptSource.Once(
            Main,
            "return \"pasted-\" + now.getFullYear() + two(now.getMonth() + 1) + two(now.getDate()) + \"-\" + two(now.getHours()) + two(now.getMinutes()) + two(now.getSeconds()) + \"-\" + random + ext;",
            "a pasted file's name no longer carries its time and a random part");
    }

    [Fact]
    public void Files_with_nobody_picked_are_not_sent()
    {
        // Without the guard a send with files and nobody picked was refused by the server, and the files had to be chosen
        // again (review, lens 3 round 37).
        ScriptSource.Once(
            Main,
            "var picked = document.querySelector('input[name=\"Targets\"]:checked, input[name=\"Everyone\"]:checked');",
            "who is picked is no longer read before a send");
        ScriptSource.Once(
            Main,
            "if (form.querySelector(\".attachment\") && !picked && !(reply && reply.value)) { e.preventDefault(); say(\"Nobody is picked, so nothing was sent. Your files are still here.\"); }",
            "a send with files and nobody picked is no longer stopped");
    }

    [Fact]
    public void A_removed_chips_file_is_not_sent()
    {
        // Without the guard the chip went from the page and its file was still sent (review, lens 3 round 37).
        ScriptSource.Once(
            Main,
            "files = files.filter(function (f) { return f !== item.parleyFile; }); item.remove(); sync();",
            "a removed chip's file is still among the files sent");
        ScriptSource.Once(Main, "item.parleyFile = file;", "a chip no longer knows its file");
        ScriptSource.Once(
            Main,
            "files.forEach(function (f) { carried.items.add(f); }); input.files = carried.files;",
            "what the form sends is no longer the files kept");
    }

    [Fact]
    public void A_file_dragged_over_the_page_does_not_make_the_browser_leave_it()
    {
        // Without the guard a file let fall outside the box opened in the browser, and the conversation and its draft were
        // gone (review, lens 3 round 37).
        ScriptSource.Once(
            Main,
            "document.addEventListener(\"dragover\", function (e) { if (!carriesFiles(e)) { return; } e.preventDefault(); form.classList.add(\"dropping\"); });",
            "dragging a file over the page no longer stops the browser opening it");
        ScriptSource.Once(
            Main,
            "document.addEventListener(\"drop\", function (e) { form.classList.remove(\"dropping\"); if (!carriesFiles(e)) { return; } e.preventDefault();",
            "dropping a file on the page no longer stops the browser opening it");
    }

    [Fact]
    public void Everyone_ticked_says_everyone_in_the_to_line()
    {
        // Without the guard the To line listed only the named agents, or "nobody chosen yet", while the message was going to
        // everyone (review, lens 3 round 37).
        ScriptSource.Once(Tree, "var all = document.querySelector('input[name=\"Everyone\"]');", "the Everyone box is no longer read");

        var everyone = ScriptSource.Once(
            Tree,
            "if (all && all.checked) { summary.textContent = \"To: everyone in this conversation.\"; return; }",
            "the To line no longer says a message goes to everyone");
        var nobody = Tree.IndexOf("if (!named.length) {", StringComparison.Ordinal);

        Assert.True(nobody > everyone, "Everyone is no longer decided before \"nobody chosen yet\"");
    }

    [Fact]
    public void Backspace_on_a_chip_removes_it()
    {
        // Without the guard Backspace and Delete on a focused chip did nothing, the keys an input chip takes (review, lens 3
        // round 37).
        ScriptSource.Once(
            Tree,
            "summary.addEventListener(\"keydown\", function (e) { var x = e.target.closest ? e.target.closest(\"[data-remove]\") : null; " +
            "if (x && (e.key === \"Backspace\" || e.key === \"Delete\")) { e.preventDefault(); untick(x.getAttribute(\"data-remove\")); } });",
            "Backspace and Delete on a chip no longer remove it");
    }

    [Fact]
    public void Removing_a_chip_unticks_only_its_agent()
    {
        // Without the name in the test removing one chip unticked every agent, and without the checked test it sent a
        // change for boxes that were already clear (review, lens 3 round 37).
        var untick = Assert.Single(Regex.Matches(
            Tree,
            """boxes\(\)\.forEach\(function \(b\) \{ if \((?<condition>[^)]*)\) \{ b\.checked = false; b\.dispatchEvent\(new Event\("change", \{ bubbles: true \}\)\); \} \}\);""",
            RegexOptions.None,
            TimeSpan.FromSeconds(1)));

        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { "b.name === \"Targets\"", "b.value === name", "b.checked" },
            ScriptSource.Terms(untick.Groups["condition"].Value, "&&"));
    }
}
