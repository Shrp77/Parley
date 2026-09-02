using System.Collections.Concurrent;
using System.Globalization;
using Grpc.Core;
using Parley.Core;
using Wire = Parley.Transport;

namespace Parley.Client.Seating;

/// <summary>What became of an attempt to take a seat.</summary>
/// <param name="Hold">The seat, when one was taken.</param>
/// <param name="Problem">Why it was not, when it was not.</param>
public sealed record SeatOutcome(SeatHold? Hold, string? Problem)
{
    /// <summary>Whether the person is now seated.</summary>
    public bool Seated => Hold is not null;
}

/// <summary>
/// Every seat this console is holding, and the only place one is taken or released.
/// </summary>
/// <remarks>
/// <para>
/// A singleton because a seat outlives the request that took it: SPEC.md §4.0 makes the open
/// stream the evidence of presence, so something has to hold that stream between page loads.
/// This is that something, and it is deliberately the only one — a seat opened by a page and
/// forgotten would be a person the deployment believes is present for as long as the console
/// runs.
/// </para>
/// <para>
/// <b>The name is checked against the registry before anything is attached.</b> Attaching
/// registers a name that is absent (the broker's <c>RegisterIfAbsent</c>), so a person typing
/// <c>orders</c> would otherwise take over the orders agent's identity and speak in its voice,
/// with the transcript recording words that agent never said. That is the one genuinely new
/// power a seat adds over what §10.2 already grants a console — an operator can instruct, retire
/// and purge any agent, but has never been able to put words in one's mouth — so it is the one
/// thing guarded here.
/// </para>
/// <para>
/// The guard is a console-side check against data the broker supplied, and is not claimed as
/// more than that. §16 already concedes that anything able to reach a broker can claim to be any
/// agent, so this stops an honest collision rather than an attacker — which is what the risk
/// actually is on a loopback console whose operator can already purge the registry.
/// </para>
/// </remarks>
public sealed class SeatBook : IDisposable
{
    /// <summary>How long a seat survives with nobody using the console.</summary>
    /// <remarks>
    /// A tab left open is not a person at a desk. Without this the roster would show somebody
    /// present until the console was restarted, which is the false presence §14 removed the
    /// sweep's heartbeat to stop manufacturing — and it would be worse here, because nobody
    /// restarts a person.
    /// </remarks>
    public static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(20);

    private readonly ConcurrentDictionary<string, SeatHold> _held = new(StringComparer.Ordinal);
    private readonly Func<ProjectSettings, Wire.Agents.AgentsClient> _agents;
    private readonly Timer _sweep;
    private volatile bool _disposed;

    /// <summary>Builds the book.</summary>
    /// <param name="agents">Opens an agent client for a project. Injected so tests need no sockets.</param>
    /// <exception cref="ArgumentNullException"><paramref name="agents"/> is <see langword="null"/>.</exception>
    public SeatBook(Func<ProjectSettings, Wire.Agents.AgentsClient> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);

        _agents = agents;
        _sweep = new Timer(
            _ => Sweep(DateTimeOffset.UtcNow),
            null,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
    }

    /// <summary>Takes a seat for one person in one project, or says why it could not.</summary>
    /// <param name="project">The project to be seated in.</param>
    /// <param name="seat">Who is sitting down.</param>
    /// <param name="registry">
    /// That project's registry as the broker reported it, or <see langword="null"/> when it could
    /// not be read.
    /// </param>
    /// <param name="cancellationToken">Cancels the attach.</param>
    /// <returns>The seat, or the reason there is none.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// A registry that could not be read refuses the seat rather than skipping the check. The
    /// alternative is a guard that passes hardest exactly when the broker is least well known,
    /// which is the shape of every check that has ever been found to be testing nothing.
    /// </remarks>
    public async Task<SeatOutcome> TakeAsync(
        ProjectSettings project,
        Seat seat,
        IReadOnlyList<AgentRecord>? registry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(seat);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = Key(project.Name.Value, seat);

        if (_held.TryGetValue(key, out var already))
        {
            already.Touch(DateTimeOffset.UtcNow);

            return new SeatOutcome(already, null);
        }

        if (registry is null)
        {
            return new SeatOutcome(
                null,
                "This project's broker did not answer, so the name could not be checked against "
                + "its registry. Nothing was joined.");
        }

        var name = seat.Name;

        if (registry.FirstOrDefault(row => row.Name == name) is { } taken
            && !string.Equals(taken.Adapter, Seat.Adapter, StringComparison.Ordinal))
        {
            return new SeatOutcome(
                null,
                $"'{name.Value}' is already registered here as an agent, running "
                + $"'{taken.Adapter}'. Joining under that name would let you speak as it, and "
                + "the transcript would record words it never said. Pick another name.");
        }

        var request = new Wire.AttachRequest
        {
            Agent = name.Value,
            Adapter = Seat.Adapter,

            // Pull, of the three §11.2 offers. A person has no turn to interrupt, which rules out
            // Full, and holds a position across turns, which is exactly what Headless cannot do.
            // What they do is see what is waiting when they look, which is what Pull describes.
            Tier = Wire.AgentTier.Pull,
            Workspace = string.Empty,
            SessionRef = string.Empty,
            AdapterConfig = string.Empty,
            Owns = string.Empty,
            ProtocolVersion = ProtocolVersion.Current
        };

        AsyncServerStreamingCall<Wire.Event>? stream = null;

        try
        {
            var agents = _agents(project);

            stream = agents.Attach(request, cancellationToken: cancellationToken);

            // Awaited here so that a refusal is a failed join rather than a seat that looks taken
            // and fails on the first thing said from it.
            if (!await stream.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false)
                || stream.ResponseStream.Current.EventCase != Wire.Event.EventOneofCase.Attached)
            {
                stream.Dispose();

                return new SeatOutcome(
                    null,
                    "This project's broker did not acknowledge the join, which means it is "
                    + "speaking a different version of the protocol than this console.");
            }

            var hold = new SeatHold(
                project, seat, agents, request, stream, DateTimeOffset.UtcNow);

            if (!_held.TryAdd(key, hold))
            {
                // Another request seated the same person first. Theirs is as good as this one,
                // and two streams for one seat is two arrivals in the registry.
                hold.Dispose();

                // Read back rather than indexed: the winner can have been released between the
                // add failing and the read, and an indexer would throw on a web request for what
                // is an ordinary outcome.
                return _held.TryGetValue(key, out var theirs)
                    ? new SeatOutcome(theirs, null)
                    : new SeatOutcome(null, "The seat was taken and released again. Try once more.");
            }

            // Disposal can have run between the check at the top of this method and here, and it
            // drains what it can see. A hold added after that drain is a person the deployment
            // believes is present with nothing left that could release them.
            if (_disposed)
            {
                if (_held.TryRemove(key, out var orphan))
                {
                    orphan.Dispose();
                }

                // Refused whether or not this removed it. Losing the race means disposal got
                // there first, so the hold is already dead — and handing back a dead seat is
                // worse than saying so, because a page would render somebody as present.
                return new SeatOutcome(null, "The console is shutting down.");
            }

            return new SeatOutcome(hold, null);
        }
        catch (RpcException failure) when (failure.StatusCode == StatusCode.Unavailable)
        {
            stream?.Dispose();

            return new SeatOutcome(
                null, "This project's broker is not answering, so nobody was joined to it.");
        }
        catch (RpcException failure)
        {
            stream?.Dispose();

            return new SeatOutcome(null, $"The broker refused the join: {failure.Status.Detail}");
        }
        catch (Exception failure) when (failure is UriFormatException or InvalidOperationException)
        {
            stream?.Dispose();

            return new SeatOutcome(
                null, $"This project's endpoint cannot be reached as written: {failure.Message}");
        }
    }

    /// <summary>The seat this person holds in a project, if they hold one.</summary>
    /// <param name="project">The project.</param>
    /// <param name="seat">Who to look for.</param>
    /// <returns>The seat, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public SeatHold? Held(string project, Seat seat)
    {
        ArgumentNullException.ThrowIfNull(seat);

        return _held.GetValueOrDefault(Key(project, seat));
    }

    /// <summary>Every seat this person is holding, in whatever project.</summary>
    /// <param name="seat">Who to look for.</param>
    /// <returns>Their seats, ordered by project.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="seat"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<SeatHold> Seats(Seat seat)
    {
        ArgumentNullException.ThrowIfNull(seat);

        return [.. _held.Values
            .Where(held => string.Equals(held.Seat.Person, seat.Person, StringComparison.Ordinal))
            .OrderBy(held => held.Project, StringComparer.Ordinal)];
    }

    /// <summary>How much is waiting for this person, across every seat they hold.</summary>
    /// <param name="seat">Who to count for.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>
    /// The total, and whether every project answered. A total of zero that nobody could confirm
    /// is not the same fact as a total of zero that was measured.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="seat"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// Asked of the broker rather than remembered. There was a cached count here, taken from the
    /// notices the attach stream pushes, and it was wrong in one direction only: a notice is sent
    /// when something arrives and never again when something leaves, so after this console
    /// drained its own inbox the number stayed up. The frame said one message was waiting beside
    /// a page that correctly said none.
    /// </para>
    /// <para>
    /// That is the shape of the fault rather than the detail of it — there were two answers to
    /// one question, and the fix is that there is now one. It matches how the rest of this
    /// console treats the broker: reachability is not held either, it is observed per call,
    /// because a fact that was true when a page was built says nothing about now.
    /// </para>
    /// <para>
    /// <see cref="SeatVoice.WaitingAsync"/> and not a drain. It marks nothing delivered and
    /// starts no working window, which is what makes it safe to call while rendering a frame
    /// that every page carries.
    /// </para>
    /// </remarks>
    public async Task<(int Waiting, bool Certain)> WaitingAsync(
        Seat seat, CancellationToken cancellationToken = default)
    {
        var total = 0;
        var certain = true;

        foreach (var held in Seats(seat))
        {
            var read = await BrokerDirectory.ReadAsync(async () =>
                await new SeatVoice(_agents(held.Settings), held.Seat)
                    .WaitingAsync(cancellationToken).ConfigureAwait(false))
                .ConfigureAwait(false);

            if (read.Problem is null)
            {
                total += read.Value.Messages;
            }
            else
            {
                certain = false;
            }
        }

        return (total, certain);
    }

    /// <summary>Records that this person is still at the console.</summary>
    /// <param name="seat">Who was seen.</param>
    /// <param name="now">The current instant.</param>
    /// <exception cref="ArgumentNullException"><paramref name="seat"/> is <see langword="null"/>.</exception>
    public void Touch(Seat seat, DateTimeOffset now)
    {
        foreach (var held in Seats(seat))
        {
            held.Touch(now);
        }
    }

    /// <summary>Releases every seat this person holds.</summary>
    /// <param name="seat">Who is leaving.</param>
    /// <returns>How many seats were given up.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="seat"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Every project at once, because a person leaves the console rather than leaving a page. A
    /// logout that released only the seat they happened to be looking at would leave them present
    /// in the others with no surface anywhere saying so.
    /// </remarks>
    public int Leave(Seat seat)
    {
        var released = 0;

        foreach (var held in Seats(seat))
        {
            if (_held.TryRemove(Key(held.Project, seat), out var gone))
            {
                gone.Dispose();
                released++;
            }
        }

        return released;
    }

    /// <summary>Releases seats nobody has used for <see cref="IdleAfter"/>.</summary>
    /// <param name="now">The current instant.</param>
    /// <returns>How many seats were released.</returns>
    public int Sweep(DateTimeOffset now)
    {
        var released = 0;

        foreach (var (key, held) in _held)
        {
            if (now - held.TouchedUtc < IdleAfter)
            {
                continue;
            }

            if (_held.TryRemove(key, out var gone))
            {
                gone.Dispose();
                released++;
            }
        }

        return released;
    }

    /// <summary>Releases every seat, which is what a console shutting down owes the registry.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sweep.Dispose();

        foreach (var key in _held.Keys)
        {
            if (_held.TryRemove(key, out var held))
            {
                held.Dispose();
            }
        }
    }

    /// <summary>One seat's key: a person in a project.</summary>
    /// <param name="project">The project.</param>
    /// <param name="seat">The person.</param>
    /// <returns>A key that cannot be produced by a different pair.</returns>
    /// <remarks>
    /// Separated by a character neither half can contain, so <c>a</c> in <c>b-c</c> and
    /// <c>a-b</c> in <c>c</c> are two keys rather than one.
    /// </remarks>
    private static string Key(string project, Seat seat) =>
        string.Create(CultureInfo.InvariantCulture, $"{project}\u0000{seat.Person}");
}
