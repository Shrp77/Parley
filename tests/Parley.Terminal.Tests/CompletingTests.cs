using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Rendering;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Core;
using Parley.Store;
using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// The completion list on screen, and what the keys do while it is there.
/// </summary>
/// <remarks>
/// <para>
/// <b>Driven, because every question here is about a key arriving.</b> The narrowing itself is covered headless in
/// <c>Parley.Client.Tests</c>; what only a driven test can answer is whether Tab reaches the box at all, whether a
/// printable key reaches the text or is taken by a region's action, and whether the pane has any size to draw in.
/// Each of those was wrong when this file was written.
/// </para>
/// <para>
/// <c>Lists</c> records why: <i>a key tested only in the negative is a key nobody has pressed.</i>
/// </para>
/// </remarks>
public sealed class CompletingTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Conversation = "room";

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    private IReadOnlyList<IDisposable> _held = [];

    public CompletingTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        var agents = new AgentRepository(_connection);

        foreach (var agent in new[] { Orders, Billing })
        {
            agents.UpsertAsync(
                new AgentRecord(agent, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _broker = LoopbackOperator.Start(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        // The driven session, the directory and the seat book, which the fixture below opens and each test holds
        // for its whole run. Kept in one list rather than three fields because none of them is read by a test.
        foreach (var held in _held)
        {
            held.Dispose();
        }

        await _broker.DisposeAsync();

        _connection.Dispose();
    }

    [Fact]
    public async Task The_line_pane_has_a_size_and_the_transcript_keeps_the_rest()
    {
        // **The whole feature was invisible until this passed.** The three-pane arrangement was never written,
        // so the line region kept its default size -- nothing -- while its text was set, read and asserted in
        // memory. A property the suite holds to and the screen never shows.
        var (shell, driving) = await SeatedAsync(TestContext.Current.CancellationToken);

        using var _ = shell;

        // **Laid out first, because the harness never pumps the loop.** 0.25.1 records a probe that reported a
        // list not scrolling by reading a screen nothing had redrawn; a frame is only true after a layout pass,
        // and reading the text is not one -- the room got its fixed width, which resolves without a parent, and
        // everything derived from the space left over was nought.
        driving.App.LayoutAndDraw(true);

        var where = $"room={shell.Room.Frame} transcript={shell.Transcript.Frame} line={shell.Line.Frame} "
            + $"wanted={shell.Line.Rows} panes={shell.Panes.Count} visible={shell.Line.Visible}";

        Assert.True(shell.Line.Frame.Height >= LineRegion.LeastRows, where);
        Assert.True(shell.Line.Frame.Width > 0, "the line got no width");

        // Under the transcript, not beside it, and starting where the transcript ends.
        Assert.Equal(shell.Transcript.Frame.Bottom, shell.Line.Frame.Y);
        Assert.Equal(shell.Transcript.Frame.X, shell.Line.Frame.X);

        // And the room's column is to the left of both.
        Assert.Equal(0, shell.Room.Frame.X);
        Assert.True(shell.Room.Frame.Right <= shell.Line.Frame.X);
        Assert.True(shell.Transcript.Frame.Height > shell.Line.Frame.Height, "the transcript kept the rest");
    }

    [Fact]
    public async Task A_printable_key_reaches_the_text_rather_than_a_region_action()
    {
        // **Every action on this surface is a letter.** `?` opens the actions menu and `x` clears the message
        // line, and both are taken in the shell's own key handler before the focused view sees them -- so a box
        // somebody types prose into has to win while it has focus, or an ask cannot contain a question mark.
        var ct = TestContext.Current.CancellationToken;
        var (shell, _) = await SeatedAsync(ct);
        using var held = shell;

        Focus(shell);

        foreach (var key in "does x work? ")
        {
            shell.NewKeyDownEvent(new Key(key));
        }

        await shell.Settled;

        Assert.Equal("does x work? ", shell.Line.Written);
    }

    [Fact]
    public async Task A_slash_opens_the_command_list_and_narrows_as_it_is_typed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (shell, _) = await SeatedAsync(ct);
        using var held = shell;

        Focus(shell);

        shell.Line.Written = "/";

        await shell.Settled;

        Assert.Contains(shell.Line.Offering, row => row.StartsWith("/add", StringComparison.Ordinal));

        // Each row says what the command is for, which is what makes a command usable unread.
        Assert.Contains(shell.Line.Offering, row => row.Contains("not in this conversation", StringComparison.Ordinal));

        // Nothing matching closes the list rather than showing an empty one.
        shell.Line.Written = "/zzz";

        await shell.Settled;

        Assert.Empty(shell.Line.Offering);

        // And deleting back into a match brings it back, because the list is derived and never remembered.
        shell.Line.Written = "/a";

        await shell.Settled;

        Assert.Contains(shell.Line.Offering, row => row.StartsWith("/add", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tab_completes_the_selection_and_Enter_still_sends()
    {
        // The owner's own correction, twice: "Clause code send on enter, it doesn't auto complete on enter",
        // then "it executes the primary function of the control that's active". The box is what is active.
        var ct = TestContext.Current.CancellationToken;
        var (shell, _) = await SeatedAsync(ct);
        using var held = shell;

        Focus(shell);

        shell.Line.Written = "/a";

        await shell.Settled;

        shell.NewKeyDownEvent(Key.Tab);

        await shell.Settled;

        Assert.Equal("/add ", shell.Line.Written);
    }

    [Fact]
    public async Task With_no_list_Tab_still_moves_to_the_next_region()
    {
        // **Tab's surface-wide meaning is kept, which is why the flip moved off it.** It was advertised in the
        // preview as taking the answer-or-ask-back alternative and bound to a key this surface consumes for
        // navigation -- a key named on screen and doing something else.
        var ct = TestContext.Current.CancellationToken;
        var (shell, _) = await SeatedAsync(ct);
        using var held = shell;

        Focus(shell);

        var before = shell.FocusedRegion;

        shell.NewKeyDownEvent(Key.Tab);

        await shell.Settled;

        Assert.NotEqual(before, shell.FocusedRegion);
    }

    [Fact]
    public async Task An_at_sign_completes_the_name_into_the_box_and_resolves_the_recipient()
    {
        // **The name is filled in, and the recipient is resolved.** The first version resolved the agent and
        // deleted what had been typed: correct, invisible, and reported as Tab doing nothing. What a person is
        // looking at when they press Tab is the thing they typed.
        var ct = TestContext.Current.CancellationToken;
        var (shell, _) = await SeatedAsync(ct);
        using var held = shell;

        Focus(shell);

        shell.Line.Written = "@ord";

        await shell.Settled;

        Assert.Contains(shell.Line.Offering, row => row.Contains(Orders.Value, StringComparison.Ordinal));

        shell.NewKeyDownEvent(Key.Tab);

        await shell.Settled;

        Assert.Equal($"@{Orders.Value} ", shell.Line.Written);
        Assert.Equal([Orders], shell.Drafting?.Recipients);
        Assert.Contains(Orders.Value, shell.Line.Preview, StringComparison.Ordinal);

        // And it is addressing rather than words, so it never reaches a payload.
        Assert.Equal(string.Empty, shell.Drafting!.Says(shell.Line.Written));
    }

    [Fact]
    public async Task Add_widens_the_recipients_to_somebody_who_is_not_a_participant()
    {
        var ct = TestContext.Current.CancellationToken;
        var (shell, _) = await SeatedAsync(ct);
        using var held = shell;

        Focus(shell);

        // Billing is in the project's roster and not in this conversation, so `/add` is what reaches it.
        Assert.DoesNotContain(Billing, shell.Drafting?.Recipients ?? []);

        shell.Line.Written = $"/{"add"} @bill";

        await shell.Settled;

        Assert.Contains(shell.Line.Offering, row => row.Contains(Billing.Value, StringComparison.Ordinal));

        shell.NewKeyDownEvent(Key.Tab);

        await shell.Settled;

        // **The command goes and the chip stays.** `/add` has done its work the moment the agent resolved,
        // so leaving it in the box would be a command nothing consumed at the next Enter -- and the chip is
        // what says who was added.
        Assert.Equal($"@{Billing.Value} ", shell.Line.Written);
        Assert.Contains(Billing, shell.Drafting!.Recipients);
        Assert.Contains(Orders, shell.Drafting!.Recipients);

        // An agent that is not a participant has no pair state, so it gets a Request -- which is how an agent
        // joins a conversation at all, membership being derived (§5.0).
        Assert.Contains(
            shell.Drafting!.Plan(),
            addressed => addressed.Peer == Billing && addressed.Act == Act.Ask);
    }

    [Fact]
    public async Task A_draft_command_sends_nothing_on_its_own_and_says_why()
    {
        var ct = TestContext.Current.CancellationToken;
        var (shell, _) = await SeatedAsync(ct);
        using var held = shell;

        Focus(shell);

        shell.Line.Written = "/add";
        shell.Line.Accept();

        await shell.Settled;

        var export = await new BrokerClient(_broker.Operators).ExportAsync(Conversation, ct);

        Assert.Single(export?.Messages ?? []);
        Assert.Contains("needs 1 thing", shell.Said, StringComparison.Ordinal);
        Assert.Equal("/add", shell.Line.Written);
    }

    [Fact]
    public async Task An_unresolved_slash_is_text_and_a_near_miss_is_named_before_Enter()
    {
        var ct = TestContext.Current.CancellationToken;
        var (shell, _) = await SeatedAsync(ct);
        using var held = shell;

        Focus(shell);

        shell.Line.Written = "/ad";

        await shell.Settled;

        Assert.Contains("did you mean /add", shell.Line.Preview, StringComparison.Ordinal);

        // A path is text with no near miss at all, decided without reference to what any command is called.
        shell.Line.Written = "/etc/hosts";

        await shell.Settled;

        Assert.DoesNotContain("did you mean", shell.Line.Preview, StringComparison.Ordinal);
        Assert.Empty(shell.Line.Offering);
    }

    [Fact]
    public async Task There_is_no_answer_or_ask_back_choice_any_more()
    {
        // **Removed on the owner's call of 2026-10-02.** F2 took "the other side of the one choice a draft offers" —
        // answer, or ask back instead — and the owner's reading was that it made no sense: a conversation never really
        // stops. Holding a request, what somebody types answers it; nothing offers to flip that.
        var ct = TestContext.Current.CancellationToken;
        var (shell, _) = await SeatedAsync(ct);
        using var held = shell;

        Focus(shell);

        var before = shell.Line.Preview;

        shell.NewKeyDownEvent(Key.F2);

        await shell.Settled;

        Assert.Equal(before, shell.Line.Preview);
        Assert.Equal(Act.Answer, Assert.Single(shell.Drafting!.Plan()).Act);
        Assert.DoesNotContain(shell.Actions(), action => action.Chord is { } chord && chord == Key.F2);
    }

    /// <summary>Puts the cursor in the box, which is where every key in this file is pressed.</summary>
    /// <param name="shell">The surface.</param>
    private static void Focus(Shell shell)
    {
        shell.Focus(shell.Panes.ToList().FindIndex(pane => pane is LineRegion));

        Assert.True(shell.Line.Editing, "the box did not take focus");
    }

    private BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(Walking.Alpha, "unused.db", _broker.Address, null)],
            _ => GrpcChannel.ForAddress(_broker.Address));

    private async Task<(Shell Shell, Driving.Session Driving)> SeatedAsync(CancellationToken ct)
    {
        var driving = Driving.Open(width: 140, height: 40);
        var directory = Directory();
        var seats = new SeatBook(directory.AgentsFor);
        var shell = new Shell(directory) { Seats = seats, MailEvery = null, FollowEvery = null };

        driving.Show(shell);
        await shell.RefreshAsync(ct);
        await shell.Settled;

        await Walking.GoAsync(shell, Viewing.Project);
        // Seated by opening the project (specs/023): the broker provides the person seat and the shell takes it, so
        // there is no Join dialog to drive.
        await shell.Settled;

        // Seeded from a peer, because membership is derived: a conversation with no traffic has no participants.
        var broker = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await broker.StartAsync(Orders, T0, ct);

        var sent = await broker.RequestAsync(
            Orders, shell.Held!.Seat.Name, "model-a", """{"ask":"is the build green?"}""",
            T0.AddMinutes(1), Conversation, ct);

        Assert.True(sent.Exchange is not null, $"the fixture's request was refused: {sent.Admission.Rejection}");

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.Settled;

        _held = [driving, directory, seats];

        return (shell, driving);
    }

    [Fact]
    public async Task Sending_is_offered_where_a_person_would_look_for_it()
    {
        // `contracts/views.md`: no action exists only as a key, and whatever a key does a pointer reaches too. This was
        // both line-pane actions until the answer-or-ask-back flip went on 2026-10-02.
        var (shell, _) = await SeatedAsync(TestContext.Current.CancellationToken);

        using var held = shell;

        Focus(shell);

        Assert.Contains(shell.Actions(), action => action.Label == Shell.SendTitle);
    }

    [Fact]
    public async Task A_long_message_grows_the_box_rather_than_scrolling_sideways()
    {
        // **The owner's report: a long message slid out of sight a character at a time.** A single-line field
        // scrolls horizontally, so the only way to read your own message back was to walk the cursor through
        // it. Rule 8.4's cap is eight thousand characters and this is the box those are written in.
        var (shell, driving) = await SeatedAsync(TestContext.Current.CancellationToken);

        using var held = shell;

        Focus(shell);

        driving.App.LayoutAndDraw(true);

        var before = shell.Line.Rows;

        shell.Line.Written = new string('a', 600);

        await shell.Settled;

        driving.App.LayoutAndDraw(true);

        Assert.True(
            shell.Line.Rows > before,
            $"the box stayed at {shell.Line.Rows} rows for six hundred characters");
    }

    [Fact]
    public async Task And_the_transcript_keeps_its_floor_however_long_the_message_is()
    {
        // A floor under the conversation rather than a ceiling on the message: somebody writing into a
        // conversation is still somebody reading one.
        var (shell, driving) = await SeatedAsync(TestContext.Current.CancellationToken);

        using var held = shell;

        Focus(shell);

        shell.Line.Written = new string('a', MessageLimits.MaxPayloadCharacters);

        await shell.Settled;

        driving.App.LayoutAndDraw(true);

        Assert.True(
            shell.Transcript.Frame.Height >= LineRegion.TranscriptFloor,
            $"the transcript was cut to {shell.Transcript.Frame.Height} rows");
    }

    [Fact]
    public async Task What_is_owed_is_listed_and_reading_it_moves_the_transcript_to_where_it_was_said()
    {
        // **A question is read in its context rather than in a list**, which is the whole reason this column
        // names messages instead of restating them.
        var (shell, driving) = await SeatedAsync(TestContext.Current.CancellationToken);

        using var held = shell;

        driving.App.LayoutAndDraw(true);

        var owed = Assert.Single(shell.Room.Written, row => row.Contains(Orders.Value, StringComparison.Ordinal)
            && row.Contains("waiting for your reply", StringComparison.Ordinal));

        Assert.NotEmpty(owed);

        shell.Focus(shell.Panes.ToList().FindIndex(pane => pane is RoomRegion));
        driving.Row(shell.Room.List, "waiting for your reply");

        var item = shell.Room.Selected;

        Assert.NotNull(item);

        // The message it points at is the peer's own, in the exchange this person holds.
        Assert.NotNull(item!.At);

        shell.Room.List.NewKeyDownEvent(Key.Enter);

        await shell.Settled;

        // **The cursor is on the message itself, not on the row used to scroll to it.** Reaching selects a
        // couple of rows earlier first, so the list scrolls those into view and the message sits below them
        // rather than against the top edge -- and the second selection is what must win.
        Assert.Equal(shell.Panes.ToList().IndexOf(shell.Transcript), shell.FocusedRegion);

        var at = shell.Transcript.List.SelectedItem;

        Assert.NotNull(at);
        Assert.Equal(
            shell.Transcript.Rows.ToList().IndexOf(item.At!.Value),
            at);

        // And a message within the first few rows is still reachable, rather than being refused for having
        // too little above it.
        Assert.True(at < TranscriptRegion.Neighbours + 2, $"the fixture's only message was at row {at}");
        Assert.Contains(
            "is the build green?",
            shell.Transcript.Written,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_row_pointing_at_nothing_says_so_rather_than_moving_the_reader()
    {
        // A transcript refilled since the column was drawn is an ordinary race. Scrolling somewhere arbitrary
        // would answer "where was this said" with somewhere nothing was.
        var (shell, driving) = await SeatedAsync(TestContext.Current.CancellationToken);

        using var held = shell;

        driving.App.LayoutAndDraw(true);

        shell.Transcript.Clear();

        Assert.False(shell.Transcript.Reach(1), "an empty transcript reached a message");
    }

    [Fact]
    public async Task Every_row_of_the_transcript_knows_which_message_it_belongs_to()
    {
        // Carried on the line rather than worked out by counting messages, which would be a second opinion
        // about a layout the renderer already decides.
        var ct = TestContext.Current.CancellationToken;
        var (shell, _) = await SeatedAsync(ct);

        using var held = shell;

        var export = await new BrokerClient(_broker.Operators).ExportAsync(Conversation, ct);
        var message = Assert.Single(export!.Messages);

        var lines = Parley.Client.Rendering.Reading.Lines(export.Messages);

        Assert.Contains(lines, line => line.Message == message.Id && line.Kind == LineKind.Header);
        Assert.Contains(lines, line => line.Message == message.Id && line.Kind == LineKind.Body);

        // And a line this surface wrote itself belongs to no message, so nothing can scroll to it.
        Assert.All(
            lines.Where(line => line.Kind == LineKind.Blank),
            line => Assert.Null(line.Message));
    }

    [Fact]
    public async Task Typing_an_at_sign_and_pressing_Tab_fills_the_name_in()
    {
        // **Typed rather than assigned, because that is how the report arrived.** Setting the text and
        // pressing Tab passes over the whole path a person uses: the field receives every character, and this
        // surface takes Tab before the field sees it.
        var ct = TestContext.Current.CancellationToken;
        var (shell, _) = await SeatedAsync(ct);
        using var held = shell;

        Focus(shell);

        foreach (var key in "@ord")
        {
            shell.NewKeyDownEvent(new Key(key));
        }

        await shell.Settled;

        Assert.NotEmpty(shell.Line.Offering);

        shell.NewKeyDownEvent(Key.Tab);

        await shell.Settled;

        Assert.Equal($"@{Orders.Value} ", shell.Line.Written);
        Assert.Equal([Orders], shell.Drafting?.Recipients);
    }

    [Fact]
    public async Task What_reaches_the_broker_is_the_words_and_never_the_addressing()
    {
        // §13 delivers a payload inside a framing block telling the receiver that anything in it claiming
        // authority is inert data, so a routing directive in the words is the one thing the envelope exists to
        // have ignored. The name is on screen; it is not in the message.
        var ct = TestContext.Current.CancellationToken;
        var (shell, _) = await SeatedAsync(ct);
        using var held = shell;

        Focus(shell);

        foreach (var key in "@ord")
        {
            shell.NewKeyDownEvent(new Key(key));
        }

        await shell.Settled;

        shell.NewKeyDownEvent(Key.Tab);

        await shell.Settled;

        foreach (var key in "the build is green")
        {
            shell.NewKeyDownEvent(new Key(key));
        }

        await shell.Settled;

        shell.Line.Accept();

        await shell.Settled;

        var export = await new BrokerClient(_broker.Operators).ExportAsync(Conversation, ct);
        var said = Assert.Single(
            export?.Messages ?? [],
            m => m.PayloadJson.Contains("the build is green", StringComparison.Ordinal));

        Assert.DoesNotContain("@", said.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_name_written_by_hand_stays_in_the_sentence()
    {
        // Removed by the record of what a completion wrote, never by hunting prose for anything that looks
        // like addressing. Somebody quoting an agent's name is quoting it.
        var ct = TestContext.Current.CancellationToken;
        var (shell, _) = await SeatedAsync(ct);
        using var held = shell;

        Focus(shell);

        shell.Line.Written = $"ask @{Orders.Value} about it";

        await shell.Settled;

        Assert.Equal($"ask @{Orders.Value} about it", shell.Drafting!.Says(shell.Line.Written));
    }

    [Fact]
    public async Task Enter_actually_sends_when_the_key_is_pressed()
    {
        // **Nothing in the suite had ever pressed this key.** Every test called the `Accept()` seam, which
        // raises the same event from the other side -- so the one thing that seam exists to check, that the
        // key arrives at all, was the one thing unchecked. The owner reported the obvious consequence.
        var ct = TestContext.Current.CancellationToken;
        var (shell, _) = await SeatedAsync(ct);
        using var held = shell;

        Focus(shell);

        foreach (var key in "the build is green")
        {
            shell.NewKeyDownEvent(new Key(key));
        }

        await shell.Settled;

        shell.NewKeyDownEvent(Key.Enter);

        await shell.Settled;

        var export = await new BrokerClient(_broker.Operators).ExportAsync(Conversation, ct);

        Assert.Contains(
            export?.Messages ?? [],
            m => m.PayloadJson.Contains("the build is green", StringComparison.Ordinal));

        Assert.Equal(string.Empty, shell.Line.Written);
    }

    [Fact]
    public async Task A_backslash_then_Enter_adds_a_line_rather_than_sending()
    {
        // The spelling that works in every terminal. Several deliver Ctrl+Enter, Shift+Enter and a bare
        // Enter as the same bytes, so the chord is offered and this is the one that cannot be taken away.
        var ct = TestContext.Current.CancellationToken;
        var (shell, _) = await SeatedAsync(ct);
        using var held = shell;

        Focus(shell);

        foreach (var key in "first" + LineRegion.Breaking)
        {
            shell.NewKeyDownEvent(new Key(key));
        }

        await shell.Settled;

        shell.NewKeyDownEvent(Key.Enter);

        await shell.Settled;

        // The break itself, however the view spells a line ending, and the backslash gone with it.
        Assert.StartsWith("first", shell.Line.Written, StringComparison.Ordinal);
        Assert.DoesNotContain(LineRegion.Breaking, shell.Line.Written, StringComparison.Ordinal);
        Assert.EndsWith("\n", shell.Line.Written.ReplaceLineEndings("\n"), StringComparison.Ordinal);

        var export = await new BrokerClient(_broker.Operators).ExportAsync(Conversation, ct);

        // One message, the fixture's own: nothing was sent.
        Assert.Single(export?.Messages ?? []);
    }

    [Fact]
    public async Task The_chord_that_adds_a_line_is_named_where_a_person_would_look()
    {
        // `contracts/views.md`: no action exists only as a key. The other spelling is in the menu beside it,
        // because a chord several terminals cannot deliver needs its fallback written down.
        var (shell, _) = await SeatedAsync(TestContext.Current.CancellationToken);

        using var held = shell;

        Focus(shell);

        var offered = Assert.Single(shell.Actions(), a => a.Label == Shell.BreakTitle);

        Assert.Equal(Key.Enter.WithCtrl.ToString(), offered.Stroke);
        Assert.Equal(Shell.BreakAlso, offered.Costs);
    }
}
