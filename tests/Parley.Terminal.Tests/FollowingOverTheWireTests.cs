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
/// That an open transcript reads its conversation again on its own, against a real broker.
/// </summary>
/// <remarks>
/// <para>
/// <c>FollowingTests</c> checks what a redraw does to the screen. This checks that one happens: that
/// the shell, left alone on a transcript, goes back to the broker and shows what has arrived — which is
/// the whole of what the owner reported missing, when an answer that came back in forty-seven seconds
/// was not on screen until they closed the transcript and opened it again.
/// </para>
/// <para>
/// Messages are written through the wire by a seated person rather than into the store, because the
/// broker is serving that store on another thread and one connection is not two writers.
/// </para>
/// </remarks>
public sealed class FollowingOverTheWireTests : IDisposable
{
    private const string Conversation = "release-notes";

    private static readonly AgentName Orders = AgentName.Create("orders@test");

    private static readonly TimeSpan Quickly = TimeSpan.FromMilliseconds(200);

    private readonly SqliteConnection _connection;

    public FollowingOverTheWireTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        new AgentRepository(_connection).UpsertAsync(
            new AgentRecord(Orders, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
            TestContext.Current.CancellationToken).GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task A_message_that_arrives_appears_in_the_open_transcript()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var broker = LoopbackOperator.Start(_connection);
        using var driving = Driving.Open();
        using var directory = Directory(broker);
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        var voice = Voice(directory, shell);

        await voice.RequestAsync(Orders, "the first thing", Conversation, ct);

        await ReadingAsync(shell, directory, ct);

        Assert.Contains("the first thing", shell.Transcript.Written, StringComparison.Ordinal);

        await voice.RequestAsync(Orders, "the second thing", Conversation, ct);

        await Eventually.UntilAsync(
            () => shell.Transcript.Written.Contains("the second thing", StringComparison.Ordinal),
            "a message sent while the transcript was open never appeared in it",
            ct);
    }

    [Fact]
    public async Task A_conversation_the_reader_has_left_is_not_read_again()
    {
        // Following belongs to the transcript on screen. Reading one nobody is looking at would be a
        // broker call every few seconds for nothing, for as long as the terminal stayed open.
        var ct = TestContext.Current.CancellationToken;

        await using var broker = LoopbackOperator.Start(_connection);
        using var driving = Driving.Open();
        using var directory = Directory(broker);
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        var voice = Voice(directory, shell);

        await voice.RequestAsync(Orders, "the first thing", Conversation, ct);

        await ReadingAsync(shell, directory, ct);

        await Walking.GoAsync(shell, Viewing.Project);

        var written = shell.Transcript.Written;

        await voice.RequestAsync(Orders, "sent after leaving", Conversation, ct);

        await Task.Delay(Quickly * 6, ct);
        await shell.Settled;

        Assert.Equal(written, shell.Transcript.Written);
    }

    [Fact]
    public async Task A_broker_that_stops_answering_is_said_and_the_transcript_is_kept()
    {
        // Wiping the transcript for a moment's trouble reaching the broker takes the conversation off the
        // screen; saying nothing leaves it claiming to be current. Neither.
        var ct = TestContext.Current.CancellationToken;

        var broker = LoopbackOperator.Start(_connection);
        using var driving = Driving.Open();
        using var directory = Directory(broker);
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        await Voice(directory, shell).RequestAsync(Orders, "the first thing", Conversation, ct);

        await ReadingAsync(shell, directory, ct);

        await broker.DisposeAsync();

        await Eventually.UntilAsync(
            () => shell.Said.Contains("may be out of date", StringComparison.Ordinal),
            "the transcript went on looking current after its broker stopped answering",
            ct);

        Assert.Contains("the first thing", shell.Transcript.Written, StringComparison.Ordinal);
    }

    private static BrokerDirectory Directory(LoopbackOperator broker) =>
        new(
            [new ProjectSettings(ProjectName.Create("acme"), "unused.db", broker.Address, null)],
            _ => GrpcChannel.ForAddress(broker.Address));

    private static SeatVoice Voice(BrokerDirectory directory, Shell shell) =>
        new(directory.AgentsFor(directory.Projects[0]), shell.Held!.Seat);

    /// <summary>Opens the conversation the way the conversations list does, with following on.</summary>
    private static async Task ReadingAsync(Shell shell, BrokerDirectory directory, CancellationToken ct)
    {
        // After Driving.Show, which turns following off for every other test in the suite.
        shell.FollowEvery = Quickly;

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.OpenAsync(directory.Projects[0], Conversation, ct);
    }

    private static async Task<Shell> SeatedAsync(
        Driving.Session driving, BrokerDirectory directory, SeatBook seats)
    {
        var shell = new Shell(directory) { Seats = seats };

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        await Walking.GoAsync(shell, Viewing.Project);

        // Seated by opening the project (specs/023): the broker provides the person seat and the shell takes it, so
        // there is no Join dialog to drive.
        await shell.Settled;

        Assert.NotNull(shell.Held);

        return shell;
    }
}
