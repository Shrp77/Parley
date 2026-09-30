using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That a terminal nobody is at stops marking the person here, keeps their seat, and takes it back on the next key.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rewritten for specs/023, and what changed is the point.</b> This file asserted that an idle terminal
/// <i>released the seat and said so</i> — "your seat was released" — and forgot who the person was. That ran two things
/// together: having a seat and being in it. The broker now provides the person seat and never releases it, so an idle
/// terminal only drops its stream: the broker reads the person as away, an agent that asks meanwhile is told they will
/// answer, and the next key takes the stream back without a word, because nothing was taken from them.
/// </para>
/// <para>
/// <b>What survived unchanged is touch-then-sweep.</b> Swept first, somebody who spent longer than the idle window
/// thinking about a question on one screen would be marked away while reading it.
/// </para>
/// <para>
/// <c>ViewLifetimeTests.Moving_between_views_counts_as_being_here</c> is not carried here. It built a shell with no
/// seat book, so the sentence it asserted against could never have been said, and it could not fail.
/// </para>
/// </remarks>
public sealed class IdleSeatTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public IdleSeatTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _broker = LoopbackOperator.Start(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();

        _connection.Dispose();
    }

    [Fact]
    public async Task Nobody_at_the_terminal_drops_the_stream_says_nothing_and_keeps_the_seat()
    {
        var ct = TestContext.Current.CancellationToken;

        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = TerminalCommand.Build(directory, seats);

        driving.Show(shell);
        await shell.RefreshAsync(ct);
        await shell.Settled;

        var seat = Assert.IsType<SeatHold>(shell.Held).Seat;
        var said = shell.Said;

        // The terminal goes on drawing, because terminals do. Nobody presses anything.
        shell.Tick(DateTimeOffset.UtcNow + SeatBook.IdleAfter + TimeSpan.FromMinutes(5));

        Assert.Null(shell.Held);
        Assert.Equal(said, shell.Said);
        Assert.Equal(seat, seats.Known(Walking.Alpha.Value));

        // The half that is not the book's: the stream ended, so the broker reads the person as not at a screen. The row
        // is still there — the seat is the broker's and was never released.
        var client = new BrokerClient(_broker.Operators);

        await Eventually.UntilAsync(
            () => client.RegistryAsync(ct).GetAwaiter().GetResult()
                .Any(a => a.Name == seat.Name && !a.Connected),
            "the person was still connected in the registry, or their row had gone",
            ct);
    }

    [Fact]
    public async Task The_next_key_takes_the_stream_back_without_a_word()
    {
        var ct = TestContext.Current.CancellationToken;

        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = TerminalCommand.Build(directory, seats);

        driving.Show(shell);
        await shell.RefreshAsync(ct);
        await shell.Settled;

        var later = DateTimeOffset.UtcNow + SeatBook.IdleAfter + TimeSpan.FromMinutes(5);

        shell.Tick(later);

        Assert.Null(shell.Held);

        var said = shell.Said;

        shell.Touched(later);
        await shell.Settled;

        Assert.NotNull(shell.Held);
        Assert.True(PersonSeat.Is(shell.Held!.Seat.Name));
        Assert.Equal(said, shell.Said);
    }

    [Fact]
    public async Task Somebody_thinking_for_longer_than_the_window_stays_here()
    {
        // The half a sweep-first implementation gets wrong. Being drawn is not being here, but a key in each window is.
        var ct = TestContext.Current.CancellationToken;

        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = TerminalCommand.Build(directory, seats);

        driving.Show(shell);
        await shell.RefreshAsync(ct);
        await shell.Settled;

        var held = shell.Held;
        var now = DateTimeOffset.UtcNow;

        // Three windows of thinking, with something pressed in each: the same hold at the end.
        for (var window = 1; window <= 3; window++)
        {
            now += SeatBook.IdleAfter - TimeSpan.FromMinutes(1);

            shell.Touched(now);
            shell.Tick(now);
        }

        Assert.Same(held, shell.Held);
    }

    private ProjectSettings Project() =>
        new(Walking.Alpha, string.Empty, _broker.Address, null);

    private BrokerDirectory Directory() =>
        new([Project()], _ => GrpcChannel.ForAddress(_broker.Address));
}
