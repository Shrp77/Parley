using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Parley.Mcp;

/// <summary>
/// Tells a Claude Code session that Parley has work waiting, through a channel, so the session takes a turn without
/// anybody typing.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this closes.</b> The broker already pushes a count down the attach stream the moment mail arrives (SPEC.md
/// §4), and until now the count stopped here: an agent attached through somebody's open session learned of it only at
/// the next hook, which is to say the next time a person typed. Claude Code channels let an MCP server push an event
/// into a running session — <c>notifications/claude/channel</c>, behind the <c>claude/channel</c> capability — and the
/// session reacts to it. That is the missing step, for every session that is open.
/// </para>
/// <para>
/// <b>Counts, never words.</b> §4: a notice carries how much is waiting and the adapter drains to learn what. A channel
/// event reaches the model outside the envelope §13 puts around a peer's words, so what it carries is this adapter's
/// sentence and two numbers — never a sender, a subject or a payload. The model calls <c>parley_inbox</c>, and the words
/// arrive the way they always have.
/// </para>
/// <para>
/// <b>Once per rise.</b> An event is sent when the count of messages and closure polls waiting goes up, and not again
/// until it has gone down — which only a collection does. Claude Code queues events that arrive while it is busy and
/// hands them over together, so this bounds the events and the queue bounds the turns: sixty Requests in a burst are
/// one event, which is §5.4's coalescing reached from the other side. File notices never count: §9.5 says a notice
/// wakes nobody, and this is a wake.
/// </para>
/// <para>
/// <b>Harmless where it is not wanted.</b> A client that did not load this server as a channel drops the event
/// silently, per the channels reference, and any other MCP client ignores a notification it does not know. So the
/// capability is always declared and no launch configuration has to say anything; the person opts in by how they start
/// Claude Code.
/// </para>
/// <para>
/// <b>Nothing is sent before the client has initialised.</b> A notification ahead of the handshake is a protocol
/// violation, and the broker's first notice arrives during the attach, which runs before the transport is up. The loop
/// looks again every few seconds, so a count that rose before the session was ready is still told once it is.
/// </para>
/// </remarks>
public sealed partial class ChannelNudges(BrokerLink link, McpServer server, ILogger<ChannelNudges> logger)
    : BackgroundService
{
    /// <summary>The Claude Code notification that pushes an event into a session.</summary>
    public const string Method = "notifications/claude/channel";

    /// <summary>The experimental capability that makes Claude Code register a listener for <see cref="Method"/>.</summary>
    public const string Capability = "claude/channel";

    /// <summary>How long the loop waits for a notice before looking again.</summary>
    public static readonly TimeSpan LookAgainEvery = TimeSpan.FromSeconds(5);

    private readonly BrokerLink _link = link ?? throw new ArgumentNullException(nameof(link));
    private readonly McpServer _server = server ?? throw new ArgumentNullException(nameof(server));
    private readonly ILogger<ChannelNudges> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly Lock _gate = new();
    private int _lowestSinceLook = int.MaxValue;

    /// <summary>Decides whether a count is worth an event.</summary>
    /// <param name="told">The count last told, or last seen falling.</param>
    /// <param name="waiting">Messages and closure polls waiting now.</param>
    /// <returns>Whether to tell, and the count to remember.</returns>
    /// <remarks>
    /// A fall is remembered as well as a rise, so that mail arriving after a collection is told even when the new count
    /// is no higher than one told before it.
    /// </remarks>
    public static (bool Tell, int Remember) Decide(int told, int waiting) =>
        waiting > told ? (true, waiting) : (false, Math.Min(told, waiting));

    /// <summary>The event for a count, in this adapter's words.</summary>
    /// <param name="agent">The agent this server speaks for.</param>
    /// <param name="messages">Messages waiting.</param>
    /// <param name="polls">Closure polls waiting.</param>
    /// <returns>The notification's parameters.</returns>
    public static JsonObject Event(string agent, int messages, int polls)
    {
        var what = new List<string>();

        if (messages > 0)
        {
            what.Add(string.Create(CultureInfo.InvariantCulture, $"{messages} message(s)"));
        }

        if (polls > 0)
        {
            what.Add(string.Create(CultureInfo.InvariantCulture, $"{polls} closure poll(s)"));
        }

        return new JsonObject
        {
            ["content"] =
                $"Parley has {string.Join(" and ", what)} waiting for {agent}. Call parley_inbox to collect them; what " +
                "they say arrives there, marked as another participant's words, and not here.",
            ["meta"] = new JsonObject
            {
                ["agent"] = agent,
                ["messages_waiting"] = messages.ToString(CultureInfo.InvariantCulture),
                ["closure_polls_waiting"] = polls.ToString(CultureInfo.InvariantCulture),
            },
        };
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _link.NoticeReceived -= OnNotice;
        _link.Collected -= OnCollected;
        _signal.Dispose();
        base.Dispose();
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _link.NoticeReceived += OnNotice;
        _link.Collected += OnCollected;

        var told = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(LookAgainEvery, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // ClientInfo rather than ClientCapabilities: every client names itself in the handshake, and one that declares no
            // capabilities leaves the other null for ever -- found by the first test of this, which waited on it.
            if (_server.ClientInfo is null)
            {
                continue;
            }

            var waiting = _link.Waiting;
            int lowest;

            lock (_gate)
            {
                lowest = _lowestSinceLook;
                _lowestSinceLook = int.MaxValue;
            }

            // The lowest count any notice carried since the last look, and not only the latest: a collection and new mail
            // both landing between two looks would otherwise read as a count that never fell, and the new mail as told.
            var (tell, remember) = Decide(Math.Min(told, lowest), waiting.MessagesWaiting + waiting.ClosurePollsWaiting);

            if (!tell)
            {
                told = remember;
                continue;
            }

            try
            {
                await _server.SendNotificationAsync(
                    Method,
                    Event(_link.Agent.Value, waiting.MessagesWaiting, waiting.ClosurePollsWaiting),
                    cancellationToken: stoppingToken).ConfigureAwait(false);

                told = remember;
            }
            catch (OperationCanceledException)
            {
                return;
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception failed)
#pragma warning restore CA1031
            {
                // A client gone, or a transport closing under the send. The count is not remembered, so the next look
                // tries again; a nudge that never arrives costs a delay, and the hooks still say what is waiting.
                LogNotSent(failed);
            }
        }
    }

    private void OnCollected(object? sender, EventArgs e)
    {
        // A collection took everything it was told about, so whatever is counted from here is new or left behind, and
        // either is worth a turn. Zero rather than what is left: mail landing between the drain and the read of what
        // remains is indistinguishable from mail that was left, and both should be told.
        lock (_gate)
        {
            _lowestSinceLook = 0;
        }

        Look();
    }

    private void OnNotice(object? sender, EventArgs e)
    {
        var waiting = _link.Waiting;

        lock (_gate)
        {
            _lowestSinceLook = Math.Min(_lowestSinceLook, waiting.MessagesWaiting + waiting.ClosurePollsWaiting);
        }

        Look();
    }

    private void Look()
    {
        // Released at most to one: several notices before the loop looks are one look, which reads the latest count.
        if (_signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "A channel event could not be sent")]
    private partial void LogNotSent(Exception failure);
}
