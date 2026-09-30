using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// The room's column as a checklist: who is here, who this message is going to, and taking a seat.
/// </summary>
/// <remarks>
/// <para>
/// All of this is the owner's first morning in the room. The seat was reachable only from the input line,
/// where <c>j</c> types a <c>j</c>; the person's own name was somewhere in a list of agents; and who a message
/// was addressed to could only be changed by typing <c>@</c> into the box.
/// </para>
/// <para>
/// <b>Driven, because every question here is about a key arriving at a pane.</b> The narrowing itself is
/// covered headless in <c>Parley.Client.Tests</c>.
/// </para>
/// </remarks>
public sealed class AddressingFromTheRoomTests
{
    [Fact]
    public async Task The_person_is_on_their_own_line_above_the_agents()
    {
        await using var seated = await Seated.OpenAsync(TestContext.Current.CancellationToken);

        var rows = seated.Shell.Room.Written;
        var you = rows.ToList().FindIndex(row => row == RoomRegion.You);
        var here = rows.ToList().FindIndex(row => row == RoomRegion.Participants);

        Assert.True(you >= 0, "the room never said which row is you");
        Assert.True(you < here, "the seat was listed among the agents rather than above them");
        Assert.Contains(Seated.Me, rows[you + 1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_participant_starts_ticked_because_typing_goes_to_the_room()
    {
        await using var seated = await Seated.OpenAsync(TestContext.Current.CancellationToken);

        Assert.Contains(
            seated.Shell.Room.Written,
            row => row.StartsWith(RoomRegion.Addressed, StringComparison.Ordinal)
                && row.Contains(Seated.Orders.Value, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unticking_a_participant_takes_them_off_what_is_about_to_be_sent()
    {
        // The column and the box are two ways at one set. A checklist that kept its own idea of who a
        // message was going to would be a second answer to the question the preview already answers.
        await using var seated = await Seated.OpenAsync(TestContext.Current.CancellationToken);
        var shell = seated.Shell;

        // A second participant, so there is something to leave addressed.
        shell.Drafting!.Widen(Seated.Billing);
        shell.Room.Address(shell.Drafting!.Recipients);

        seated.Focus<RoomRegion>();
        seated.Row(shell.Room.List, Seated.Orders.Value);

        shell.Room.List.NewKeyDownEvent(Key.Enter);

        await shell.Settled;

        Assert.DoesNotContain(Seated.Orders, shell.Drafting!.Recipients);
        Assert.Contains(Seated.Billing, shell.Drafting!.Recipients);

        Assert.Contains(
            shell.Room.Written,
            row => row.StartsWith(RoomRegion.NotAddressed, StringComparison.Ordinal)
                && row.Contains(Seated.Orders.Value, StringComparison.Ordinal));

        // And the preview above the box agrees, because it is the same draft.
        Assert.DoesNotContain(Seated.Orders.Value, shell.Line.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ticking_one_back_on_addresses_it_again()
    {
        await using var seated = await Seated.OpenAsync(TestContext.Current.CancellationToken);
        var shell = seated.Shell;

        shell.Drafting!.Widen(Seated.Billing);
        shell.Room.Address(shell.Drafting!.Recipients);

        seated.Focus<RoomRegion>();
        seated.Row(shell.Room.List, Seated.Orders.Value);

        shell.Room.List.NewKeyDownEvent(Key.Enter);
        await shell.Settled;

        shell.Room.List.NewKeyDownEvent(Key.Enter);
        await shell.Settled;

        Assert.Contains(Seated.Orders, shell.Drafting!.Recipients);
    }

    [Fact]
    public async Task The_last_recipient_cannot_be_unticked_and_the_refusal_says_why()
    {
        // A message addressed to nobody is not a state worth being in: the box would go on saying there is
        // nobody in this conversation while somebody typed into it.
        await using var seated = await Seated.OpenAsync(TestContext.Current.CancellationToken);
        var shell = seated.Shell;

        Assert.Single(shell.Drafting!.Recipients);

        seated.Focus<RoomRegion>();
        seated.Row(shell.Room.List, Seated.Orders.Value);

        shell.Room.List.NewKeyDownEvent(Key.Enter);

        await shell.Settled;

        Assert.Contains(Seated.Orders, shell.Drafting!.Recipients);
        Assert.Equal(RoomRegion.LastOne, shell.Said);
    }

    [Fact]
    public async Task Space_does_the_same_thing_and_is_named_where_a_person_would_look()
    {
        // `contracts/views.md`: no action exists only as a key. The chord is offered only on a row it would
        // do something to, which is the other half of that rule.
        await using var seated = await Seated.OpenAsync(TestContext.Current.CancellationToken);
        var shell = seated.Shell;

        shell.Drafting!.Widen(Seated.Billing);
        shell.Room.Address(shell.Drafting!.Recipients);

        seated.Focus<RoomRegion>();
        seated.Row(shell.Room.List, Seated.Orders.Value);

        var offered = Assert.Single(shell.Actions(), a => a.Label == Shell.IncludeTitle);

        Assert.Equal(Key.Space.ToString(), offered.Stroke);

        shell.NewKeyDownEvent(Key.Space);

        await shell.Settled;

        Assert.DoesNotContain(Seated.Orders, shell.Drafting!.Recipients);
    }

    [Fact]
    public async Task Enter_on_something_owed_still_reads_it_rather_than_ticking_anything()
    {
        // Two kinds of row, one key, and which one it is decides. A row that was both would be a keystroke
        // whose meaning depended on the order two branches were written in.
        await using var seated = await Seated.OpenAsync(TestContext.Current.CancellationToken);
        var shell = seated.Shell;

        seated.Lay();
        seated.Focus<RoomRegion>();
        seated.Row(shell.Room.List, "answer or ask back");

        Assert.Null(shell.Room.SelectedPeer);
        Assert.NotNull(shell.Room.Selected);

        shell.Room.List.NewKeyDownEvent(Key.Enter);

        await shell.Settled;

        Assert.Contains(Seated.Orders, shell.Drafting!.Recipients);
        Assert.IsType<TranscriptRegion>(shell.Panes[shell.FocusedRegion]);
    }
}
