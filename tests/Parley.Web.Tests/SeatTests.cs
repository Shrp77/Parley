using Grpc.Core;
using Parley.Core;
using Parley.Web.Seating;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// The guards around a person taking a seat.
/// </summary>
/// <remarks>
/// <para>
/// A seat is the one thing the console gained that an operator never had. §10.2 lets an operator
/// retire, purge and force-conclude any agent in the deployment, so a console has never been
/// short of power — what it could not do was <em>speak as</em> somebody, and a transcript
/// recording words an agent never said is a different kind of wrong from an operator visibly
/// instructing it.
/// </para>
/// <para>
/// These probe that rather than read it. Every one of them was written as the attack first: seat
/// somebody on a name an agent holds, seat them while the registry is unknown, leave one project
/// and check the others, and walk the clock past the idle window.
/// </para>
/// </remarks>
public sealed class SeatTests
{
    private static readonly ProjectSettings Alpha =
        new(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:5301", null, null);

    private static readonly ProjectSettings Beta =
        new(ProjectName.Create("beta"), string.Empty, "http://127.0.0.1:5302", null, null);

    [Fact]
    public async Task A_name_an_agent_already_holds_is_refused()
    {
        // The attack. `orders` is a perfectly ordinary thing for a person to type, and without
        // this the console attaches under that name -- the broker registers what is absent and
        // updates what is not -- and every message the person then sends is recorded as the
        // orders agent's own words.
        var agents = new StubAgents();
        using var book = new SeatBook(_ => agents);

        var outcome = await book.TakeAsync(
            Alpha,
            Seated("orders"),
            [Registered("orders", adapter: "claude-code")],
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Seated);
        Assert.Contains("already registered here as an agent", outcome.Problem!, StringComparison.Ordinal);

        // Refused before anything was attached, not after. An attach that is undone is still a
        // registration written and a presence announced.
        Assert.Equal(0, agents.Attaches);
    }

    [Fact]
    public async Task A_seat_this_console_already_holds_is_not_a_collision()
    {
        // The other direction, and the reason the check is on the adapter rather than on the
        // name being present at all. Somebody who joins, is swept for idleness and comes back
        // finds their own registry row waiting, and must not be locked out of their own name.
        var agents = new StubAgents();
        using var book = new SeatBook(_ => agents);

        var outcome = await book.TakeAsync(
            Alpha,
            Seated("arvind"),
            [Registered("arvind", adapter: Seat.Adapter)],
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Seated);
        Assert.Equal(1, agents.Attaches);
    }

    [Fact]
    public async Task A_registry_that_could_not_be_read_refuses_the_seat()
    {
        // The vacuous case, which is the one that matters. A guard that reads a registry and
        // then proceeds when the read failed is a guard that passes hardest exactly when the
        // deployment is least well known -- and it would pass silently, because an unreachable
        // broker and a registry with nobody in it are the same empty list.
        var agents = new StubAgents();
        using var book = new SeatBook(_ => agents);

        var outcome = await book.TakeAsync(
            Alpha, Seated("orders"), registry: null, TestContext.Current.CancellationToken);

        Assert.False(outcome.Seated);
        Assert.Equal(0, agents.Attaches);
    }

    [Fact]
    public async Task Leaving_releases_the_seat_in_every_project_at_once()
    {
        // A person leaves a console, not a page. Releasing only the project they happened to be
        // looking at would leave them present in the others, with no surface anywhere saying so
        // and nothing but the idle sweep to eventually notice.
        var agents = new StubAgents();
        using var book = new SeatBook(_ => agents);
        var seat = Seated("arvind");
        var token = TestContext.Current.CancellationToken;

        await book.TakeAsync(Alpha, seat, [], token);
        await book.TakeAsync(Beta, seat, [], token);

        Assert.Equal(2, book.Seats(seat).Count);
        Assert.Equal(2, book.Leave(seat));
        Assert.Empty(book.Seats(seat));
    }

    [Fact]
    public async Task One_person_leaving_does_not_unseat_another()
    {
        var agents = new StubAgents();
        using var book = new SeatBook(_ => agents);
        var token = TestContext.Current.CancellationToken;

        await book.TakeAsync(Alpha, Seated("arvind"), [], token);
        await book.TakeAsync(Alpha, Seated("sam"), [], token);

        Assert.Equal(1, book.Leave(Seated("arvind")));
        Assert.Single(book.Seats(Seated("sam")));
    }

    [Fact]
    public async Task A_seat_nobody_has_used_is_released()
    {
        // SPEC.md §4.0 makes an open connection the evidence that an agent is reachable, and it
        // has no heartbeat because a heartbeat manufactures presence for something that has
        // died. A browser tab is exactly that: it holds the console open long after the person
        // has gone home, and a peer reading the roster would be told somebody is there to ask.
        var agents = new StubAgents();
        using var book = new SeatBook(_ => agents);
        var seat = Seated("arvind");

        await book.TakeAsync(Alpha, seat, [], TestContext.Current.CancellationToken);

        Assert.Equal(0, book.Sweep(DateTimeOffset.UtcNow));
        Assert.Equal(1, book.Sweep(DateTimeOffset.UtcNow + SeatBook.IdleAfter + TimeSpan.FromMinutes(1)));
        Assert.Empty(book.Seats(seat));
    }

    [Fact]
    public async Task Using_the_console_keeps_the_seat()
    {
        // The other half, and the one that makes the sweep safe to have. Without it a person
        // reading a long transcript is thrown out of the deployment mid-read.
        var agents = new StubAgents();
        using var book = new SeatBook(_ => agents);
        var seat = Seated("arvind");

        await book.TakeAsync(Alpha, seat, [], TestContext.Current.CancellationToken);

        var later = DateTimeOffset.UtcNow + SeatBook.IdleAfter - TimeSpan.FromMinutes(1);

        book.Touch(seat, later);

        Assert.Equal(0, book.Sweep(later + TimeSpan.FromMinutes(2)));
        Assert.Single(book.Seats(seat));
    }

    [Fact]
    public async Task Joining_twice_holds_one_seat_and_not_two()
    {
        // Two attachments for one name is two arrivals in the registry and two departures on the
        // way out, and the second release would report an agent gone that is still connected.
        var agents = new StubAgents();
        using var book = new SeatBook(_ => agents);
        var seat = Seated("arvind");
        var token = TestContext.Current.CancellationToken;

        await book.TakeAsync(Alpha, seat, [], token);
        await book.TakeAsync(Alpha, seat, [], token);

        Assert.Single(book.Seats(seat));
        Assert.Equal(1, agents.Attaches);
    }

    [Theory]
    [InlineData("Arvind", "arvind")]
    [InlineData("  sam  ", "sam")]
    public void A_name_is_read_as_a_person_would_write_it(string typed, string expected)
    {
        // The constraint is the protocol's spelling of a role, not a rule about how anybody
        // writes their own name. Refusing "Arvind" and making somebody guess why would be the
        // console enforcing a wire format on a person.
        Assert.True(Seat.TryRead(typed, out var seat, out _));
        Assert.Equal(expected, seat.Person);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2fast")]
    [InlineData("has space")]
    [InlineData("orders@alpha")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void A_name_that_cannot_be_an_agent_is_refused_with_the_rule(string typed)
    {
        Assert.False(Seat.TryRead(typed, out _, out var problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void A_seat_reports_a_human_responder()
    {
        // SPEC.md §11.3: identity uses a scheme rather than a model name, and `human:<name>` is
        // the one for a person. Getting this wrong would make a person's answers indistinguishable
        // from a model's, which is the whole failure §11.3 exists to prevent.
        Assert.Equal("human:arvind", Seated("arvind").Responder);
        Assert.StartsWith("arvind@", Seated("arvind").Name.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void A_seat_cannot_claim_the_brokers_own_namespace()
    {
        // §11.3 reserves `parley:` and has the broker refuse an adapter claiming it, because §15
        // counts broker-authored closes to find an unhealthy deployment. A person cannot reach
        // that namespace here because the prefix is fixed and the name is a role, but the check
        // is worth having: it is one edit away from being an interpolation somebody widens.
        Assert.DoesNotContain("parley:", Seated("parley").Responder, StringComparison.Ordinal);
    }

    /// <summary>A seat for a person, with the name read the way the form reads it.</summary>
    /// <param name="person">What they typed.</param>
    /// <returns>The seat.</returns>
    private static Seat Seated(string person)
    {
        Assert.True(Seat.TryRead(person, out var seat, out _));

        return seat;
    }

    /// <summary>A registry row for a name on this machine.</summary>
    /// <param name="role">The role half.</param>
    /// <param name="adapter">What the row says is running it.</param>
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

    /// <summary>An agent client that acknowledges an attach and holds a stream that never ends.</summary>
    /// <remarks>
    /// Enough to be attached and no more. What these tests are about is which attaches happen and
    /// which do not, so the stream's job is to acknowledge and then block — which is also what a
    /// real one does for almost all of its life.
    /// </remarks>
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
