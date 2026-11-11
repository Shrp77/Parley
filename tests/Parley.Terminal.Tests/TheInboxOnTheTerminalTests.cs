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
    public async Task A_message_naming_an_exchange_that_does_not_exist_opens_one_that_says_so()
    {
        // Without the guard the broker's empty answer for an exchange it does not have was read as a conversation, and the
        // terminal said "Could not read the exchanges: A conversation identifier is required" instead of that there is no
        // such exchange (review, lens 3 round 36).
        await using var seated = await Seated.OpenAsync(Ct);

        _ = await seated.AsksAsync(Seated.Orders, seated.Person, "Billing answered on exchange #9999; see it.", "notes", Ct);

        await Walking.GoAsync(seated.Shell, Viewing.Conversation, "notes");
        await seated.Shell.Settled;

        var export = await seated.Operators.ExportAsync("notes", Ct);
        var naming = export.Messages.Single(message => message.PayloadJson.Contains("see it", StringComparison.Ordinal));

        Assert.True(seated.Shell.Transcript.Reach(naming.Id));

        seated.Focus<TranscriptRegion>();
        await seated.Session.PressThenSettleAsync(new Key(Shell.ReferenceKey).WithAlt, () => seated.Shell.Settled);

        var shown = seated.Shell.ExchangeShown;

        if (shown is not null)
        {
            Prompting.Close(seated.Session.App, seated.Shell.ExchangeSession);
        }

        var lines = Assert.IsType<ExchangeView>(shown).Shown;

        Assert.Equal("There is no exchange #9999 in this project.", lines[0]);
    }

    [Fact]
    public async Task A_message_naming_two_exchanges_offers_them_both_and_never_names_only_the_first()
    {
        // Without the guard a message naming two exchanges said "Open exchange #<first>" on the status line, though the key
        // opens both (review, lens 3 round 34).
        await using var seated = await Seated.OpenAsync(Ct);

        var first = await seated.AsksAsync(Seated.Orders, Seated.Billing, "Which ledger?", "ledgers", Ct);
        var second = await seated.AsksAsync(Seated.Orders, Seated.Billing, "Which period?", "periods", Ct);

        _ = await seated.AsksAsync(
            Seated.Orders, seated.Person, $"See exchange #{first} and exchange #{second}; both matter.", "notes", Ct);

        await Walking.GoAsync(seated.Shell, Viewing.Conversation, "notes");
        await seated.Shell.Settled;

        var export = await seated.Operators.ExportAsync("notes", Ct);
        var naming = export.Messages.Single(message => message.PayloadJson.Contains("both matter", StringComparison.Ordinal));

        Assert.True(seated.Shell.Transcript.Reach(naming.Id));
        Assert.Equal(2, seated.Shell.ReferencesUnder.Select(found => found.Exchange).Distinct().Count());
        Assert.Equal(Shell.ReferenceTitle, seated.Shell.ReferenceSays);
    }

    [Fact]
    public async Task An_inbox_read_that_lands_after_the_person_moved_to_another_project_opens_nothing()
    {
        // Without the guard the Inbox read for alpha opened over beta, the project the person had moved to while it was read
        // (review, lens 3 round 34).
        await using var seated = await Seated.OpenAsync(Ct, withBeta: true);

        await seated.AskAsync(Seated.Billing, "billing-talk", Ct, go: false);

        // Holds the work queue, so the Inbox's read waits behind it until the person has moved.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        seated.Shell.Queued(_ => gate.Task);

        seated.Focus<RoomRegion>();
        seated.Session.Press(new Key(Shell.InboxKey).WithAlt);

        await seated.Shell.GoAsync(Parley.Client.Views.ViewPlace.Into(Seated.Beta));

        gate.SetResult();
        await seated.Shell.Settled;

        var shown = seated.Shell.InboxShown;

        if (shown is not null)
        {
            Prompting.Close(seated.Session.App, seated.Shell.InboxSession);
        }

        Assert.Null(shown);
        Assert.Equal(Seated.Beta, seated.Shell.Place.Project);
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

    [Fact]
    public void An_empty_view_says_nothing_a_pruned_row_says_its_words_were_removed_and_one_day_is_singular()
    {
        // Drawn, not read off the lines held in memory: an empty view says "Nothing." under its heading, a row whose words
        // retention removed says so instead of ending blank, and a window of one day reads "1 day".
        var reading = new Parley.Client.Seating.InboxReading(
            [
                new Parley.Client.Seating.InboxRow(
                    5, "billing-talk", Seated.Billing, Parley.Client.Seating.InboxView.WaitingOnYou,
                    DateTimeOffset.UnixEpoch.AddHours(2), null, null, null),
            ],
            1,
            0,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddDays(1));

        using var driving = Driving.Open();

        var (_, token) = InboxWindow.Open(driving.App, Walking.Alpha.Value, reading);

        driving.App.LayoutAndDraw(true);

        var screen = driving.Text();

        Prompting.Close(driving.App, token);

        Assert.Contains("owes you an answer. its words were removed by retention", screen, StringComparison.Ordinal);
        Assert.Contains("In flight (0)", screen, StringComparison.Ordinal);
        Assert.Contains("Closed in the last 1 day (0)", screen, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(screen, @"^\W*Nothing\.", System.Text.RegularExpressions.RegexOptions.Multiline).Count);
    }

    [Fact]
    public void A_rows_words_are_cut_at_a_hundred_characters_and_marked_as_cut()
    {
        // The row's line is wider than the screen, so the list's own line is read: what the list was given to draw.
        var words = string.Concat(Enumerable.Range(0, 30).Select(i => $"w{i:D2} "));
        var said = new Parley.Core.Message(
            1, 5, Parley.Core.MessageType.Clarify, Seated.Billing, Seated.Orders, "test",
            $$"""{"text":"{{words}}"}""", "billing-talk", DateTimeOffset.UnixEpoch, null);
        var reading = new Parley.Client.Seating.InboxReading(
            [
                new Parley.Client.Seating.InboxRow(
                    5, "billing-talk", Seated.Billing, Parley.Client.Seating.InboxView.WaitingOnYou,
                    DateTimeOffset.UnixEpoch.AddHours(2), said, null, null),
            ],
            7,
            0,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddDays(7));

        using var driving = Driving.Open();

        var (inbox, token) = InboxWindow.Open(driving.App, Walking.Alpha.Value, reading);

        driving.App.LayoutAndDraw(true);

        var line = inbox.Shown.Single(shown => shown.Contains("billing-talk", StringComparison.Ordinal));

        Prompting.Close(driving.App, token);

        Assert.True(words.Trim().Length > 100);
        Assert.EndsWith(". " + words[..100] + "…", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_follow_up_is_live_on_an_agents_row_and_never_on_the_facilitators()
    {
        // Without the guard the follow-up was offered on the facilitator's row, which is never given work, so the status
        // line named a command that could only do nothing (review, lens 3 round 35).
        var facilitator = Parley.Core.Facilitation.On(Walking.Alpha);
        var waiting = Parley.Client.Seating.InboxView.WaitingOnYou;
        var reading = new Parley.Client.Seating.InboxReading(
            [
                new Parley.Client.Seating.InboxRow(5, "billing-talk", Seated.Billing, waiting, DateTimeOffset.UnixEpoch.AddHours(2), null, null, null),
                new Parley.Client.Seating.InboxRow(6, "asking", facilitator, waiting, DateTimeOffset.UnixEpoch.AddHours(1), null, null, null),
            ],
            7,
            0,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddDays(7));

        using var driving = Driving.Open();

        var (inbox, token) = InboxWindow.Open(driving.App, Walking.Alpha.Value, reading);

        driving.App.LayoutAndDraw(true);

        var lines = inbox.Shown.ToList();

        bool Offered() => inbox.Table.Live(KeyContext.InboxDialog, inbox).Any(action => action.Label == InboxWindow.FollowUpTitle);

        inbox.Select(lines.FindIndex(line => line.Contains($"{facilitator.Value} in asking", StringComparison.Ordinal)));
        Assert.Equal(facilitator, inbox.Selected?.Agent);
        Assert.False(Offered(), "the follow-up is offered on the facilitator's row");

        inbox.Select(lines.FindIndex(line => line.Contains($"{Seated.Billing.Value} in billing-talk", StringComparison.Ordinal)));
        Assert.Equal(Seated.Billing, inbox.Selected?.Agent);
        Assert.True(Offered(), "the follow-up is not offered on an agent's row");

        Prompting.Close(driving.App, token);
    }
}
