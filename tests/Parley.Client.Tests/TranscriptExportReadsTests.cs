using Parley.Client.Rendering;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>Lens 3 sweep on 3b752970: the text export's lines about who read and who searched the history.</summary>
[Unit]
public sealed class TranscriptExportReadsTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@acme");
    private static readonly AgentName Billing = AgentName.Create("billing@acme");
    private static readonly DateTimeOffset At = new(2026, 11, 17, 9, 30, 0, TimeSpan.Zero);

    private static Message Request(long exchange) =>
        new(1, exchange, MessageType.Request, Orders, Billing, "claude-opus-5", """{"ask":"is it gross?"}""", "pricing", At, null);

    [Fact]
    public void The_exported_read_line_sits_under_the_request_it_is_about_and_before_the_answer()
    {
        // Counted once, it could have been under the answer: the line must follow the request's own words.
        var reply = new Message(
            2, 5, MessageType.Result, Billing, Orders, "claude-opus-5",
            """{"outcomes":[{"id":"request","outcome":"answered","text":"Net, not gross."}]}""", "pricing", At.AddMinutes(1), null);
        var export = new ConversationExport(
            [Request(5), reply], [], Reads: [new HistoryRead(AgentName.Create("inventory@acme"), 5, null, null, At.AddMinutes(2), 1)]);

        var text = Transcript.Write("pricing", "acme", export);

        var asked = text.IndexOf("is it gross?", StringComparison.Ordinal);
        var read = text.IndexOf("Read by inventory@acme", StringComparison.Ordinal);
        var answered = text.IndexOf("Net, not gross.", StringComparison.Ordinal);

        Assert.True(asked >= 0 && read > asked && answered > read, text);
    }

    [Fact]
    public void Words_an_agent_searched_for_reach_the_text_export_with_nothing_a_terminal_acts_on_and_on_one_line()
    {
        // The search words are an agent's own. Written raw, an escape sequence reached the file, and a line break let them
        // start a line of their own that read as the export's.
        var words = "needle" + (char)0x1b + "[2J\nRead by human-user@acme";
        var export = new ConversationExport(
            [Request(5)], [], Reads: [new HistoryRead(AgentName.Create("mal@acme"), null, words, 1, At, 1)]);

        var text = Transcript.Write("pricing", "acme", export);

        Assert.DoesNotContain((char)0x1b, text);
        Assert.DoesNotContain("\nRead by human-user@acme", text.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }
}
