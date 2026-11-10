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
    public async Task The_closed_window_is_the_one_the_deployment_keeps()
    {
        // The settings page saves it "here and in the terminal"; a terminal that ignored settings.json showed the default
        // week with every test green (review, lens 3 round 19).
        var root = Directory.CreateTempSubdirectory("parley-terminal-window").FullName;

        try
        {
            Assert.Null(Parley.Configuration.ParleySettings.WriteClosedDays(root, 30));

            await using var seated = await Seated.OpenAsync(Ct, root: root);

            seated.Focus<RoomRegion>();
            await seated.Session.PressThenSettleAsync(new Key(Shell.InboxKey).WithAlt, () => seated.Shell.Settled);

            var lines = Assert.IsType<InboxWindow>(seated.Shell.InboxShown).Shown;

            Assert.Contains(lines, line => line.StartsWith("Closed in the last 30 days", StringComparison.Ordinal));

            Prompting.Close(seated.Session.App, seated.Shell.InboxSession);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_follow_up_goes_through_the_inboxs_own_project_whatever_is_in_view()
    {
        // The follow-up's sender was built from the project in view when the key was pressed. Moved off the project, it
        // found none and refused; moved to another project, it sent through that project's broker into its conversation
        // of the same name. It now speaks for the Inbox's own project.
        await using var seated = await Seated.OpenAsync(Ct, withBeta: true);

        await seated.AskAsync(Seated.Billing, "billing-talk", Ct, go: false);

        seated.Focus<RoomRegion>();
        await seated.Session.PressThenSettleAsync(new Key(Shell.InboxKey).WithAlt, () => seated.Shell.Settled);

        var inbox = Assert.IsType<InboxWindow>(seated.Shell.InboxShown);

        inbox.Select(inbox.Shown.ToList().FindIndex(line => line.Contains("billing-talk", StringComparison.Ordinal)));

        await seated.Shell.GoAsync(Parley.Client.Views.ViewPlace.Into(Seated.Beta));
        await seated.Shell.Settled;
        seated.Lay();
        await seated.Shell.Settled;

        inbox.NewKeyDownEvent(new Key(InboxWindow.FollowUpKey).WithAlt);

        var typing = (TextDialog)seated.Session.Find(InboxWindow.FollowUpTitle);

        seated.Session.Type("any news?");
        typing.Finish(taken: true);
        await seated.Shell.Settled;

        var export = await seated.Operators.ExportAsync("billing-talk", Ct);

        Assert.Contains(export.Messages, m => m.To == Seated.Billing && m.PayloadJson.Contains("any news?", StringComparison.Ordinal));
        Assert.DoesNotContain(
            (await seated.BetaOperators.ExportAsync("billing-talk", Ct)).Messages,
            m => m.PayloadJson.Contains("any news?", StringComparison.Ordinal));

        Prompting.Close(seated.Session.App, seated.Shell.InboxSession);
    }

    [Fact]
    public async Task Enter_on_a_row_goes_to_its_conversation_and_the_next_line_is_sent_there()
    {
        // Enter read the row's conversation into the transcript but left the place and the box on the one the person was
        // in, so the next line went to billing in the room, pulling billing into it (review, lens 1 round 25).
        await using var seated = await Seated.OpenAsync(Ct);

        await seated.AskAsync(Seated.Billing, "billing-talk", Ct, go: false);

        seated.Focus<RoomRegion>();
        await seated.Session.PressThenSettleAsync(new Key(Shell.InboxKey).WithAlt, () => seated.Shell.Settled);

        var inbox = Assert.IsType<InboxWindow>(seated.Shell.InboxShown);

        inbox.Select(inbox.Shown.ToList().FindIndex(line => line.Contains("billing-talk", StringComparison.Ordinal)));
        await seated.Session.PressThenSettleAsync(Key.Enter, () => seated.Shell.Settled);
        await seated.Shell.Settled;

        Assert.Equal(Viewing.Conversation, seated.Shell.Place.Viewing);
        Assert.Equal("billing-talk", seated.Shell.Place.Title);

        seated.Shell.Line.Written = "is the ledger closed?";
        seated.Shell.Touched();
        seated.Shell.Line.Accept();
        await seated.Shell.Settled;

        Assert.Contains(
            (await seated.Operators.ExportAsync("billing-talk", Ct)).Messages,
            m => m.PayloadJson.Contains("is the ledger closed?", StringComparison.Ordinal));
        Assert.DoesNotContain(
            (await seated.Operators.ExportAsync(Seated.Conversation, Ct)).Messages,
            m => m.PayloadJson.Contains("is the ledger closed?", StringComparison.Ordinal));
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

    [Fact]
    public void An_exchange_whose_words_were_pruned_says_so()
    {
        // Without the line a pruned exchange showed its heading and nothing under it, as though it had never been said
        // (review, lens 3 round 31).
        var pruned = new Parley.Client.Seating.ExchangeDetail(
            4, "room", [],
            new Parley.Core.ExchangeSnapshot(4, Parley.Core.ExchangeState.Closed, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            null);

        var lines = ExchangeView.Lines([pruned]);

        Assert.StartsWith("Exchange #4 in room.", lines[0], StringComparison.Ordinal);
        Assert.Contains("  Its words were removed by retention.", lines);
    }

    [Fact]
    public async Task A_follow_up_with_no_seat_to_send_it_from_says_so()
    {
        // Without the guard the follow-up box opened with no seat behind it, and what was typed went nowhere (review, lens 3
        // round 31).
        await using var seated = await Seated.OpenAsync(Ct, withBeta: true);

        await seated.AskAsync(Seated.Billing, "billing-talk", Ct, go: false);

        seated.Focus<RoomRegion>();
        await seated.Session.PressThenSettleAsync(new Key(Shell.InboxKey).WithAlt, () => seated.Shell.Settled);

        var inbox = Assert.IsType<InboxWindow>(seated.Shell.InboxShown);

        inbox.Select(inbox.Shown.ToList().FindIndex(line => line.Contains("billing-talk", StringComparison.Ordinal)));

        // Moved to another project, with the Inbox's project's hold dropped as an idle surface drops it.
        await seated.Shell.GoAsync(Parley.Client.Views.ViewPlace.Into(Seated.Beta));
        await seated.Shell.Settled;
        seated.Shell.Seats!.Sweep(DateTimeOffset.UtcNow.AddDays(1));
        Assert.Null(seated.Shell.Seats.Held(Walking.Alpha.Value));

        inbox.NewKeyDownEvent(new Key(InboxWindow.FollowUpKey).WithAlt);
        await seated.Shell.Settled;

        Assert.Equal("A follow-up goes out as you, and you are not seated in this project.", seated.Shell.Said);

        Prompting.Close(seated.Session.App, seated.Shell.InboxSession);
    }

    [Fact]
    public async Task A_follow_up_with_no_words_sends_nothing_and_says_so()
    {
        // Without the guard an empty follow-up reached SeatVoice, which refused it itself before anything went to the broker,
        // in its own words, "Nothing was asked for", which reads as though the person asked for nothing rather than wrote
        // nothing (review, lens 3 round 31).
        await using var seated = await Seated.OpenAsync(Ct);

        await seated.AskAsync(Seated.Billing, "billing-talk", Ct, go: false);

        seated.Focus<RoomRegion>();
        await seated.Session.PressThenSettleAsync(new Key(Shell.InboxKey).WithAlt, () => seated.Shell.Settled);

        var inbox = Assert.IsType<InboxWindow>(seated.Shell.InboxShown);

        inbox.Select(inbox.Shown.ToList().FindIndex(line => line.Contains("billing-talk", StringComparison.Ordinal)));

        var before = (await seated.Operators.ExportAsync("billing-talk", Ct)).Messages.Count;

        inbox.NewKeyDownEvent(new Key(InboxWindow.FollowUpKey).WithAlt);

        var typing = (TextDialog)seated.Session.Find(InboxWindow.FollowUpTitle);

        typing.Finish(taken: true);
        await seated.Shell.Settled;

        Assert.Equal("Nothing was written, so nothing was sent.", seated.Shell.Said);
        Assert.Equal(before, (await seated.Operators.ExportAsync("billing-talk", Ct)).Messages.Count);

        Prompting.Close(seated.Session.App, seated.Shell.InboxSession);
    }
}
