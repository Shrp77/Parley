using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Core;
using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// The seat guard, exercised through the screen that takes a seat rather than through the book.
/// </summary>
/// <remarks>
/// <para>
/// <c>SeatTests</c> proves <see cref="SeatBook.TakeAsync"/> refuses when it is handed a null
/// registry. That is half the property. The other half is whether an unreachable broker actually
/// <i>arrives</i> as null — the surface reads the registry itself and hands it in, so a read that
/// contained a failure into an empty list would satisfy every existing test while turning the
/// guard off exactly when the deployment is least well known.
/// </para>
/// <para>
/// <b>An empty registry and an unreadable one are the same value in a weaker type</b>, and the
/// distance between "the broker says nobody is registered" and "nobody could ask the broker" is
/// the whole of this guard. Probed by pointing at a port with nothing behind it.
/// </para>
/// </remarks>
public sealed class JoinGuardTests
{
    /// <summary>A loopback port with nothing listening, so the dial is refused rather than slow.</summary>
    private const string Nowhere = "http://127.0.0.1:5799";

    private static readonly ProjectSettings Unreachable =
        new(ProjectName.Create("alpha"), string.Empty, Nowhere, null, null);

    [Fact]
    public async Task A_broker_that_cannot_be_reached_seats_nobody()
    {
        var token = TestContext.Current.CancellationToken;

        using var directory = new BrokerDirectory(
            [Unreachable], _ => GrpcChannel.ForAddress(Nowhere));

        using var seats = new SeatBook(directory.AgentsFor);

        // Null rather than empty, which is what the surface hands in when it could not read the
        // registry. This is the value under test: a read contained into an empty list would look
        // identical here and would turn the guard off.
        var (hold, said) = await AgentsRegion.JoinAsync(
            seats, Unreachable, null, "arvind", token);

        Assert.Null(hold);

        Assert.True(Seat.TryRead("arvind", out var me, out _));
        Assert.Empty(seats.Seats(me));

        // And it says which of the two things went wrong. "Nobody was joined" without a reason
        // reads as a refusal of the name, which would send somebody off to pick another one.
        Assert.Contains("registry", said, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_name_refused_by_the_grammar_never_reaches_the_broker()
    {
        // The field checks as it is typed, so a name that cannot be an agent is turned back at the
        // screen. Worth pinning: a check that let one through would put the grammar's job onto the
        // broker, which answers a different question and would register it.
        var token = TestContext.Current.CancellationToken;

        using var directory = new BrokerDirectory(
            [Unreachable], _ => GrpcChannel.ForAddress(Nowhere));

        using var seats = new SeatBook(directory.AgentsFor);

        Assert.NotNull(AgentsRegion.NameProblem("Orders With Spaces"));

        var (hold, said) = await AgentsRegion.JoinAsync(
            seats, Unreachable, null, "Orders With Spaces", token);

        // Refused by the grammar, and the reason says so rather than blaming the broker -- which
        // is the point: the broker was never asked.
        Assert.Null(hold);
        Assert.DoesNotContain("registry", said, StringComparison.OrdinalIgnoreCase);
    }
}
