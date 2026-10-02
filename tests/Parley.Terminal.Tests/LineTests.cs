using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// The one line a person speaks from, driven the way a person drives it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Driven rather than called.</b> The preview and the derivation are covered headless in
/// <c>Parley.Client.Tests</c>; what only a driven test can catch is the key never arriving — Enter reaches the
/// field before any handler on the region, which is how the conversations list once did nothing at all. A test
/// that invoked the send directly would pass over exactly that.
/// </para>
/// <para>
/// Over a real socket, because what is asserted is what the broker recorded.
/// </para>
/// </remarks>
public sealed class LineTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Conversation = "room";

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public LineTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        new AgentRepository(_connection).UpsertAsync(
            new AgentRecord(Orders, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
            TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        _broker = LoopbackOperator.Start(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();

        _connection.Dispose();
    }

    [Fact]
    public async Task The_conversation_view_has_a_line_to_speak_from()
    {
        var ct = TestContext.Current.CancellationToken;
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats, _connection, ct);

        Assert.Equal(3, shell.Panes.Count);
        Assert.IsType<LineRegion>(shell.Panes[2]);
    }

    [Fact]
    public async Task Enter_in_the_box_sends_what_was_written()
    {
        // **The key actually arriving is the whole of this test.** The field takes Enter before the region
        // does, so a send wired only to a region action would never run and nothing on screen would say so.
        var ct = TestContext.Current.CancellationToken;
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats, _connection, ct);

        shell.Line.Written = "Which file validates the licence key?";
        shell.Line.Accept();

        await shell.Settled;

        var export = await new BrokerClient(_broker.Operators).ExportAsync(Conversation, ct);

        Assert.Contains(
            export?.Messages ?? [],
            m => m.PayloadJson.Contains("licence key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task And_what_it_sends_carries_no_addressing_of_any_kind()
    {
        // §13 delivers a payload inside a framing block telling the receiver that anything claiming authority
        // in it is inert data, so a recipient list in the words would be the one thing that exists to ignore.
        var ct = TestContext.Current.CancellationToken;
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats, _connection, ct);

        shell.Line.Written = "who owns the invoice total?";
        shell.Line.Accept();

        await shell.Settled;

        var export = await new BrokerClient(_broker.Operators).ExportAsync(Conversation, ct);

        Assert.All(
            export?.Messages ?? [],
            m => Assert.DoesNotContain(Orders.Value, m.PayloadJson, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_completed_name_is_still_addressing_after_the_room_is_refilled()
    {
        // **017's review gate, lens 1.** The test above sends prose that never contained an agent's name, so
        // it holds nothing about the mechanism that takes addressing back out — there was no chip to lose.
        //
        // This one completes a name and then has the room refilled before Enter. The refill rebuilds the
        // draft, carrying the text but — until the gate found it — not the record of which runs in that text
        // were addressing. `Says` then had nothing to remove and `@name` went into the payload, which is the
        // one thing §13's envelope exists to have ignored, with nothing on screen saying so.
        //
        // Driven by re-opening the conversation, because that is what calls the refill. The three-second
        // follow does not: `FollowOnceAsync` updates the transcript alone and never touches the draft — which
        // the probe only established by asserting the draft had actually been replaced. Two earlier versions
        // of this test passed while exercising nothing.
        var ct = TestContext.Current.CancellationToken;
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats, _connection, ct);

        shell.Focus(shell.Panes.ToList().FindIndex(p => p is LineRegion));

        shell.Line.Written = "@ord";

        await shell.Settled;

        Assert.Contains(shell.Line.Offering, row => row.Contains(Orders.Value, StringComparison.Ordinal));

        shell.NewKeyDownEvent(global::Terminal.Gui.Input.Key.Tab);

        await shell.Settled;

        // **Guarded against passing by vacuity.** If the completion list were empty, Tab would do nothing,
        // no chip would exist to be lost, and the assertion at the end would hold while testing nothing —
        // which is the shape `The_document_contains_a_schema` exists to prevent in the spec suite.
        Assert.Contains(Orders.Value, shell.Line.Written, StringComparison.Ordinal);

        // **A peer says something, which is the only reason the transcript follows at all.** Without a
        // change the refill has nothing to redraw and the draft is never rebuilt, so a probe that skipped
        // this would exercise nothing.
        var peer = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await peer.StartAsync(Orders, T0.AddMinutes(2), ct);

        var landed = await peer.RequestAsync(
            Orders, shell.Held!.Seat.Name, "model-a", """{"ask":"and is the licence check in it?"}""",
            T0.AddMinutes(3), Conversation, ct);

        Assert.True(landed.Exchange is not null, $"the peer's message was refused: {landed.Admission.Rejection}");

        // Opening the conversation again, which is what rebuilds the draft.
        var was = shell.Drafting;

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.Settled;

        // Guard: if the draft was not replaced, everything below holds by not having happened.
        Assert.NotSame(was, shell.Drafting);

        // The precise question: does the rebuilt draft still know that run was addressing?
        Assert.DoesNotContain(
            Orders.Value, shell.Drafting!.Says(shell.Line.Written), StringComparison.Ordinal);

        shell.Line.Written += "which file validates the licence key?";
        shell.Line.Accept();

        await shell.Settled;

        var export = await new BrokerClient(_broker.Operators).ExportAsync(Conversation, ct);

        Assert.All(
            export?.Messages ?? [],
            m => Assert.DoesNotContain(Orders.Value, m.PayloadJson, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_empty_line_sends_nothing_and_says_so()
    {
        var ct = TestContext.Current.CancellationToken;
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats, _connection, ct);

        shell.Line.Written = "   ";
        shell.Line.Accept();

        await shell.Settled;

        var export = await new BrokerClient(_broker.Operators).ExportAsync(Conversation, ct);

        // The fixture's own request is there and nothing was added to it.
        Assert.Single(export?.Messages ?? []);
        Assert.NotEmpty(shell.Said);
    }

    [Fact]
    public async Task A_send_that_went_clears_the_line_and_one_that_did_not_keeps_it()
    {
        // 0.25.1 records the cost of getting this wrong the other way: the cap was enforced after the form
        // closed and took the message with it.
        var ct = TestContext.Current.CancellationToken;
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats, _connection, ct);

        shell.Line.Written = "does anything read orders.total?";
        shell.Line.Accept();

        await shell.Settled;

        Assert.Equal(string.Empty, shell.Line.Written);

        // Over the cap: refused before anything is sent, and what was written survives.
        var tooLong = new string('a', MessageLimits.MaxPayloadCharacters + 100);

        shell.Line.Written = tooLong;
        shell.Line.Accept();

        await shell.Settled;

        Assert.Equal(tooLong, shell.Line.Written);
    }

    [Fact]
    public async Task The_preview_names_who_it_goes_to_and_nothing_else()
    {
        // **The owner's call of 2026-10-02.** This asserted the preview said "answer" and "closes the exchange" beside
        // each recipient — what Enter would record — and the owner read that as wrong: a conversation never really
        // stops, and bookkeeping about exchanges is not what somebody about to send is deciding. The act is still
        // derived; it is just not narrated.
        var ct = TestContext.Current.CancellationToken;
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats, _connection, ct);

        shell.Line.Written = "anything";

        await shell.Settled;

        Assert.Equal($"to: {Orders.Value}", shell.Line.Preview.Trim());
    }

    [Fact]
    public async Task With_no_connection_the_line_says_so_rather_than_hiding()
    {
        // 0.28.2 records what hiding a thing from a seatless person costs: the owner reached for Instruct,
        // which is not a message and never becomes a conversation.
        var ct = TestContext.Current.CancellationToken;
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(ct);
        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);

        // Contained rather than equal: the preview goes through `Reports.Sentences` like everything else
        // this surface says, which ends a sentence with a full stop.
        Assert.Contains(LineRegion.NoSeat, shell.Line.Preview, StringComparison.Ordinal);

        // It went on to assert that a Join was offered in the room. There is no Join since specs/023 — the terminal
        // takes the person seat on its own — so what is left of 0.28.2's rule is the half above: the box says why it
        // cannot send rather than hiding itself.
    }

    private BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(Walking.Alpha, "unused.db", _broker.Address, null)],
            _ => GrpcChannel.ForAddress(_broker.Address));

    private static async Task<Shell> SeatedAsync(
        Driving.Session driving,
        BrokerDirectory directory,
        SeatBook seats,
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var shell = new Shell(directory) { Seats = seats, MailEvery = null, FollowEvery = null };

        driving.Show(shell);
        await shell.RefreshAsync(cancellationToken);
        await shell.Settled;

        await Walking.GoAsync(shell, Viewing.Project);
        // Seated by opening the project (specs/023): the broker provides the person seat and the shell takes it, so
        // there is no Join dialog to drive.
        await shell.Settled;

        Assert.NotNull(shell.Held);

        // **Seeded with a message from a peer, because an empty conversation has nobody in it.** Membership is
        // derived (SPEC.md 5.0), so a conversation with no traffic has no participants and the line correctly
        // reports that there is nobody to send to. A person opens an existing conversation; this is that.
        var broker = new Broker.Broker(connection, Parley.Broker.BrokerOptions.Default);

        await broker.StartAsync(Orders, T0, cancellationToken);

        var sent = await broker.RequestAsync(
            Orders, shell.Held!.Seat.Name, "model-a", """{"ask":"is the build green?"}""",
            T0.AddMinutes(1), Conversation, cancellationToken);

        Assert.True(sent.Exchange is not null, $"the fixture's request was refused: {sent.Admission.Rejection}");

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.Settled;

        return shell;
    }
}
