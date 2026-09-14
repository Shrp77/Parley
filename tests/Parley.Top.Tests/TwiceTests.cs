using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// That doing the same thing twice is safe, because somebody will.
/// </summary>
/// <remarks>
/// <para>
/// Lens 1 of the review gate, run 1.16. Every probe so far drove each act once. A person at a
/// terminal presses a key again when nothing visibly happened, and the two acts that carry state on
/// this surface are exactly the two they would repeat: taking delivery, which marks messages
/// delivered and starts a working window, and answering, which closes an exchange.
/// </para>
/// <para>
/// The failure worth catching is not the second attempt failing — it is the second attempt
/// succeeding differently: a debt struck off twice, a delivery counted twice, or an exchange the
/// seat believes open after the broker has closed it. The seat's own idea of what is owed is a
/// local copy of a fact the broker holds, and a local copy is only worth what its worst update
/// path leaves it.
/// </para>
/// </remarks>
public sealed class TwiceTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public TwiceTests()
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
    public async Task Taking_delivery_twice_does_not_deliver_the_same_message_twice()
    {
        var ct = TestContext.Current.CancellationToken;
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);

        var (seated, voice) = await AskedAsync(directory, seats, ct);

        var (first, _) = await voice.TakeDeliveryAsync(seated, ct);
        var (second, _) = await voice.TakeDeliveryAsync(seated, ct);

        Assert.Single(first);
        Assert.Empty(second);

        // And the debt is still one debt, not two. A second drain that re-added it would show a
        // person two questions where a peer asked one.
        Assert.Single(seated.Delivered, one => one.Owes);
    }

    [Fact]
    public async Task Sending_the_same_result_twice_leaves_one_result_and_says_so()
    {
        var ct = TestContext.Current.CancellationToken;
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);

        var (seated, voice) = await AskedAsync(directory, seats, ct);

        var (taken, _) = await voice.TakeDeliveryAsync(seated, ct);
        var owed = taken.Single(one => one.Owes);
        var exchange = owed.Message.ExchangeId;

        var first = await voice.ResultAsync(
            seated, exchange, [.. owed.Items().Select(id => (id, "yes", "answered"))], ct);

        Assert.True(first.Closed, first.Says);

        var again = await voice.ResultAsync(
            seated, exchange, [.. owed.Items().Select(id => (id, "yes again", "answered"))], ct);

        // Whatever the broker's verdict is, it is reported rather than swallowed, and the record
        // holds one answer rather than two.
        Assert.NotNull(again.Says);
        Assert.NotEqual(string.Empty, again.Says);

        var export = await new BrokerClient(_broker.Operators).ExportAsync("chat", ct);

        Assert.Single(
            export.Messages,
            message => message.PayloadJson.Contains("yes", StringComparison.Ordinal)
                    && message.From == seated.Seat.Name);
    }

    [Fact]
    public async Task A_debt_settled_once_is_not_owed_again_after_a_second_drain()
    {
        // The two acts crossed. Answering settles the local copy; a drain afterwards must not
        // resurrect what the broker has already closed.
        var ct = TestContext.Current.CancellationToken;
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);

        var (seated, voice) = await AskedAsync(directory, seats, ct);

        var (taken, _) = await voice.TakeDeliveryAsync(seated, ct);
        var owed = taken.Single(one => one.Owes);

        await voice.ResultAsync(
            seated, owed.Message.ExchangeId, [.. owed.Items().Select(id => (id, "yes", "answered"))], ct);

        Assert.DoesNotContain(seated.Delivered, one => one.Owes);

        await voice.TakeDeliveryAsync(seated, ct);

        Assert.DoesNotContain(seated.Delivered, one => one.Owes);
    }

    [Fact]
    public async Task Leaving_a_seat_twice_is_not_an_error_and_leaves_it_gone()
    {
        var ct = TestContext.Current.CancellationToken;
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);

        var (seated, _) = await AskedAsync(directory, seats, ct);

        var left = seats.Leave(seated.Seat);
        var again = seats.Leave(seated.Seat);

        Assert.Equal(1, left);
        Assert.Equal(0, again);

        Assert.Empty(seats.Seats(seated.Seat));
    }

    /// <summary>A seat with one question waiting on it, and the voice to answer through.</summary>
    private async Task<(SeatHold Seated, SeatVoice Voice)> AskedAsync(
        BrokerDirectory directory, SeatBook seats, CancellationToken cancellationToken)
    {
        var project = Project();
        var registry = await new BrokerClient(_broker.Operators).RegistryAsync(cancellationToken);

        Assert.True(Seat.TryRead("arvind", out var seat, out _));

        var outcome = await seats.TakeAsync(project, seat, registry, cancellationToken);

        Assert.True(outcome.Seated, outcome.Problem);

        var seated = outcome.Hold!;
        var broker = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await broker.StartAsync(Orders, T0, cancellationToken);

        var sent = await broker.RequestAsync(
            Orders,
            seated.Seat.Name,
            "model-a",
            """{"intent":"deciding","items":[{"id":"q1","kind":"question","text":"is it safe"}],"done_when":"a release goes out or does not, with the reason"}""",
            T0.AddMinutes(1),
            "chat",
            cancellationToken);

        Assert.True(sent.Exchange is not null, $"the request was refused: {sent.Admission.Rejection}");

        return (seated, new SeatVoice(directory.AgentsFor(project), seated.Seat));
    }

    private ProjectSettings Project() =>
        new(ProjectName.Create("alpha"), string.Empty, _broker.Address, null, null);

    private BrokerDirectory Directory() =>
        new([Project()], _ => GrpcChannel.ForAddress(_broker.Address));
}
