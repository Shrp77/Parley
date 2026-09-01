using Grpc.Core;
using Parley.Core;
using Wire = Parley.Transport;

namespace Parley.Web.Seating;

/// <summary>
/// One person, seated in one project, for as long as the stream stays open.
/// </summary>
/// <remarks>
/// <para>
/// The stream is the point rather than an implementation detail. SPEC.md §4.0 makes a connection
/// the evidence that an agent is reachable — there is no heartbeat and no timestamp to age —
/// so holding one is what puts a person in the roster beside the agents, and dropping it is what
/// takes them out. Joining and leaving are not a session flag the console keeps to itself; they
/// are the same event every other agent's arrival and departure is.
/// </para>
/// <para>
/// Notices are counted and their contents are not read here. §13 has a notice carry counts and
/// never a payload, and this holds the last one so a page can say how much is waiting without
/// draining anything — draining starts a working window (§6), and a window opened by rendering a
/// page is one nobody decided to open.
/// </para>
/// </remarks>
public sealed class SeatHold : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Wire.Agents.AgentsClient _agents;
    private readonly Wire.AttachRequest _request;
    private AsyncServerStreamingCall<Wire.Event> _stream;
    private int _disposed;

    /// <summary>How long to wait before trying a broker that has gone away again.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(2);

    /// <summary>Holds an attachment that has already been acknowledged.</summary>
    /// <param name="project">The project this seat is taken in.</param>
    /// <param name="seat">Whose seat it is.</param>
    /// <param name="agents">The client the stream was opened on.</param>
    /// <param name="request">The attach, kept so the same one can be replayed on a reconnect.</param>
    /// <param name="stream">The open stream.</param>
    /// <param name="now">When the seat was taken.</param>
    internal SeatHold(
        ProjectSettings project,
        Seat seat,
        Wire.Agents.AgentsClient agents,
        Wire.AttachRequest request,
        AsyncServerStreamingCall<Wire.Event> stream,
        DateTimeOffset now)
    {
        Settings = project;
        Project = project.Name.Value;
        Seat = seat;
        Since = now;
        TouchedUtc = now;
        _agents = agents;
        _request = request;
        _stream = stream;

        _ = Task.Run(PumpAsync);
    }

    /// <summary>The project this seat is taken in.</summary>
    public string Project { get; }

    /// <summary>Whose seat it is.</summary>
    public Seat Seat { get; }

    /// <summary>The agent name held here.</summary>
    public AgentName Name => Seat.Name;

    /// <summary>When the seat was taken.</summary>
    public DateTimeOffset Since { get; }

    /// <summary>When this person was last seen doing anything in the console.</summary>
    /// <remarks>
    /// Read by the sweep in <see cref="SeatBook"/>. A browser tab left open is not a person at a
    /// desk, and a seat that outlived its occupant would be the console manufacturing exactly the
    /// false presence §14 was rewritten to stop the sweep's old heartbeat manufacturing.
    /// </remarks>
    public DateTimeOffset TouchedUtc { get; private set; }

    /// <summary>The project this seat is in, as the manifest describes it.</summary>
    /// <remarks>
    /// Kept so the book can reach this project's broker without being handed the directory. The
    /// name alone is what pages display and what keys a seat; this is what opens a client.
    /// </remarks>
    internal ProjectSettings Settings { get; }

    private readonly List<Delivered> _delivered = [];

    /// <summary>What has been handed to this person and not yet dealt with.</summary>
    /// <remarks>
    /// Newest last, the order a transcript reads in. Kept here rather than fetched per request
    /// because a drain is not repeatable: it is what marks a message delivered, so what it
    /// returned is the only copy on the agent's own surface. A console restart loses it, and the
    /// text is still readable in the conversation the message belongs to — which is worth
    /// knowing, because the working window keeps running across the restart.
    /// </remarks>
    public IReadOnlyList<Delivered> Delivered
    {
        get
        {
            lock (_delivered)
            {
                return [.. _delivered];
            }
        }
    }

    /// <summary>Keeps what a drain returned.</summary>
    /// <param name="items">What the broker handed over.</param>
    internal void Record(IEnumerable<Delivered> items)
    {
        lock (_delivered)
        {
            _delivered.AddRange(items);
        }
    }

    /// <summary>Drops the messages belonging to an exchange that has been dealt with.</summary>
    /// <param name="exchangeId">The exchange.</param>
    /// <remarks>
    /// By exchange rather than by message, because a question and the clarifications on it are
    /// one thing owed and are settled by one answer. Leaving the others would show somebody a
    /// debt they had already paid.
    /// </remarks>
    internal void Settled(long exchangeId)
    {
        lock (_delivered)
        {
            _delivered.RemoveAll(one => one.Message.ExchangeId == exchangeId);
        }
    }

    /// <summary>Drops what has been read and owes nothing back.</summary>
    /// <returns>How many were dropped.</returns>
    /// <remarks>
    /// A debt cannot be dismissed, which is the whole of the rule. An answer or a notice is
    /// finished the moment it has been read, and leaving it there buries the one thing on the
    /// page that still needs doing; a question is settled by answering it, and a button that
    /// made it disappear instead would let somebody clear a list while the exchange behind it
    /// ran on to be ghosted by §14.
    /// </remarks>
    internal int DismissRead()
    {
        lock (_delivered)
        {
            return _delivered.RemoveAll(one => !one.Owes);
        }
    }

    /// <summary>Records that the person is still here.</summary>
    /// <param name="now">The current instant.</param>
    /// <remarks>
    /// Internal, so that <see cref="SeatBook"/> stays the only thing that takes, keeps or
    /// releases a seat. A caller able to reach past it could hold one alive without holding a
    /// reference anything would ever sweep.
    /// </remarks>
    internal void Touch(DateTimeOffset now) => TouchedUtc = now;

    /// <summary>Reads the stream, reconnecting for as long as the seat is held.</summary>
    /// <returns>A task completing when the seat is released or refused for good.</returns>
    private async Task PumpAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                await foreach (var received in _stream.ResponseStream
                    .ReadAllAsync(_cancellation.Token).ConfigureAwait(false))
                {
                    // Read and discarded. The stream has to be consumed or it backs up, but the
                    // count it carries is deliberately not kept: a notice says how much arrived
                    // and is never sent again to say how much left, so a console that cached it
                    // showed a number that only ever went up. It said "1 waiting" beside a page
                    // correctly reporting none, because this was a second answer to a question
                    // the broker was already being asked properly.
                    _ = received;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (RpcException)
            {
                // The broker went away. The loop below is what brings the seat back when it
                // returns, which matters more here than for a process-bound agent: nobody is
                // going to notice a person has silently stopped being present and restart them.
            }

            if (!await ReattachAsync().ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>Opens a new stream after the previous one ended.</summary>
    /// <returns><see langword="false"/> when the seat is being released, or refused for good.</returns>
    private async Task<bool> ReattachAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RetryAfter, _cancellation.Token).ConfigureAwait(false);

                var stream = _agents.Attach(_request, cancellationToken: _cancellation.Token);

                if (!await stream.ResponseStream.MoveNext(_cancellation.Token).ConfigureAwait(false)
                    || stream.ResponseStream.Current.EventCase
                        != Wire.Event.EventOneofCase.Attached)
                {
                    stream.Dispose();
                    continue;
                }

                Interlocked.Exchange(ref _stream, stream).Dispose();

                // Disposal can have run while this was reconnecting, in which case it disposed
                // whatever it saw and will never look again — leaving this stream open, and the
                // person present in a deployment the console has stopped holding a seat in.
                if (_cancellation.IsCancellationRequested)
                {
                    stream.Dispose();

                    return false;
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (RpcException failure) when (failure.StatusCode == StatusCode.Unavailable)
            {
                // Still down. Round again.
            }
            catch (RpcException)
            {
                // Retired, or the name has been taken by something else since. Retrying that
                // every two seconds would fill a log rather than surface anything.
                return false;
            }
        }

        return false;
    }

    /// <summary>Ends the attachment, which is what tells the deployment the person has gone.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _cancellation.Cancel();
        _stream.Dispose();

        // The source is deliberately not disposed. The pump is still inside a `Task.Delay` or an
        // `await foreach` holding its token, and disposing it under them raises
        // ObjectDisposedException rather than the OperationCanceledException every catch here is
        // written for — so the loop would escape through a path nothing handles, on a task
        // nobody observes. Cancelling is what ends the attachment; the source itself holds
        // nothing that needs reclaiming, since no wait handle is ever taken from it.
    }
}
