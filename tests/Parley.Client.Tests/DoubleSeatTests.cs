using Grpc.Core;
using Parley.Client.Seating;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Client.Tests;

/// <summary>
/// One person, two surfaces, one name.
/// </summary>
/// <remarks>
/// <para>
/// Research R2 settled that both the console and the terminal present the same adapter, so a
/// person seated in a browser and in a terminal at once is two attachments under one name rather
/// than a collision. SPEC.md §14 makes that legitimate, and the alternative — giving the terminal
/// its own marker — would have invented a restriction the protocol does not have, by way of a
/// naming choice.
/// </para>
/// <para>
/// <b>Legitimate is not the same as free.</b> Two attachments for one agent is precisely the
/// state <c>connected = 1</c> read wrongly until it was fixed on 2026-09-02: a third window
/// closing marked the agent absent while the first two were still working, and the broker
/// released work that was in progress. That was rare when the only way to hold two sessions was
/// two editors. A terminal beside a browser makes it ordinary, which is why it is probed here
/// rather than left to the assembly that fixed it.
/// </para>
/// <para>
/// Written from the attack: seat the same person twice, close one, and check the other is still
/// sitting there.
/// </para>
/// </remarks>
public sealed class DoubleSeatTests
{
    private static readonly ProjectSettings Alpha =
        new(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:5401", null, null);

    [Fact]
    public async Task Two_surfaces_seat_one_person_as_two_attachments()
    {
        // The console and the terminal, as two books over one deployment. Each attaches, because
        // an attachment is what says somebody is present (§4.0) and there are two of them here.
        var console = new StubAgents();
        var terminal = new StubAgents();
        using var browser = new SeatBook(_ => console);
        using var screen = new SeatBook(_ => terminal);
        var seat = Seated("arvind");
        var token = TestContext.Current.CancellationToken;

        var first = await browser.TakeAsync(Alpha, seat, [], token);
        var second = await screen.TakeAsync(Alpha, seat, [], token);

        Assert.True(first.Seated);
        Assert.True(second.Seated);
        Assert.Equal(1, console.Attaches);
        Assert.Equal(1, terminal.Attaches);
    }

    [Fact]
    public async Task Closing_one_surface_leaves_the_other_seated()
    {
        // The regression this exists for. Leaving in one place must not unseat the person in the
        // other -- the shape of the bug that released a third window's in-progress work when the
        // count of attachments was read as a flag.
        var console = new StubAgents();
        var terminal = new StubAgents();
        using var browser = new SeatBook(_ => console);
        using var screen = new SeatBook(_ => terminal);
        var seat = Seated("arvind");
        var token = TestContext.Current.CancellationToken;

        await browser.TakeAsync(Alpha, seat, [], token);
        await screen.TakeAsync(Alpha, seat, [], token);

        Assert.Equal(1, browser.Leave(seat));

        Assert.Empty(browser.Seats(seat));
        Assert.Single(screen.Seats(seat));
        Assert.NotNull(screen.Held(Alpha.Name.Value, seat));
    }

    [Fact]
    public async Task A_person_already_seated_here_is_not_attached_a_second_time()
    {
        // The same surface, twice -- a page refreshed, or a screen redrawn. The seat already
        // exists, so nothing new goes on the wire: a second attach would be a second presence
        // announced for somebody who never left, and the count of them is what the broker reads.
        var agents = new StubAgents();
        using var book = new SeatBook(_ => agents);
        var seat = Seated("arvind");
        var token = TestContext.Current.CancellationToken;

        var first = await book.TakeAsync(Alpha, seat, [], token);
        var second = await book.TakeAsync(Alpha, seat, [], token);

        Assert.True(second.Seated);
        Assert.Same(first.Hold, second.Hold);
        Assert.Equal(1, agents.Attaches);
        Assert.Single(book.Seats(seat));
    }

    [Fact]
    public async Task A_seat_the_other_surface_holds_is_not_mistaken_for_an_agent()
    {
        // The guard and this case meeting. A person seated in the browser appears in the registry
        // under Seat.Adapter, and the terminal reads that registry when it seats them too. If the
        // check were "is this name present" rather than "is this name an agent", the second
        // surface would refuse the person their own name -- the restriction R2 declined to invent.
        var agents = new StubAgents();
        using var screen = new SeatBook(_ => agents);

        var outcome = await screen.TakeAsync(
            Alpha,
            Seated("arvind"),
            [Registered("arvind", adapter: Seat.Adapter)],
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Seated);
        Assert.Null(outcome.Problem);
    }

    /// <summary>A person's seat, by the name they typed.</summary>
    /// <param name="person">What they typed.</param>
    /// <returns>The seat.</returns>
    private static Seat Seated(string person)
    {
        Assert.True(Seat.TryRead(person, out var seat, out _));

        return seat;
    }

    /// <summary>A registry row for a name on this machine.</summary>
    /// <param name="role">The role half.</param>
    /// <param name="adapter">What the registry says is running under it.</param>
    /// <returns>The row.</returns>
    private static AgentRecord Registered(string role, string adapter) =>
        new(AgentName.Create($"{role}@{MachineName.Current}"),
            adapter,
            AgentTier.Pull,
            null,
            null,
            null,
            Connected: true,
            AgentStatus.Active);

    /// <summary>An Agents client that counts what was actually put on the wire.</summary>
    private sealed class StubAgents : Wire.Agents.AgentsClient
    {
        private int _attaches;

        /// <summary>How many times an attach was actually put on the wire.</summary>
        public int Attaches => Volatile.Read(ref _attaches);

        /// <inheritdoc/>
        public override AsyncServerStreamingCall<Wire.Event> Attach(
            Wire.AttachRequest request, CallOptions options)
        {
            Interlocked.Increment(ref _attaches);

            return new AsyncServerStreamingCall<Wire.Event>(
                new Acknowledged(),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });
        }
    }

    /// <summary>A stream that yields one attached event and then waits forever.</summary>
    private sealed class Acknowledged : IAsyncStreamReader<Wire.Event>
    {
        private bool _sent;

        /// <inheritdoc/>
        public Wire.Event Current { get; private set; } = new();

        /// <inheritdoc/>
        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (!_sent)
            {
                _sent = true;
                Current = new Wire.Event { Attached = new Wire.Attached() };

                return true;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

            return false;
        }
    }
}
