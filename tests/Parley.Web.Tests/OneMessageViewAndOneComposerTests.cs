using System.Text.RegularExpressions;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// That every page draws a message through one partial and takes a person's words in one box with one set of keys.
/// </summary>
/// <remarks>
/// <b>The owner's request, 2026-11-06.</b> The Inbox drew a message's Markdown as plain text while its conversation
/// rendered it, and Enter sent on the conversation page and made a line on the Inbox. Each page had its own copy. These
/// hold the copies to one: <c>_MessageBody</c> for what a message says, <c>_Composer</c> for the box, and one key handler
/// in the frame.
/// </remarks>
[Integration]
public sealed class OneMessageViewAndOneComposerTests
{
    private const string Orders = "orders@test";
    private const string Person = "human-user@" + RoadmapConsole.Project;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IEnumerable<(string Name, string Text)> Pages() =>
        Directory.GetFiles(RepositoryPath("src/Parley.Web/Pages"), "*.cshtml", SearchOption.AllDirectories)
            .Select(path => (Path.GetFileName(path), File.ReadAllText(path)));

    /// <summary>The frame's composer key handler script, as code.</summary>
    /// <returns>The script with its comments taken out and its whitespace folded.</returns>
    private static string ComposerKeys() =>
        ScriptSource.Code(Pages().Single(page => page.Name == "_Layout.cshtml").Text, "hasAttribute(\"data-composer\")");

    [Fact]
    public void Only_the_message_partial_renders_what_a_message_says()
    {
        var drawing = Pages()
            .Where(page => page.Text.Contains("MarkdownText.Render", StringComparison.Ordinal)
                           || page.Text.Contains("class=\"said\"", StringComparison.Ordinal)
                           || page.Text.Contains("class=\"points\"", StringComparison.Ordinal))
            .Select(page => page.Name)
            .ToList();

        Assert.Equal(["_MessageBody.cshtml"], drawing);
    }

    [Fact]
    public void Every_page_that_shows_a_message_uses_the_message_partial()
    {
        foreach (var name in new[] { "_Conversation.cshtml", "Inbox.cshtml" })
        {
            var text = Pages().Single(page => page.Name == name).Text;

            Assert.Contains("<partial name=\"_MessageBody\"", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_box_a_person_types_in_is_the_composer()
    {
        // The owner, 2026-11-06: "Anywhere where you enter in text should have the same behavior". A page with a textarea of
        // its own has keys of its own. A box that only shows text (readonly) takes no typing and is the one exception.
        var own = Pages()
            .Where(page => page.Name != "_Composer.cshtml")
            .SelectMany(page => Regex.Matches(page.Text, "<textarea[^>]*>").Select(match => (page.Name, Tag: match.Value)))
            .Where(found => !found.Tag.Contains(" readonly", StringComparison.Ordinal))
            .Select(found => $"{found.Name}: {found.Tag}")
            .ToList();

        Assert.Empty(own);
        Assert.Contains("data-composer", Pages().Single(page => page.Name == "_Composer.cshtml").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_composer_draws_an_attachment_area_and_only_the_frame_takes_a_paste_or_a_drop()
    {
        // The owner, 2026-11-13: a screenshot pasted on the conversation page was attached, and in the Inbox nothing
        // happened, because the conversation page carried its own copy. Attaching is the composer's (SPEC.md §5.6).
        var drawing = Pages()
            .Where(page => page.Text.Contains("class=\"attachments\"", StringComparison.Ordinal)
                           || page.Text.Contains("name=\"Attachments\"", StringComparison.Ordinal))
            .Select(page => page.Name)
            .ToList();

        Assert.Equal(["_Composer.cshtml"], drawing);

        var taking = Pages()
            .Where(page => Regex.IsMatch(page.Text, "addEventListener\\(\"(paste|drop)\"", RegexOptions.None, TimeSpan.FromSeconds(1)))
            .Select(page => page.Name)
            .ToList();

        Assert.Equal(["_Layout.cshtml"], taking);
    }

    [Fact]
    public void Every_page_that_sends_a_message_from_the_seat_takes_files_in_its_composer()
    {
        // Drawn by what the page does, not by a list of pages (SPEC.md §5.6): a page model that sends a Request, a reply or
        // a Result in the person's voice is a message box, and a new one is held to this without being named here.
        var sends = new Regex(@"\.(RequestAsync|RequestOpeningAsync|ClarifyAsync|ResultAsync)\(", RegexOptions.None, TimeSpan.FromSeconds(1));
        var models = Directory.GetFiles(RepositoryPath("src/Parley.Web/Pages"), "*.cshtml.cs", SearchOption.AllDirectories)
            .Where(path => sends.IsMatch(File.ReadAllText(path)))
            .ToList();

        Assert.NotEmpty(models);

        foreach (var model in models)
        {
            var page = model[..^".cs".Length];
            var markup = File.ReadAllText(page)
                         + string.Concat(Directory.GetFiles(Path.GetDirectoryName(page)!, "_*.cshtml").Select(File.ReadAllText));

            Assert.True(
                File.ReadAllText(model).Contains("IList<IFormFile> Attachments", StringComparison.Ordinal),
                $"{Path.GetFileName(model)} sends a message and takes no files");
            Assert.True(
                markup.Contains("Attach: true", StringComparison.Ordinal),
                $"{Path.GetFileName(page)} sends a message and its composer takes no files");
            Assert.True(
                markup.Contains("enctype=\"multipart/form-data\"", StringComparison.Ordinal),
                $"{Path.GetFileName(page)} sends a message and its form cannot carry files");
        }
    }

    [Fact]
    public void Only_the_frame_sends_on_enter()
    {
        var sending = Pages()
            .Where(page => page.Text.Contains("requestSubmit", StringComparison.Ordinal))
            .Select(page => page.Name)
            .ToList();

        Assert.Equal(["_Layout.cshtml"], sending);

        // Read as code, comments out, so a line commented out behind a weaker one no longer counts as there.
        var handler = ComposerKeys();

        // Ctrl+Enter makes a line, as in the terminal; it no longer sends.
        Assert.Contains("if (e.ctrlKey) { lineBreak(", handler, StringComparison.Ordinal);
        Assert.Contains("form.requestSubmit()", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void Enter_is_left_alone_with_shift_while_composing_and_when_taken_first()
    {
        // Without e.shiftKey Shift+Enter sends instead of making a line break; without e.isComposing the Enter that
        // confirms an input method's text sends the half-written message. No test runs the frame's script, so the
        // handler's early return is read from source and each key it must leave alone is named (review, lens 3 round 33).
        // Read as code, comments out, so a guard commented out behind a weaker line no longer counts as there.
        var frame = ComposerKeys();
        var start = frame.IndexOf("hasAttribute(\"data-composer\")", StringComparison.Ordinal);

        Assert.True(start >= 0, "the composer's key handler was not found");

        var handler = frame[start..frame.IndexOf("e.preventDefault();", start, StringComparison.Ordinal)];
        var guard = Assert.Single(Regex.Matches(handler, """if \((?<condition>e\.key !== "Enter"[^)]*)\) \{ return; \}"""));
        var leftAlone = guard.Groups["condition"].Value
            .Split("||", StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "e.key !== \"Enter\"", "e.isComposing", "e.altKey", "e.metaKey", "e.shiftKey", "e.defaultPrevented",
            },
            leftAlone);
    }

    [Fact]
    public async Task The_inbox_renders_a_messages_markdown_and_its_boxes_are_composers()
    {
        using var console = new RoadmapConsole();

        console.Broker.Seats = true;
        console.Broker.Registry.Add(new Wire.RegistryRow
        {
            Name = Person,
            Adapter = "parley-console",
            Tier = Wire.AgentTier.Full,
            Status = Wire.AgentStatus.Active,
        });
        console.Broker.Conversations.Add(new Wire.ConversationSummary { ConversationId = "shared", HasOpenExchange = true });
        console.Broker.ExportMessages.Add(new Wire.Message
        {
            Id = 1,
            ExchangeId = 1,
            Type = Wire.MessageType.Request,
            From = Orders,
            To = Person,
            ResponderModel = "test",
            PayloadJson = """{"ask":"Use the **new** schema?\n\n- yes\n- no"}""",
            ConversationId = "shared",
            CreatedUtc = "2026-11-06T10:00:00Z",
        });
        console.Broker.ExportExchanges.Add(new Wire.ExchangeSnapshot
        {
            ExchangeId = 1,
            State = Wire.ExchangeState.Open,
            OpenedUtc = "2026-11-06T10:00:00Z",
        });
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Orders, WaitedOn = Person, ExchangeId = 1 });

        using var client = console.CreateClient();
        var page = await client.GetStringAsync(new Uri("/Inbox", UriKind.Relative), Ct);

        // In the opened message; the row's one-line preview is an excerpt of the words and stays plain.
        var body = page[page.IndexOf("<div class=\"said\">", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("</div>", StringComparison.Ordinal)];

        Assert.Contains("<strong>new</strong>", body, StringComparison.Ordinal);
        Assert.Contains("<li>yes</li>", body, StringComparison.Ordinal);
        Assert.DoesNotContain("**new**", body, StringComparison.Ordinal);
        Assert.Matches("""<textarea data-composer id="t-1-request" name="Texts" """, page);
        Assert.Matches("""<textarea data-composer id="c-1" name="Clarification" """, page);
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
