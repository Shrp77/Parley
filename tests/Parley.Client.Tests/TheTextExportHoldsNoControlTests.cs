using Parley.Client.Rendering;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// That nothing an agent wrote reaches the text export as a character a terminal or an editor acts on: each is replaced
/// one for one.
/// </summary>
/// <remarks>
/// Lens 2 run 139: the line beneath a reply that names the questions it settled carried the asker's decoded words with
/// only their whitespace collapsed, so ESC, BEL, a one-byte CSI and a right-to-left override landed raw in the file,
/// where <c>cat</c> or <c>type</c> hands them to the terminal. The characters are built from their numbers so no editor
/// can turn an escape in this file into the character itself.
/// </remarks>
[Unit]
public sealed class TheTextExportHoldsNoControlTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Person = AgentName.Create("human-user@test");
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    private const string Conversation = "invoice-export";

    private static readonly string Backslash = ((char)92).ToString();

    // CSI and RLO may sit raw in a JSON string; ESC and BEL may not, so they arrive escaped and are decoded by the reader.
    private static readonly string Csi = ((char)0x9B).ToString();
    private static readonly string Override = ((char)0x202E).ToString();

    private static readonly string Question =
        "{\"ask\":\"a" + Backslash + "u001Bb" + Backslash + "u0007c" + Csi + "d" + Override + "e\"}";

    private static Message Said(long id, long exchange, MessageType type, AgentName from, AgentName to, string payload, DateTimeOffset at) =>
        new(id, exchange, type, from, to, "model-a", payload, Conversation, at, null);

    // orders asked (exchange 1) with control characters in its words; the person wrote (2); orders replied, settling 1.
    private static ConversationExport Settling()
    {
        var reply = T0.AddMinutes(10);

        return new ConversationExport(
            [
                Said(1, 1, MessageType.Request, Orders, Person, Question, T0),
                Said(2, 2, MessageType.Request, Person, Orders, """{"ask":"Twelve."}""", T0.AddMinutes(5)),
                Said(3, 2, MessageType.Result, Orders, Person,
                    "{\"outcomes\":[{\"id\":\"request\",\"outcome\":\"answered\",\"text\":\"Done" + Csi + Override + "\"}]}",
                    reply),
            ],
            [
                new ExchangeSnapshot(1, ExchangeState.Closed, T0, reply, ExchangeOutcome.Settled, 2),
                new ExchangeSnapshot(2, ExchangeState.Closed, T0.AddMinutes(5), reply, ExchangeOutcome.Complete),
            ],
            null);
    }

    [Fact]
    public void The_settled_line_carries_the_question_with_each_control_replaced()
    {
        var export = Settling();

        // What the reader decodes still holds them; the export is where they are neutralised.
        var said = Assert.Single(Reading.Settled(export.Messages[2], export));
        Assert.Equal("a" + (char)0x1B + "b" + (char)0x07 + "c" + Csi + "d" + Override + "e", said);

        var written = Transcript.Write(Conversation, "alpha", export.Messages, export.Exchanges);

        Assert.Contains("Settled: “a?b?c?d?e”", written, StringComparison.Ordinal);
    }

    [Fact]
    public void A_payload_carries_each_raw_control_replaced()
    {
        var export = Settling();

        var written = Transcript.Write(Conversation, "alpha", export.Messages, export.Exchanges);

        Assert.Contains("\"text\":\"Done??\"", written, StringComparison.Ordinal);
        Assert.Contains("a" + Backslash + "u001Bb" + Backslash + "u0007c?d?e", written, StringComparison.Ordinal);
    }

    // Lens 2 run 140: the web page passes the conversation and project from its query string, and the heading took both raw.
    [Fact]
    public void The_heading_carries_a_conversation_and_project_from_a_link_with_each_control_replaced()
    {
        var export = Settling();
        var escape = ((char)0x1B).ToString();

        var written = Transcript.Write(
            "inv" + escape + "]52;c;eA==" + (char)0x07 + "\r\nFAKE",
            "al" + escape + "pha",
            export.Messages,
            export.Exchanges);

        Assert.StartsWith("Conversation inv?]52;c;eA==?" + Backslash + "nFAKE in project al?pha", written, StringComparison.Ordinal);
        Assert.DoesNotContain(escape, written, StringComparison.Ordinal);
    }

    [Fact]
    public void The_export_holds_no_character_that_breaks_a_line_but_its_own_line_ends()
    {
        var export = Settling();

        var written = Transcript.Write(Conversation, "alpha", export.Messages, export.Exchanges);

        var breaking = written.EnumerateRunes()
            .Where(rune => rune.Value is not '\r' and not '\n' && PrintableLine.Breaks(rune))
            .Select(rune => $"U+{rune.Value:X4}")
            .ToList();

        Assert.Empty(breaking);
    }
}
