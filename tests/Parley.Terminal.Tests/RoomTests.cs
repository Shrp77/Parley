using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// The narrow column of the room: who is here, and what is owed.
/// </summary>
/// <remarks>
/// <para>
/// The owner asked for this after 0.29.2 put a count of waiting mail on the status line. Knowing that something
/// is waiting is not knowing what, and the answer to "what" was a key pressed inside a transcript they had no
/// reason to open.
/// </para>
/// <para>
/// <b>Built around the transcript rather than in place of it</b>, which is why the suite that drives this view
/// lost almost nothing: the column-zero rule, the escaping, the wrapping and the refill race are all properties
/// of a region this change did not touch.
/// </para>
/// </remarks>
[Integration]
public sealed class RoomTests
{
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Orders = AgentName.Create("orders@test");

    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(Walking.Alpha, string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

    private static RoomRegion Filled(bool seated = true, params PairState[] pairs)
    {
        var room = new RoomRegion();

        room.Show(
            pairs,
            [.. pairs.Where(p => p.Turn == Turn.Mine && p.OpenExchange is not null)
                .Select(p => new OutstandingItem(p.Peer, [new WaitingQuestion(p.OpenExchange!.Value, DateTimeOffset.UnixEpoch, null, "a question")]))],
            seated);

        return room;
    }

    [Fact]
    public async Task The_conversation_view_is_three_panes_with_the_room_beside_the_transcript()
    {
        using var driving = Driving.Open(width: 120, height: 40);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation, "pricing");

        Assert.Equal(3, shell.Panes.Count);
        Assert.IsType<RoomRegion>(shell.Panes[0]);
        Assert.IsType<TranscriptRegion>(shell.Panes[1]);
        Assert.IsType<LineRegion>(shell.Panes[2]);
    }

    [Fact]
    public async Task And_it_opens_focused_on_the_transcript_rather_than_on_the_furniture()
    {
        // The room is context; the transcript is what somebody opened a conversation to read, and it is where
        // every action on this view already lived.
        using var driving = Driving.Open(width: 120, height: 40);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation, "pricing");

        Assert.IsType<TranscriptRegion>(shell.Panes[shell.FocusedRegion]);
    }

    [Fact]
    public async Task The_room_takes_a_fixed_narrow_column_rather_than_a_share_of_the_width()
    {
        // It holds names and short clauses whatever the terminal is; everything it does not take belongs to the
        // transcript, which holds whatever a peer wrote.
        using var driving = Driving.Open(width: 200, height: 40);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation, "pricing");
        driving.App.LayoutAndDraw(true);

        Assert.Equal(Shell.Narrow, shell.Panes[0].Frame.Width);
        Assert.True(
            shell.Panes[1].Frame.Width > shell.Panes[0].Frame.Width * 3,
            $"the transcript got {shell.Panes[1].Frame.Width} of 200 columns");
    }

    [Fact]
    public void Participants_are_listed_under_a_heading_that_says_what_they_are()
    {
        var room = Filled(true, new PairState(Billing, null, Turn.Nobody), new PairState(Orders, null, Turn.Nobody));

        Assert.Contains(RoomRegion.Participants, room.Written);
        Assert.Contains(room.Written, row => row.Contains(Billing.Role, StringComparison.Ordinal));
        Assert.Contains(room.Written, row => row.Contains(Orders.Role, StringComparison.Ordinal));
    }

    [Fact]
    public void What_stands_between_them_is_written_out_and_not_left_to_a_colour()
    {
        // `ColourTests` reads this screen with every attribute stripped and asserts nothing is lost. A row
        // that said "waiting" only by being a different hue would be the archived-row fault again.
        var room = Filled(
            true,
            new PairState(Billing, 3, Turn.Mine),
            new PairState(Orders, 7, Turn.Theirs));

        Assert.Contains(room.Written, row => row.Contains("waiting on you", StringComparison.Ordinal));
        Assert.Contains(room.Written, row => row.Contains("owes you", StringComparison.Ordinal));
    }

    [Fact]
    public void What_is_owed_is_listed_under_its_own_heading()
    {
        var room = Filled(true, new PairState(Billing, 3, Turn.Mine));

        Assert.Contains(RoomRegion.ToAddress, room.Written);
        // One row for the agent with how many questions are waiting, not the heading said again.
        Assert.Contains("  billing (1)", room.Written);
        Assert.DoesNotContain(room.Written, row => row.Contains("waiting for your reply", StringComparison.Ordinal));
    }

    [Fact]
    public void Nothing_owed_says_so_rather_than_standing_empty()
    {
        // An empty list and a list that could not be built look identical, and only one of them means stop
        // looking. The same rule the console's waiting count is held to.
        var room = Filled(true, new PairState(Orders, null, Turn.Nobody));

        Assert.Contains("  " + RoomRegion.NothingOwed, room.Written);
    }

    [Fact]
    public void With_no_seat_the_participants_are_still_shown_and_a_seat_is_asked_for()
    {
        // Who is in a conversation is known from the export alone. Losing that as well would report less than
        // was actually read, and 0.28.2 records what hiding things from a seatless person costs.
        var room = Filled(false, new PairState(Billing, null, Turn.Nobody));

        Assert.Contains(room.Written, row => row.Contains(Billing.Role, StringComparison.Ordinal));
        Assert.Contains("  " + RoomRegion.NoSeat, room.Written);
        Assert.DoesNotContain("  " + RoomRegion.NothingOwed, room.Written);
    }

    [Fact]
    public void An_agent_name_reaches_the_column_as_data()
    {
        // A name is somebody else's text: the registry holds whatever an adapter said about itself. Every other
        // list on this surface makes control characters visible for the same reason.
        var room = Filled(true, new PairState(AgentName.Create("orders@test"), null, Turn.Nobody));

        Assert.All(room.Written, row => Assert.DoesNotContain('', row));
    }
}
