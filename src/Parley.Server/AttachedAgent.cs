using System.Threading.Channels;
using Parley.Core;
using Wire = Parley.Transport;

namespace Parley.Server;

/// <summary>
/// One agent's open <c>Attach</c> stream, which is the whole of what "connected" means.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §4.0: a connection is evidence. This object exists for exactly as long as the stream
/// does, and its disposal is what tells the registry the agent has gone — so a clean shutdown, a
/// killed process and a severed network are one event rather than three cases, because from this
/// side of the socket they are one event.
/// </para>
/// <para>
/// <b>The queue drops rather than blocks.</b> A notice says how much is waiting, so the newest
/// one makes every older one redundant: an adapter that receives "4 waiting" has no use for the
/// "2 waiting" it never read. Keeping a single slot and overwriting it means a slow reader
/// falls behind by latency rather than by memory, and cannot make the broker wait — which
/// matters because the broker writes these while holding nothing, but the alternative is an
/// unbounded queue per agent and an agent that never reads is exactly the agent that has stopped
/// working.
/// </para>
/// </remarks>
public sealed class AttachedAgent : IAsyncDisposable
{
    private readonly BrokerRuntime _runtime;
    private readonly Channel<Wire.Notice> _notices;

    private int _closed;

    internal AttachedAgent(
        BrokerRuntime runtime,
        AgentName name,
        Parley.Broker.Broker broker,
        Parley.Broker.StartResult started)
    {
        _runtime = runtime;
        Name = name;
        Broker = broker;
        Started = started;

        // One slot, newest wins. See the remarks: a notice supersedes its predecessor entirely,
        // so a backlog of them carries no information a single current one does not.
        _notices = Channel.CreateBounded<Wire.Notice>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
    }

    /// <summary>Gets the agent this stream belongs to.</summary>
    public AgentName Name { get; }

    /// <summary>Gets what the attach sequence of SPEC.md §14 did.</summary>
    public Parley.Broker.StartResult Started { get; }

    /// <summary>Gets the broker bound to this agent.</summary>
    internal Parley.Broker.Broker Broker { get; }

    /// <summary>Gets the notices waiting to go down the stream.</summary>
    /// <value>Completes when the attachment closes, which ends the stream.</value>
    internal ChannelReader<Wire.Notice> Notices => _notices.Reader;

    /// <summary>Queues a notice, discarding any the reader has not taken.</summary>
    /// <param name="messagesWaiting">How many messages are waiting.</param>
    /// <param name="pollsWaiting">How many closure polls are waiting.</param>
    internal void Post(int messagesWaiting, int pollsWaiting) =>
        _ = _notices.Writer.TryWrite(new Wire.Notice
        {
            MessagesWaiting = messagesWaiting,
            ClosurePollsWaiting = pollsWaiting
        });

    /// <summary>Ends the stream without touching the registry.</summary>
    /// <remarks>
    /// For a runtime shutting down, where the connection is about to close and a detach written
    /// against it would either fail or be lost. The registry is left as it is on purpose: a
    /// broker that stopped is a broker whose <c>connected</c> counts are meaningless until it
    /// comes back, and it reconciles them at startup rather than racing to tidy up on the way
    /// out.
    /// </remarks>
    internal void Close() => _ = _notices.Writer.TryComplete();

    /// <summary>Ends the stream and tells the registry the agent has gone.</summary>
    /// <returns>A task that completes when the registry has been told.</returns>
    public async ValueTask DisposeAsync()
    {
        // Once. A stream can end and be disposed by both the handler's finally and the host's
        // shutdown, and a second detach would decrement `connected` for a session that had
        // already been accounted for -- which is how an agent with one window open comes to look
        // like an agent with none.
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return;
        }

        Close();

        await _runtime.ReleaseAsync(this, CancellationToken.None).ConfigureAwait(false);
    }
}
