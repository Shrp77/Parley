using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>What a row of the terminal's Inbox says, and that it carries nothing of somebody else's but plain text.</summary>
/// <remarks>
/// A row ends in a preview of the last message, which an agent wrote. Each case calls the rows' own maker with a reading
/// built in memory, so it needs no broker and runs with the unit tests: the class that drives the Inbox through the keys is
/// slow, and a guard on what an agent's text may do to the terminal should not wait for the nightly run.
/// </remarks>
[Unit]
public sealed class TheInboxRowsAreInertTests
{
    private static Parley.Client.Seating.InboxReading Reading(params Parley.Client.Seating.InboxRow[] rows) =>
        new(rows, 7, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(7));

    [Fact]
    public void Every_row_of_the_inbox_is_inert()
    {
        // A row ends in the last message's words, which are somebody else's: an escape sequence or a bell in them is
        // never handed to the terminal.
        var said = new Parley.Core.Message(
            1, 5, Parley.Core.MessageType.Request, Seated.Billing, Seated.Orders, "test",
            "{\"ask\":\"red\\u001b[31m and \\u0007 bell\"}", "billing-talk", DateTimeOffset.UnixEpoch, null);

        var lines = InboxWindow.Lines(Reading(
            new Parley.Client.Seating.InboxRow(
                5, "billing-talk", Seated.Billing, Parley.Client.Seating.InboxView.WaitingOnYou,
                DateTimeOffset.UnixEpoch.AddHours(2), said, null, null)));

        var row = Assert.Single(lines, line => line.Row is not null).Line;

        Assert.Contains("red", row, StringComparison.Ordinal);
        Assert.Contains("bell", row, StringComparison.Ordinal);
        Assert.All(lines, line =>
        {
            Assert.DoesNotContain((char)0x1B, line.Line);
            Assert.DoesNotContain((char)0x07, line.Line);
            Assert.DoesNotContain(line.Line, character => char.IsControl(character));
        });
    }

    [Fact]
    public void A_closed_row_says_how_it_ended_and_never_that_an_answer_is_owed()
    {
        var lines = InboxWindow.Lines(Reading(
            new Parley.Client.Seating.InboxRow(
                5, "billing-talk", Seated.Billing, Parley.Client.Seating.InboxView.Closed,
                DateTimeOffset.UnixEpoch.AddHours(2), null, null,
                new Parley.Client.Seating.ExchangeEnding(
                    DateTimeOffset.UnixEpoch.AddHours(3), Parley.Client.Seating.Ending.Blocked, "missing information", Seated.Billing))));

        var row = Assert.Single(lines, line => line.Row is not null).Line;

        Assert.Contains(": Blocked: missing information. ", row, StringComparison.Ordinal);
        Assert.DoesNotContain("owes you an answer", row, StringComparison.Ordinal);
        Assert.Contains("Closed in the last 7 days (1)", lines.Select(line => line.Line));
    }

    [Fact]
    public void A_flagged_row_in_flight_says_its_flag_beside_where_the_agent_stands()
    {
        var flagged = new Parley.Client.Seating.InFlightStanding(
            Parley.Client.Seating.AgentState.Idle, null, Parley.Client.Seating.ExchangeFlag.Stalled);
        var plain = new Parley.Client.Seating.InFlightStanding(Parley.Client.Seating.AgentState.Working, null, null);

        var lines = InboxWindow.Lines(Reading(
            new Parley.Client.Seating.InboxRow(
                5, "billing-talk", Seated.Billing, Parley.Client.Seating.InboxView.InFlight,
                DateTimeOffset.UnixEpoch.AddHours(2), null, flagged, null),
            new Parley.Client.Seating.InboxRow(
                6, "orders-talk", Seated.Orders, Parley.Client.Seating.InboxView.InFlight,
                DateTimeOffset.UnixEpoch.AddHours(1), null, plain, null)));

        Assert.Contains(lines, line => line.Line.Contains("in billing-talk", StringComparison.Ordinal)
                                       && line.Line.Contains(": Idle, Stalled. ", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Line.Contains("in orders-talk", StringComparison.Ordinal)
                                       && line.Line.Contains(": Working. ", StringComparison.Ordinal));
    }
}
