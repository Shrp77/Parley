using System.Collections.Concurrent;
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
/// The person's seat in every project this surface shows, and the only place one is taken or let go.
/// </summary>
/// <remarks>
/// <para>
/// <b>Having a seat and being in it are separate</b>. The broker provides the person seat in every project
/// and never releases it, so a surface never has to ask for one: it attaches as the seat the registry names. What this
/// book holds is the other half — whether somebody is at this screen now. A hold is the open stream, and SPEC.md §4.0
/// makes that stream the evidence of <c>here</c>; after <see cref="IdleAfter"/> with nothing touched it is dropped, the
/// broker reads <c>away</c>, and the next touch takes it back through <see cref="EnsureAsync"/> without a word to the
/// person, because nothing was taken from them.
/// </para>
/// <para>
/// <b>The seat each project named is remembered after its hold is dropped.</b> Mail waits against the name, not the
/// stream, so the count of what is waiting must work while the person is away — which is exactly when it matters:
/// somebody coming back after an hour is the person the count exists for.
/// </para>
/// <para>
/// <b>The registry is read before anything is attached, and one that could not be read refuses.</b> A guard that
/// skipped its check on an unreachable broker would pass hardest exactly when the deployment is least well known. A
/// registry with no person row is a broker older than 0.36.0; that is said rather than papered over with a name this
/// surface made up, which is the failure 023 exists to end.
/// </para>
/// </remarks>
public sealed class SeatBook : IDisposable
{
    /// <summary>How long a hold survives with nobody using this surface.</summary>
    /// <remarks>
    /// A tab left open is not a person at a desk. Past this the stream is dropped and the broker reads the person as
    /// away; the seat itself is untouched, and the next touch takes the stream back.
    /// </remarks>
    public static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(20);

    private readonly ConcurrentDictionary<string, SeatHold> _held = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (ProjectSettings Project, Seat Seat)> _known =
        new(StringComparer.Ordinal);

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

    /// <summary>What a surface says when a project's broker provides no person seat.</summary>
    public const string NoPersonSeat =
        "This project's broker has no seat for a person, so it is older than 0.36.0 and nothing here can speak in it. "
        + "Upgrade it (install with -Reset), and the seat is there when it next starts.";

    /// <summary>Takes the person's seat in a project, as its registry names it, or says why it could not.</summary>
    /// <param name="project">The project.</param>
    /// <param name="registry">
    /// That project's registry as the broker reported it, or <see langword="null"/> when it could not be read.
    /// </param>
    /// <param name="cancellationToken">Cancels the attach.</param>
    /// <returns>The seat, or the reason there is none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> is <see langword="null"/>.</exception>
    public async Task<SeatOutcome> TakeAsync(
        ProjectSettings project,
        IReadOnlyList<AgentRecord>? registry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = project.Name.Value;

        if (_held.TryGetValue(key, out var already))
        {
            if (!already.Lost)
            {
                already.Touch(DateTimeOffset.UtcNow);

                return new SeatOutcome(already, null);
            }

            // A lost hold is a dead attachment. Handing it back would tell somebody they were seated on a stream that
            // has ended; it is cleared and the seat taken again.
            if (_held.TryRemove(key, out var dead))
            {
                dead.Dispose();
            }
        }

        if (registry is null)
        {
            return new SeatOutcome(
                null,
                "This project's broker did not answer, so its seat for the person could not be found. Nothing was "
                + "taken; it is tried again the next time you do anything here.");
        }

        if (Seat.In(registry) is not { } seat)
        {
            return new SeatOutcome(null, NoPersonSeat);
        }

        var request = new Wire.AttachRequest
        {
            Agent = seat.Name.Value,
            Adapter = Seat.Adapter,

            // Pull, of the three §11.2 offers. A person has no turn to interrupt, which rules out Full, and holds a
            // position across turns, which is exactly what Headless cannot do.
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

            // Awaited here so that a refusal is a failed seat rather than one that looks taken and fails on the first
            // thing said from it.
            if (!await stream.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false)
                || stream.ResponseStream.Current.EventCase != Wire.Event.EventOneofCase.Attached)
            {
                stream.Dispose();

                return new SeatOutcome(
                    null,
                    "This project's broker did not acknowledge the seat, which means it is speaking a different "
                    + "version of the protocol than this surface.");
            }

            var hold = new SeatHold(project, seat, agents, request, stream, DateTimeOffset.UtcNow);

            if (!_held.TryAdd(key, hold))
            {
                // Another caller took it first. Theirs is as good as this one, and two streams from one surface is two
                // arrivals in the registry for one person.
                hold.Dispose();

                return _held.TryGetValue(key, out var theirs) && !theirs.Lost
                    ? new SeatOutcome(theirs, null)
                    : new SeatOutcome(null, "The seat was taken and let go again. Try once more.");
            }

            _known[key] = (project, seat);

            // Disposal can have run between the check at the top of this method and here, and it drains what it can
            // see. A hold added after that drain would be a person marked here with nothing left to let them go.
            if (_disposed)
            {
                if (_held.TryRemove(key, out var orphan))
                {
                    orphan.Dispose();
                }

                return new SeatOutcome(null, "This surface is shutting down.");
            }

            return new SeatOutcome(hold, null);
        }
        catch (RpcException failure) when (failure.StatusCode == StatusCode.Unavailable)
        {
            stream?.Dispose();

            return new SeatOutcome(null, "This project's broker is not answering, so its seat was not taken.");
        }
        catch (RpcException failure)
        {
            stream?.Dispose();

            return new SeatOutcome(null, $"The broker refused the seat: {failure.Status.Detail}");
        }
        catch (Exception failure) when (failure is UriFormatException or InvalidOperationException)
        {
            stream?.Dispose();

            return new SeatOutcome(null, $"This project's endpoint cannot be reached as written: {failure.Message}");
        }
    }

    /// <summary>The person's seat in a project, taking it again first if nothing holds it now.</summary>
    /// <param name="project">The project.</param>
    /// <param name="registry">Reads that project's registry, asked only when the seat has to be taken.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The seat, or the reason there is none.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// <b>What a surface calls whenever the person does something</b>: on opening, and after an idle drop on the next
    /// keystroke or page. The registry is read only when there is no live hold, so the ordinary case costs nothing.
    /// </remarks>
    public async Task<SeatOutcome> EnsureAsync(
        ProjectSettings project,
        Func<CancellationToken, Task<IReadOnlyList<AgentRecord>?>> registry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(registry);

        if (Held(project.Name.Value) is { } held)
        {
            held.Touch(DateTimeOffset.UtcNow);

            return new SeatOutcome(held, null);
        }

        return await TakeAsync(project, await registry(cancellationToken).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The person's seat in a project while somebody is at this surface, or <see langword="null"/>.</summary>
    /// <param name="project">The project's name.</param>
    /// <returns>The seat.</returns>
    /// <remarks>
    /// A hold whose attachment has ended for good is not a seat, and is reported absent the moment it is asked for:
    /// the question a surface is asking is whether to act as the person now.
    /// </remarks>
    public SeatHold? Held(string project)
    {
        var held = _held.GetValueOrDefault(project);

        return held is null || held.Lost ? null : held;
    }

    /// <summary>The person seat a project named, whether or not anything holds it now.</summary>
    /// <param name="project">The project's name.</param>
    /// <returns>The seat, or <see langword="null"/> if this surface has never taken it.</returns>
    public Seat? Known(string project) => _known.TryGetValue(project, out var known) ? known.Seat : null;

    /// <summary>Every seat held now, ordered by project.</summary>
    /// <returns>The holds.</returns>
    public IReadOnlyList<SeatHold> Seats() =>
        [.. _held.Values.Where(held => !held.Lost).OrderBy(held => held.Project, StringComparer.Ordinal)];

    /// <summary>How much is waiting for the person, in every project given.</summary>
    /// <param name="projects">The projects to count in; each is asked independently.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>
    /// The total, and whether every project answered. A total of zero nobody could confirm is not the same fact as a
    /// total of zero that was measured.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="projects"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// <b>Counted whether or not anything is held</b>, so it works while the person is away — mail waits against the
    /// name, and somebody coming back is who the count is for. The name is the seat this surface took when it has taken
    /// one, and otherwise the one SPEC.md §12.3 says the broker provides, <c>human-user@&lt;project&gt;</c>: a broker
    /// whose registry could not be read may still answer how much is waiting, and "nothing" said because no seat had
    /// been taken yet would be 0.29.1's false sentence again.
    /// </para>
    /// <para>
    /// Asked of the broker rather than remembered: a notice arrives when something lands and never when something is
    /// collected, so a count kept from the stream only ever climbs. <see cref="SeatVoice.WaitingAsync"/> marks nothing
    /// delivered and opens nothing, which is what makes it safe to call while drawing a frame.
    /// </para>
    /// </remarks>
    public async Task<(int Waiting, bool Certain)> WaitingAsync(
        IEnumerable<ProjectSettings> projects, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projects);

        var total = 0;
        var certain = true;

        foreach (var project in projects)
        {
            var seat = Known(project.Name.Value) ?? new Seat(PersonSeat.On(project.Name));

            var read = await BrokerDirectory.ReadAsync(async () =>
                await new SeatVoice(_agents(project), seat)
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

    /// <summary>Records that the person is still at this surface.</summary>
    /// <param name="now">The current instant.</param>
    public void Touch(DateTimeOffset now)
    {
        foreach (var held in Seats())
        {
            held.Touch(now);
        }
    }

    /// <summary>Drops the hold of any seat nobody has touched for <see cref="IdleAfter"/>, and any that was lost.</summary>
    /// <param name="now">The current instant.</param>
    /// <returns>How many holds were dropped.</returns>
    /// <remarks>
    /// The seat is not released — the broker never releases it — only this surface's stream, so the broker reads the
    /// person as away. What the seat was is kept in <see cref="Known"/>.
    /// </remarks>
    public int Sweep(DateTimeOffset now)
    {
        var dropped = 0;

        foreach (var (key, held) in _held)
        {
            if (!held.Lost && now - held.TouchedUtc < IdleAfter)
            {
                continue;
            }

            if (_held.TryRemove(key, out var gone))
            {
                gone.Dispose();
                dropped++;
            }
        }

        return dropped;
    }

    /// <summary>Drops every hold, which is what a surface closing owes the registry: the person is away.</summary>
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
}
