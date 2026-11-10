using Parley.Client.Seating;
using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// The terminal's way to ask each agent which of its questions to the person are still open, as the console's Inbox does.
/// </summary>
/// <remarks>
/// <b>The owner, 2026-11-06:</b> fix the remaining open items, one of which was that the terminal had none of this. The
/// round and the standing are the console's own (<see cref="StillOpen"/>); these hold that the key asks, lists the agents
/// asked with where each stands, and does not ask twice.
/// </remarks>
[Integration]
public sealed class AskingWhichAreStillOpenOnTheTerminalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<IReadOnlyList<string>> PressAsync(Seated seated)
    {
        // The list from an earlier press is closed first, as a person closes it: it is a session on top and takes keys.
        var before = seated.Shell.StillOpenShown;

        if (seated.Shell.StillOpenSession is { } open)
        {
            Prompting.Close(seated.Session.App, open);
        }

        seated.Focus<RoomRegion>();
        await seated.Session.PressThenSettleAsync(new Key(Shell.StillOpenKey).WithAlt, () => seated.Shell.Settled);

        Assert.NotNull(seated.Shell.StillOpenShown);
        Assert.NotSame(before, seated.Shell.StillOpenShown);

        return seated.Shell.StillOpenShown!.Shown;
    }

    [Fact]
    public async Task The_key_asks_the_agent_and_lists_it_with_where_it_stands()
    {
        await using var seated = await Seated.OpenAsync(Ct);

        // The fixture's own question from orders, and one more from billing in another conversation.
        _ = await seated.AsksAsync(Seated.Billing, seated.Person, "Monthly or yearly?", "billing-talk", Ct);

        var lines = await PressAsync(seated);

        Assert.StartsWith("Asked about 2 questions in 2 requests.", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, line => line.StartsWith($"{Seated.Orders.Value} in {Seated.Conversation}: Waiting for its reply.", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith($"{Seated.Billing.Value} in billing-talk: Waiting for its reply.", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("0 of 1 closed.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_press_asks_nobody_still_to_reply_and_lists_them_again()
    {
        await using var seated = await Seated.OpenAsync(Ct);

        _ = await PressAsync(seated);
        var again = await PressAsync(seated);

        Assert.StartsWith("Nobody was asked. One agent was already asked and has not replied yet", again[0], StringComparison.Ordinal);
        Assert.Contains(again, line => line.StartsWith($"{Seated.Orders.Value} in {Seated.Conversation}: Waiting for its reply.", StringComparison.Ordinal));

        // One Request from the person to orders, not two.
        var export = await seated.Operators.ExportAsync(Seated.Conversation, Ct);

        Assert.Single(export.Messages, message =>
            message.From == seated.Person && message.PayloadJson.Contains(StillOpen.Opening, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_press_lists_an_agent_still_to_reply_as_the_first_press_did()
    {
        // Without the record of what the press asked, the second press found the ask again only in the broker's record,
        // which does not say how the agent was reached, so the line that had said so named the agent instead (review,
        // lens 3 round 36).
        await using var seated = await Seated.OpenAsync(Ct);

        var first = await PressAsync(seated);
        var again = await PressAsync(seated);

        var orders = $"{Seated.Orders.Value} in {Seated.Conversation}: ";

        Assert.StartsWith("Nobody was asked.", again[0], StringComparison.Ordinal);
        Assert.Equal(
            Assert.Single(first, line => line.StartsWith(orders, StringComparison.Ordinal)),
            Assert.Single(again, line => line.StartsWith(orders, StringComparison.Ordinal)));
    }

    [Fact]
    public void The_dialog_says_when_nothing_waits_and_makes_every_line_inert()
    {
        var empty = StillOpenView.Lines("Nobody was asked.", []);

        Assert.Equal("Nobody has questions waiting on you here.", empty[^1]);

        var ask = new StillOpenAsk("alpha", Seated.Orders, "room", [1], null, "refused\u001b[31m");
        var lines = StillOpenView.Lines("said", [new StillOpenStanding(ask, 0, "Not sent", ask.Said)]);

        Assert.DoesNotContain(lines, line => line.Contains('\u001b', StringComparison.Ordinal));
    }
}
