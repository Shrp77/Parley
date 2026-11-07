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
    public void Only_the_frame_sends_on_enter()
    {
        var sending = Pages()
            .Where(page => page.Text.Contains("requestSubmit", StringComparison.Ordinal))
            .Select(page => page.Name)
            .ToList();

        Assert.Equal(["_Layout.cshtml"], sending);

        var frame = Pages().Single(page => page.Name == "_Layout.cshtml").Text;

        // Ctrl+Enter makes a line, as in the terminal; it no longer sends.
        Assert.Contains("if (e.ctrlKey) { lineBreak(", frame, StringComparison.Ordinal);
        Assert.Contains("hasAttribute(\"data-composer\")", frame, StringComparison.Ordinal);
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
