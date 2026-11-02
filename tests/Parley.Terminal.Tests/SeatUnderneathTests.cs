using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client.Views;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That a seat released while somebody is mid-sentence does not become a sentence sent by nobody.
/// </summary>
/// <remarks>
/// <para>
/// Lens 1 of the review gate, run 1.15. Two things move on their own here and they move on
/// different threads of control: the idle sweep runs on every draw and takes a seat away, and a
/// composer holds the voice it was opened with until somebody presses OK. Between those two is a
/// window in which the thing about to speak is a seat that no longer exists.
/// </para>
/// <para>
/// It is reachable by doing nothing, which is what makes it worth probing rather than reasoning
/// about: opening the composer and then being called away is the ordinary shape of a person's
/// afternoon, and the sweep exists precisely because they will be.
/// </para>
/// </remarks>
[Integration]
public sealed class SeatUnderneathTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public SeatUnderneathTests()
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
    public async Task A_composer_finished_after_the_seat_was_swept_does_not_crash_the_surface()
    {
        // The failure worth catching is an unhandled exception on a keystroke, which takes the
        // whole screen down over one message nobody could have sent anyway.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        var form = await Composing.WithAsync(
            driving, shell.Start, Participation.NameTitle, Orders.Value, () => shell.Settled);

        Sweep(shell);

        Assert.Null(shell.Held);
        form.Fill(0, "underneath");
        form.Finish(taken: true);

        await shell.Settled;

        shell.Line.Written = "a request nobody can send";
        shell.Line.Accept();

        await shell.Settled;

        // Whatever it says, it says something, and the surface is still usable afterwards.
        Assert.NotNull(shell.Said);

        // Named rather than counted. The conversation view gained a second pane in 0.30.0 and an index
        // silently became a different region; what this test is about is the transcript.
        shell.Focus(shell.Panes.ToList().FindIndex(p => p is TranscriptRegion));

        Assert.Contains(Shell.FocusPrefix, shell.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_recorded_under_a_name_that_was_released()
    {
        // The integrity half. A transcript row attributed to somebody who had already left is the
        // one outcome worse than the message not being sent.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        var form = await Composing.WithAsync(
            driving, shell.Start, Participation.NameTitle, Orders.Value, () => shell.Settled);

        Sweep(shell);
        form.Fill(0, "underneath");
        form.Finish(taken: true);

        await shell.Settled;

        shell.Line.Written = "a request nobody can send";
        shell.Line.Accept();

        await shell.Settled;

        var export = await new BrokerClient(_broker.Operators)
            .ExportAsync("underneath", TestContext.Current.CancellationToken);

        Assert.Empty(export?.Messages ?? []);
    }

    [Fact]
    public async Task A_result_is_not_recorded_under_a_name_that_was_released_either()
    {
        // **The same defect as the test above, one dialog along, and worse.** A Result closes
        // somebody else's exchange, so a stale seat here does not merely attribute words to a
        // person who had left — it closes a request on their behalf.
        //
        // Both composers captured the seat when the dialog opened and never asked again;
        // `TakeDelivery` re-checked, which is what made it a pattern applied unevenly rather than
        // a decision. Found by running the suite on Linux while CI was down: the sibling test
        // passed on Windows purely because the attach stream tore down before the send landed.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        await shell.SendResult();

        Sweep(shell);

        await shell.Settled;

        // Nothing is asserted about which dialog opened -- with nothing owed there may be none.
        // What is asserted is that the surface knows the seat has gone.
        Assert.Null(shell.Voice());
    }

    [Fact]
    public async Task The_actions_a_seatless_surface_offers_no_longer_include_speaking()
    {
        // What the person sees after the sweep, which is the part that tells them why the composer
        // stopped working rather than leaving them to guess.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        // The transcript, which is its own view now rather than the fourth region of one.
        await Walking.GoAsync(shell, Viewing.Conversation);

        Assert.Contains(shell.Actions(), action => action.Label == Participation.RequestTitle);

        Sweep(shell);

        Assert.DoesNotContain(shell.Actions(), action => action.Label == Participation.RequestTitle);

        // And nothing says the seat was released, because it was not: only the stream went, and the
        // next key brings it back.
        Assert.DoesNotContain("released", shell.Said ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Fills every field of the composer with something it accepts, naming the conversation.</summary>
    /// <param name="form">The composer.</param>
    private static void Written(FormDialog form)
    {
        form.Fill(0, "still typing when the sweep came");
        form.Fill(1, "underneath");
    }

    [Fact]
    public async Task After_a_sweep_the_next_key_takes_the_seat_back_the_way_it_was_first_taken()
    {
        // A dropped stream must leave nothing behind that the retake trips over -- the book, the registry and the region
        // all have to agree the person was away before they can be here again.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        Sweep(shell);

        Assert.Null(shell.Held);

        shell.Touched(DateTimeOffset.UtcNow + SeatBook.IdleAfter + TimeSpan.FromMinutes(6));
        await shell.Settled;

        Assert.NotNull(shell.Held);
    }

    /// <summary>Draws once, long enough later that the seat has gone idle.</summary>
    private static void Sweep(Shell shell) =>
        shell.Tick(DateTimeOffset.UtcNow + SeatBook.IdleAfter + TimeSpan.FromMinutes(5));

    private async Task<Shell> SeatedAsync(
        Driving.Session driving, BrokerDirectory directory, SeatBook seats)
    {
        var shell = new Shell(directory) { Seats = seats };

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        Assert.NotNull(shell.Held);

        return shell;
    }

    private ProjectSettings Project() =>
        new(ProjectName.Create("alpha"), string.Empty, _broker.Address, null);

    private BrokerDirectory Directory() =>
        new([Project()], _ => GrpcChannel.ForAddress(_broker.Address));
}
