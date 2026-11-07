using Parley.Client.Views;
using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>The terminal's Inbox and its references to exchanges, as the console has them (spec 036).</summary>
/// <remarks>
/// <b>The owner, 2026-11-06:</b> the Inbox keeps tabs on an exchange after it is answered, a message's "exchange #N" opens
/// that exchange where it is mentioned, and the terminal has the same. The views and their words are the console's own
/// (<c>Inbox</c>); these hold that the keys open them with the right rows.
/// </remarks>
[Integration]
public sealed class TheInboxOnTheTerminalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_key_lists_what_waits_on_the_person_and_what_is_in_flight()
    {
        await using var seated = await Seated.OpenAsync(Ct);

        // The fixture's own question from orders waits on the person; the person's request to billing is in flight.
        await seated.AskAsync(Seated.Billing, "billing-talk", Ct, go: false);

        seated.Focus<RoomRegion>();
        await seated.Session.PressThenSettleAsync(new Key(Shell.InboxKey).WithAlt, () => seated.Shell.Settled);

        var lines = Assert.IsType<InboxWindow>(seated.Shell.InboxShown).Shown;

        Assert.Contains("Waiting on you (1)", lines);
        Assert.Contains("In flight (1)", lines);
        Assert.Contains(lines, line => line.Contains($"{Seated.Orders.Value} in {Seated.Conversation}", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains($"{Seated.Billing.Value} in billing-talk", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("Closed in the last 7 days", StringComparison.Ordinal));

        Prompting.Close(seated.Session.App, seated.Shell.InboxSession);
    }

    [Fact]
    public async Task A_follow_up_is_offered_on_a_row_and_never_on_a_heading()
    {
        await using var seated = await Seated.OpenAsync(Ct);

        await seated.AskAsync(Seated.Billing, "billing-talk", Ct, go: false);

        seated.Focus<RoomRegion>();
        await seated.Session.PressThenSettleAsync(new Key(Shell.InboxKey).WithAlt, () => seated.Shell.Settled);

        var inbox = Assert.IsType<InboxWindow>(seated.Shell.InboxShown);
        var lines = inbox.Shown.ToList();

        inbox.Select(lines.FindIndex(line => line.StartsWith("In flight", StringComparison.Ordinal)));
        Assert.Null(inbox.Selected);

        inbox.Select(lines.FindIndex(line => line.Contains("billing-talk", StringComparison.Ordinal)));
        Assert.Equal(Seated.Billing, inbox.Selected?.Agent);
        Assert.True(inbox.Selected!.FollowUp);

        Prompting.Close(seated.Session.App, seated.Shell.InboxSession);
    }

    [Fact]
    public async Task A_message_that_names_an_exchange_opens_it_from_the_transcript()
    {
        await using var seated = await Seated.OpenAsync(Ct);

        var named = await seated.AsksAsync(Seated.Orders, Seated.Billing, "Which ledger?", "ledgers", Ct);

        _ = await seated.AsksAsync(Seated.Orders, seated.Person, $"Billing answered on exchange #{named}; see it.", "notes", Ct);

        await Walking.GoAsync(seated.Shell, Viewing.Conversation, "notes");
        await seated.Shell.Settled;

        var export = await seated.Operators.ExportAsync("notes", Ct);
        var naming = export.Messages.Single(message => message.PayloadJson.Contains("see it", StringComparison.Ordinal));

        Assert.True(seated.Shell.Transcript.Reach(naming.Id));
        Assert.Equal($"Open exchange #{named}", seated.Shell.ReferenceSays);

        seated.Focus<TranscriptRegion>();
        await seated.Session.PressThenSettleAsync(new Key(Shell.ReferenceKey).WithAlt, () => seated.Shell.Settled);

        var lines = Assert.IsType<ExchangeView>(seated.Shell.ExchangeShown).Shown;

        Assert.StartsWith($"Exchange #{named} in ledgers. Open, waiting on {Seated.Billing.Value}.", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, line => line.Contains("Which ledger?", StringComparison.Ordinal));

        Prompting.Close(seated.Session.App, seated.Shell.ExchangeSession);
    }

    [Fact]
    public void Every_line_of_the_exchange_dialog_is_inert()
    {
        var detail = new Parley.Client.Seating.ExchangeDetail(
            3, "room",
            [new Parley.Core.Message(1, 3, Parley.Core.MessageType.Request, Seated.Orders, Seated.Billing, "test",
                "{\"ask\":\"red\\u001b[31m\"}", "room", DateTimeOffset.UnixEpoch, null)],
            new Parley.Core.ExchangeSnapshot(3, Parley.Core.ExchangeState.Open, DateTimeOffset.UnixEpoch, null),
            null);

        Assert.DoesNotContain(ExchangeView.Lines([detail]), line => line.Contains('\u001b', StringComparison.Ordinal));
        Assert.Equal("There is no exchange #9 in this project.", ExchangeView.Lines([new(9, null, [], null, null)])[0]);
    }
}
