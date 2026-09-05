using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Top;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// That a terminal nobody is at loses its seat, and one somebody is thinking at does not.
/// </summary>
/// <remarks>
/// <para>
/// Lens 1 of the review gate, probing the one thing on this surface that depends on the clock. A
/// terminal left open overnight holding somebody present in the registry is the failure
/// <c>SeatBook.IdleAfter</c> exists to prevent, reached by a different kind of window than the
/// browser tab it was written for.
/// </para>
/// <para>
/// <b>Touch then sweep, and the order is the whole of it.</b> Swept first, a person who spent
/// longer than the idle window on one screen comes back to find their seat gone — and the screen
/// they were on is where they read a question and write an answer, which is the slowest and most
/// valuable thing this surface is for. Being unseated for thinking about the question is the exact
/// opposite of what an idle timeout is for.
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
    public async Task A_seat_nobody_is_at_is_released_and_the_surface_says_so()
    {
        // **The finding this file was written for.** 004 swept on every visit to its menu; the new
        // surface has no menu to visit, and nothing was calling the sweep at all -- so a terminal
        // left open overnight held somebody present in the registry until the process ended, which
        // is the exact failure IdleAfter exists to prevent. The shell now sweeps on every draw.
        var ct = TestContext.Current.CancellationToken;

        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = TopCommand.Build(directory, seats);

        driving.Show(shell);
        await shell.RefreshAsync(ct);
        await shell.Settled;

        driving.Tab();

        _ = shell.Join();

        var dialog = (TextDialog)driving.Find(AgentsRegion.JoinTitle);

        driving.Type("arvind");
        dialog.Finish(taken: true);

        await shell.Settled;

        Assert.NotNull(shell.Held);
        Assert.NotNull(shell.Me);

        // The terminal goes on drawing, because terminals do. Nobody presses anything.
        shell.Tick(DateTimeOffset.UtcNow + SeatBook.IdleAfter + TimeSpan.FromMinutes(5));

        Assert.Null(shell.Held);
        Assert.Null(shell.Me);
        Assert.Equal(Shell.Released, shell.Said);
    }

    [Fact]
    public async Task A_seat_somebody_is_at_survives_far_longer_than_the_window()
    {
        // The other half, and the one a sweep-first implementation gets wrong. Being drawn is being
        // here, so the seat of the person watching cannot be taken by the sweep they are causing.
        var ct = TestContext.Current.CancellationToken;

        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = TopCommand.Build(directory, seats);

        driving.Show(shell);
        await shell.RefreshAsync(ct);
        await shell.Settled;

        driving.Tab();

        _ = shell.Join();

        var dialog = (TextDialog)driving.Find(AgentsRegion.JoinTitle);

        driving.Type("arvind");
        dialog.Finish(taken: true);

        await shell.Settled;

        var now = DateTimeOffset.UtcNow;

        // Three windows of thinking, with something pressed in each: still seated at the end.
        for (var window = 1; window <= 3; window++)
        {
            now += SeatBook.IdleAfter - TimeSpan.FromMinutes(1);

            shell.Touched(now);
            shell.Tick(now);
        }

        Assert.NotNull(shell.Held);
        Assert.NotEqual(Shell.Released, shell.Said);
    }

    [Fact]
    public async Task Somebody_elses_idle_seat_is_swept_while_this_one_is_kept()
    {
        // What the sweep is actually for, and the shape 004's test used: the person at the surface
        // is touched, and every seat nobody is at goes.
        var ct = TestContext.Current.CancellationToken;

        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);

        Assert.True(Seat.TryRead("arvind", out var here, out _));
        Assert.True(Seat.TryRead("sam", out var gone, out _));

        Assert.True((await seats.TakeAsync(Project(), here, [], ct)).Seated);
        Assert.True((await seats.TakeAsync(Project(), gone, [], ct)).Seated);

        var later = DateTimeOffset.UtcNow + SeatBook.IdleAfter + TimeSpan.FromMinutes(5);

        Assert.NotNull(AgentsRegion.Refresh(seats, here, Project(), later));
        Assert.Empty(seats.Seats(gone));
    }

    [Fact]
    public void Nobody_seated_is_not_an_error()
    {
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);

        Assert.Null(AgentsRegion.Refresh(seats, null, Project(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_swept_seat_is_gone_from_the_registry_and_not_only_from_the_book()
    {
        // The half that is not the book's: the attach stream ends, so the broker learns somebody
        // left. A book that forgot the seat while the registry still showed them present would be
        // the disagreement between screen and deployment this whole surface is written against.
        var ct = TestContext.Current.CancellationToken;

        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);

        Assert.True(Seat.TryRead("arvind", out var me, out _));
        Assert.True((await seats.TakeAsync(Project(), me, [], ct)).Seated);

        var client = new BrokerClient(_broker.Operators);

        await Eventually.UntilAsync(
            () => client.RegistryAsync(ct).GetAwaiter().GetResult().Any(a => a.Name.Role == "arvind"),
            "the seat never reached the registry",
            ct);

        Assert.True(Seat.TryRead("sam", out var elsewhere, out _));

        AgentsRegion.Refresh(
            seats, elsewhere, Project(),
            DateTimeOffset.UtcNow + SeatBook.IdleAfter + TimeSpan.FromMinutes(5));

        await Eventually.UntilAsync(
            () => client.RegistryAsync(ct).GetAwaiter().GetResult()
                .All(a => a.Name.Role != "arvind" || !a.Connected),
            "the swept seat was still connected in the registry",
            ct);
    }

    private ProjectSettings Project() =>
        new(ProjectName.Create("alpha"), string.Empty, _broker.Address, null, null);

    private BrokerDirectory Directory() =>
        new([Project()], _ => GrpcChannel.ForAddress(_broker.Address));
}
