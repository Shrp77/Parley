using Parley.Client.Rendering;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// The person is told who read an exchange, beneath its first message, in the text export and on the screen (SPEC.md §5.8).
/// </summary>
/// <remarks>
/// The line is the person's alone: it is drawn from the export, which only the operator reads, and an agent has no call
/// that returns it.
/// </remarks>
[Unit]
public sealed class WhoReadAnExchangeTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@acme");
    private static readonly AgentName Billing = AgentName.Create("billing@acme");
    private static readonly DateTimeOffset At = new(2026, 11, 17, 9, 30, 0, TimeSpan.Zero);

    private static HistoryRead Read(string reader, long? exchange, int minute) =>
        new(AgentName.Create(reader), exchange, exchange is null ? "invoice" : null, exchange is null ? 2 : null, At.AddMinutes(minute));

    private static Message Request(long exchange) =>
        new(1, exchange, MessageType.Request, Orders, Billing, "claude-opus-5", """{"ask":"is it gross?"}""", "pricing", At, null);

    [Fact]
    public void Nobody_having_read_it_says_nothing()
    {
        Assert.Null(Reading.ReadLine([], 5, TimeZoneInfo.Utc));
        Assert.Null(Reading.ReadLine(null, 5, TimeZoneInfo.Utc));
        Assert.Null(Reading.ReadLine([Read("inventory@acme", 6, 1)], 5, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Readers_are_named_with_when_oldest_first()
    {
        var line = Reading.ReadLine([Read("later@acme", 5, 9), Read("inventory@acme", 5, 1)], 5, TimeZoneInfo.Utc);

        Assert.Equal("Read by inventory@acme at 17 Nov 09:31, later@acme at 17 Nov 09:39", line);
    }

    [Fact]
    public void A_search_is_not_a_read_of_an_exchange()
    {
        Assert.Null(Reading.ReadLine([Read("inventory@acme", null, 1)], 5, TimeZoneInfo.Utc));
    }

    [Fact]
    public void At_most_five_are_named_and_the_rest_counted()
    {
        var reads = Enumerable.Range(0, 8).Select(i => Read($"agent{i}@acme", 5, i)).ToList();

        var line = Reading.ReadLine(reads, 5, TimeZoneInfo.Utc);

        Assert.EndsWith(" and 3 more", line, StringComparison.Ordinal);
        Assert.DoesNotContain("agent5@acme", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_exported_text_carries_the_line_under_the_request_and_not_under_a_reply()
    {
        var reply = new Message(2, 5, MessageType.Result, Billing, Orders, "claude-opus-5", """{"outcomes":[]}""", "pricing", At.AddMinutes(1), null);
        var export = new ConversationExport([Request(5), reply], [], Reads: [Read("inventory@acme", 5, 2)]);

        var text = Transcript.Write("pricing", "acme", export);

        Assert.Equal(1, text.Split("Read by inventory@acme").Length - 1);
    }

    [Fact]
    public void The_screen_draws_it_as_an_annotation_under_the_request()
    {
        var export = new ConversationExport([Request(5)], [], Reads: [Read("inventory@acme", 5, 2)]);

        var lines = Reading.Lines(export, TimeZoneInfo.Utc);

        var annotation = Assert.Single(lines, l => l.Text.Contains("Read by inventory@acme", StringComparison.Ordinal));
        Assert.Equal(LineKind.Annotation, annotation.Kind);
    }
}
