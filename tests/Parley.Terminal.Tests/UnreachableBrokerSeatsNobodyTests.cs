using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That a broker nobody can reach seats nobody, through the screen as well as through the book.
/// </summary>
/// <remarks>
/// <para>
/// <b>Was <c>JoinGuardTests</c></b>, which drove the Join dialog. There is no dialog now — the terminal
/// takes the person seat its registry names — but the property it guarded is unchanged and matters more now, since
/// nothing typed stands between the screen and the attach: <b>an unreadable registry must arrive as null, not as an
/// empty list.</b> A read contained into an empty list would look like "this broker has no person seat" and, with a
/// looser book, like permission to guess one. Its second test, that a typed name refused by the grammar never reaches
/// the broker, went with the typed name.
/// </para>
/// <para>
/// Probed by pointing at a port with nothing behind it.
/// </para>
/// </remarks>
public sealed class UnreachableBrokerSeatsNobodyTests
{
    /// <summary>A loopback port with nothing listening, so the dial is refused rather than slow.</summary>
    private const string Nowhere = "http://127.0.0.1:5799";

    private static readonly ProjectSettings Unreachable = new(Walking.Alpha, string.Empty, Nowhere, null);

    [Fact]
    public async Task A_broker_that_cannot_be_reached_seats_nobody()
    {
        using var directory = new BrokerDirectory([Unreachable], _ => GrpcChannel.ForAddress(Nowhere));
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = new Shell(directory) { Seats = seats, MailEvery = null, FollowEvery = null };

        using var session = Driving.Open();

        session.Show(shell);

        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await Walking.GoAsync(shell, Viewing.Project);

        Assert.Null(shell.Held);
        Assert.Empty(seats.Seats());
        Assert.Null(seats.Known(Walking.Alpha.Value));
    }

    [Fact]
    public async Task The_book_says_which_of_the_two_things_went_wrong()
    {
        // "Nothing was taken" without a reason reads as a refusal of the person, which would send somebody looking for
        // a name to change when there is none to change.
        using var directory = new BrokerDirectory([Unreachable], _ => GrpcChannel.ForAddress(Nowhere));
        using var seats = new SeatBook(directory.AgentsFor);

        var outcome = await seats.EnsureAsync(
            Unreachable, _ => Task.FromResult<IReadOnlyList<AgentRecord>?>(null), TestContext.Current.CancellationToken);

        Assert.Null(outcome.Hold);
        Assert.Contains("did not answer", outcome.Problem, StringComparison.Ordinal);
    }
}
