using Grpc.Core;
using Parley.Core;
using Parley.Client.Seating;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Client.Tests;

/// <summary>
/// The guards around the person's seat: whose it is, where the name comes from, and how long it is held.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rewritten for specs/023, and T024's tests are these.</b> Until then this file probed a person typing a name: an
/// agent's name refused, capitals folded, a malformed name refused with the rule, a person leaving every project at
/// once. There is no typed name now. The broker provides <c>human-user@&lt;project&gt;</c> and the book attaches as the
/// row its registry names — so the attack those tests were written against, a person speaking as <c>orders</c>, is
/// refused in storage instead, where the seat's adapter cannot sit under any other role.
/// </para>
/// <para>
/// What is probed here is what a surface still decides: <b>that the name comes from the registry and nothing the
/// surface knows about itself</b> (FR-001a, FR-017 — the evidence that a surface on a laptop speaks as the project's
/// person); that an unreadable registry, and one with no person row, refuse rather than guess; that one project's seat
/// says nothing about another's; and that the idle window drops the stream while keeping the seat known.
/// </para>
/// </remarks>
public sealed class SeatTests
{
    private static readonly ProjectSettings Alpha =
        new(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:5301", null);

    private static readonly ProjectSettings Beta =
        new(ProjectName.Create("beta"), string.Empty, "http://127.0.0.1:5302", null);

    [Fact]
    public async Task The_seat_is_the_registrys_person_row_and_not_this_machine()
    {
        // The defect 023 exists for, at the layer that had it: `Seat.Name` added `MachineName.Current` to whatever was
        // typed, so a surface on a laptop reaching a broker through a tunnel became a second person in the project. The
        // row here names a project, `alpha`, that is not this machine's name, and the attach must say exactly that.
        var agents = new StubAgents();
        using var book = new SeatBook(_ => agents);

        var outcome = await book.TakeAsync(Alpha, [Person(Alpha)], TestContext.Current.CancellationToken);

        Assert.True(outcome.Seated, outcome.Problem);
        Assert.Equal("human-user@alpha", outcome.Hold!.Seat.Name.Value);
        Assert.Equal("human-user@alpha", agents.LastAttach);
        Assert.NotEqual(MachineName.Current, outcome.Hold.Seat.Name.Machine);
    }

    [Fact]
    public async Task A_registry_with_no_person_row_refuses_and_says_why()
    {
        // A broker older than 0.36.0. Inventing a name here would be the failure 023 ends, so it refuses — with nothing
        // on the wire, since an attach that is undone is still a registration written.
        var agents = new StubAgents();
        using var book = new SeatBook(_ => agents);

        var outcome = await book.TakeAsync(
            Alpha, [Registered("orders", "claude-code")], TestContext.Current.CancellationToken);

        Assert.False(outcome.Seated);
        Assert.Equal(SeatBook.NoPersonSeat, outcome.Problem);
        Assert.Equal(0, agents.Attaches);
    }

    [Fact]
    public async Task A_typed_seat_left_from_before_is_not_taken_for_the_person()
    {
        // A store made before 023 can still hold `arvind@…` under the seat's adapter. The person is found by role; a row
        // that merely shares the adapter is somebody's old typed seat, and adopting it would speak as the wrong name.
        var agents = new StubAgents();
        using var book = new SeatBook(_ => agents);

        var outcome = await book.TakeAsync(
            Alpha, [Registered("arvind", Seat.Adapter)], TestContext.Current.CancellationToken);

        Assert.False(outcome.Seated);
        Assert.Equal(0, agents.Attaches);
    }

    [Fact]
    public async Task A_registry_that_could_not_be_read_refuses_the_seat()
    {
        // The vacuous case, which is the one that matters: an unreachable broker and a registry with nobody in it must
        // not be the same value, or a guard passes hardest exactly when the deployment is least well known.
        var agents = new StubAgents();
        using var book = new SeatBook(_ => agents);

        var outcome = await book.TakeAsync(Alpha, registry: null, TestContext.Current.CancellationToken);

        Assert.False(outcome.Seated);
        Assert.Equal(0, agents.Attaches);
    }

    [Fact]
    public async Task One_seat_per_project_and_one_says_nothing_of_another()
    {
        var agents = new StubAgents();
        using var book = new SeatBook(_ => agents);
        var token = TestContext.Current.CancellationToken;

        await book.TakeAsync(Alpha, [Person(Alpha)], token);

        Assert.NotNull(book.Held("alpha"));
        Assert.Null(book.Held("beta"));

        await book.TakeAsync(Beta, [Person(Beta)], token);

        Assert.Equal(["alpha", "beta"], book.Seats().Select(h => h.Project));
    }

    [Fact]
    public async Task Taking_it_twice_holds_one_seat_and_not_two()
    {
        // Two attachments for one name is two arrivals in the registry and two departures on the way out.
        var agents = new StubAgents();
        using var book = new SeatBook(_ => agents);
        var token = TestContext.Current.CancellationToken;

        await book.TakeAsync(Alpha, [Person(Alpha)], token);
        await book.TakeAsync(Alpha, [Person(Alpha)], token);

        Assert.Single(book.Seats());
        Assert.Equal(1, agents.Attaches);
    }

    [Fact]
    public async Task Ensuring_a_held_seat_asks_nothing_of_the_registry()
    {
        // What every page and every key calls. The ordinary case — somebody is here — must cost no broker call.
        var agents = new StubAgents();
        using var book = new SeatBook(_ => agents);
        var token = TestContext.Current.CancellationToken;
        var asked = 0;

        await book.TakeAsync(Alpha, [Person(Alpha)], token);

        var outcome = await book.EnsureAsync(
            Alpha,
            _ =>
            {
                asked++;

                return Task.FromResult<IReadOnlyList<AgentRecord>?>([Person(Alpha)]);
            },
            token);

        Assert.True(outcome.Seated);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task Nobody_at_the_surface_drops_the_stream_and_the_seat_stays_known()
    {
        // SPEC.md §4.0: an open connection is the evidence of presence, and a tab left open is not somebody at a desk.
        // So the stream goes after the idle window and the broker reads the person as away — but the seat is the
        // broker's and is remembered here, so the count of what is waiting still works.
        var agents = new StubAgents();
        using var book = new SeatBook(_ => agents);

        var taken = await book.TakeAsync(Alpha, [Person(Alpha)], TestContext.Current.CancellationToken);

        Assert.Equal(0, book.Sweep(DateTimeOffset.UtcNow));
        Assert.Equal(1, book.Sweep(DateTimeOffset.UtcNow + SeatBook.IdleAfter + TimeSpan.FromMinutes(1)));
        Assert.Empty(book.Seats());
        Assert.Equal(taken.Hold!.Seat, book.Known("alpha"));
    }

    [Fact]
    public async Task Using_the_surface_keeps_the_person_here()
    {
        // The other half, and the one that makes the sweep safe to have.
        var agents = new StubAgents();
        using var book = new SeatBook(_ => agents);

        await book.TakeAsync(Alpha, [Person(Alpha)], TestContext.Current.CancellationToken);

        var later = DateTimeOffset.UtcNow + SeatBook.IdleAfter - TimeSpan.FromMinutes(1);

        book.Touch(later);

        Assert.Equal(0, book.Sweep(later + TimeSpan.FromMinutes(2)));
        Assert.Single(book.Seats());
    }

    [Fact]
    public void A_seat_reports_a_human_responder()
    {
        // SPEC.md §11.3: identity uses a scheme rather than a model name, and `human:<name>` is the one for a person.
        var seat = new Seat(PersonSeat.On(ProjectName.Create("alpha")));

        Assert.Equal("human:human-user", seat.Responder);
        Assert.Equal(PersonSeat.Role, seat.Person);
        Assert.DoesNotContain("parley:", seat.Responder, StringComparison.Ordinal);
    }

    /// <summary>The person seat a broker writes for a project (specs/023).</summary>
    private static AgentRecord Person(ProjectSettings project) =>
        new(PersonSeat.On(project.Name), PersonSeat.Adapter, AgentTier.Pull, null, null, null, Connected: false,
            AgentStatus.Active);

    /// <summary>A registry row for a role on this machine.</summary>
    private static AgentRecord Registered(string role, string adapter) =>
        new(AgentName.Create($"{role}@{MachineName.Current}"), adapter, AgentTier.Pull, null, null, null,
            Connected: true, AgentStatus.Active);

    /// <summary>An agent client that acknowledges an attach and holds a stream that never ends.</summary>
    private sealed class StubAgents : Wire.Agents.AgentsClient
    {
        private int _attaches;

        /// <summary>How many times an attach was actually put on the wire.</summary>
        public int Attaches => Volatile.Read(ref _attaches);

        /// <summary>The name the last attach was for.</summary>
        public string? LastAttach { get; private set; }

        /// <inheritdoc/>
        public override AsyncServerStreamingCall<Wire.Event> Attach(
            Wire.AttachRequest request, CallOptions options)
        {
            Interlocked.Increment(ref _attaches);
            LastAttach = request.Agent;

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
