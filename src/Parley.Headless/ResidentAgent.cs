using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Parley.Core;

namespace Parley.Headless;

/// <summary>
/// One agent, started once and kept alive across many turns.
/// </summary>
/// <remarks>
/// <para>
/// A process started
/// per message can answer a bounded factual question — the envelope of §5.3 carries the ask
/// back, so a cold reader has what it needs — but it cannot hold a position
/// across turns. It cannot remember why it asked a clarification, cannot get better at its own
/// domain by answering thirty questions about it, and cannot negotiate a contested boundary,
/// which is the case the protocol exists for.
/// </para>
/// <para>
/// So the process stays up and the turns arrive over its standard input. A resident agent keeps
/// one conversation for its whole life: it accumulates its own diplomatic history, separate from
/// whatever a person is doing in that repository, and every turn lands in a session that
/// remembers the last one.
/// </para>
/// <para>
/// <b>Standard input is the turn channel.</b> It is how a turn is delivered, and it stays open
/// for exactly as long as the agent should live. Closing it is how the agent is told to quit.
/// </para>
/// <para>
/// Standard output is drained continuously by a background pump rather than read per turn. With
/// a long-lived process that is not a nicety: a pipe nobody is reading fills, and a full pipe
/// blocks the writer, so an unread agent wedges at exactly the moment it has most to say.
/// </para>
/// </remarks>
public sealed class ResidentAgent : IAsyncDisposable
{
    /// <summary>
    /// How much of standard error to keep for diagnostics.
    /// </summary>
    /// <remarks>
    /// Bounded and never persisted. Nothing an agent *says* is kept here — that travels as
    /// messages under §9.4's retention, and a second uncontrolled copy would sit outside every
    /// guard governing the first. What this holds is the runner failing to start: "command not
    /// found", a bad flag, an auth error. Without it a misconfigured fleet reports only that
    /// every agent died, which names the symptom and hides the cause.
    /// </remarks>
    private const int DiagnosticsLimit = 4000;

    private readonly Process _process;
    private readonly Channel<JsonNode> _events;
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private readonly StringBuilder _diagnostics = new();
    private readonly Lock _diagnosticsLock = new();
    private readonly TimeProvider _clock;
    private readonly Task _outputPump;
    private readonly Task _errorPump;

    private int _abandonedResults;
    private volatile string? _sessionId;
    private volatile bool _stopping;

    private ResidentAgent(
        AgentName name, string workspace, Process process, TimeProvider clock)
    {
        Name = name;
        Workspace = workspace;
        _process = process;
        _clock = clock;
        _events = Channel.CreateUnbounded<JsonNode>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        _outputPump = Task.Run(PumpOutputAsync);
        _errorPump = Task.Run(PumpErrorAsync);
    }

    /// <summary>Gets who this agent is, as the registry names it.</summary>
    public AgentName Name { get; }

    /// <summary>Gets the directory the agent runs in.</summary>
    public string Workspace { get; }

    /// <summary>Gets the runner's own identifier for the conversation, once it has reported one.</summary>
    /// <remarks>
    /// Useful for an operator who wants to open the same conversation by hand and read what the
    /// agent has been saying. It is reported by the runner rather than chosen here, because a
    /// resident agent that is restarted gets a new one and pretending otherwise would mislead.
    /// </remarks>
    public string? SessionId => _sessionId;

    /// <summary>Gets whether the agent's process is still running.</summary>
    public bool IsAlive
    {
        get
        {
            try
            {
                return !_process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>Gets whatever the runner wrote to standard error, up to a bounded length.</summary>
    /// <returns>The diagnostics tail, or an empty string.</returns>
    public string Diagnostics()
    {
        lock (_diagnosticsLock)
        {
            return _diagnostics.ToString();
        }
    }

    /// <summary>
    /// Starts an agent and leaves it running.
    /// </summary>
    /// <param name="name">Who is being started.</param>
    /// <param name="command">What to run.</param>
    /// <param name="workspace">The directory to run it in.</param>
    /// <param name="clock">The clock turns are measured against.</param>
    /// <param name="environment">
    /// Added to what the process inherits — the launch token a node gives each process it starts (SPEC.md §11.7).
    /// </param>
    /// <returns>The running agent.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="DirectoryNotFoundException">The workspace does not exist.</exception>
    /// <exception cref="InvocationFailedException">The runner could not be started.</exception>
    /// <remarks>
    /// The workspace is checked before launching rather than after failing. A registry entry can
    /// name a directory that has since moved, and the process-start failure for that is an
    /// obscure platform error; telling an operator which of the two is wrong is the whole
    /// difference between a fixable fleet and a mysterious one.
    /// </remarks>
    public static ResidentAgent Start(
        AgentName name,
        InvocationCommand command,
        string workspace,
        TimeProvider clock,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);

        if (!Directory.Exists(workspace))
        {
            throw new DirectoryNotFoundException(
                $"The registry says {name.Value} works in '{workspace}', and that directory does " +
                "not exist. It cannot be started until the registry and the disk agree.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            WorkingDirectory = workspace,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Added to what the process inherits, never in place of it: the agent's own tools need PATH and the rest.
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[key] = value;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();

            throw new InvocationFailedException(
                $"Could not start {name.Value}: '{command.FileName}' would not run in " +
                $"'{workspace}'. {ex.Message}",
                ex);
        }

        return new ResidentAgent(name, workspace, process, clock);
    }

    /// <summary>
    /// Gives the agent a turn and waits for it to finish.
    /// </summary>
    /// <param name="text">What to say to it.</param>
    /// <param name="timeout">How long to wait before giving up on this turn.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>What the turn cost and how it ended.</returns>
    /// <exception cref="ArgumentException"><paramref name="text"/> is empty.</exception>
    /// <remarks>
    /// <para>
    /// One turn at a time. Two turns in flight would interleave on one stream with no way to tell
    /// whose result was whose, and the agent would be answering two prompts in one context.
    /// </para>
    /// <para>
    /// <b>A timeout does not kill the agent.</b> The process holds the agent's entire accumulated context, and discarding that because one
    /// turn ran long is a far worse trade than waiting. So a timed-out turn is recorded and left:
    /// the exchange is unharmed — still owed, still open, tried again — and the result that
    /// eventually arrives is discarded before the next turn rather than being read as its answer.
    /// Without that bookkeeping the stream desynchronises and every later turn reads the previous
    /// turn's reply.
    /// </para>
    /// </remarks>
    public Task<TurnOutcome> TakeTurnAsync(
        string text, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        TakeTurnAsync(text, timeout, static () => Task.CompletedTask, cancellationToken);

    /// <summary>
    /// Gives the agent a turn, saying when it actually begins, and waits for it to finish.
    /// </summary>
    /// <param name="text">What to say to it.</param>
    /// <param name="timeout">How long to wait before giving up on this turn.</param>
    /// <param name="began">Run once the turn holds the agent, before anything is said to it.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>What the turn cost and how it ended.</returns>
    /// <remarks>
    /// <b>A turn begins when it holds the agent, not when it is asked for.</b> A second wake while a turn runs queues
    /// behind it, and a start reported at the asking put the turn's end — start plus <see cref="TurnOutcome.Elapsed"/>,
    /// which is measured from here — before the work it picked up, so a turn stopped on a refused permission would never
    /// be flagged or shown stopped.
    /// </remarks>
    public async Task<TurnOutcome> TakeTurnAsync(
        string text, TimeSpan timeout, Func<Task> began, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(began);

        await _turnLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        var started = _clock.GetTimestamp();

        try
        {
            await began().ConfigureAwait(false);

            if (!IsAlive)
            {
                return Died(started);
            }

            await DiscardAbandonedResultsAsync(cancellationToken).ConfigureAwait(false);

            var message = new JsonObject
            {
                ["type"] = "user",
                ["message"] = new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "text", ["text"] = text })
                }
            };

            try
            {
                await _process.StandardInput
                    .WriteLineAsync(message.ToJsonString().AsMemory(), cancellationToken)
                    .ConfigureAwait(false);

                await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return Died(started);
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);

            try
            {
                while (await _events.Reader.WaitToReadAsync(deadline.Token).ConfigureAwait(false))
                {
                    while (_events.Reader.TryRead(out var evt))
                    {
                        Observe(evt);

                        if (TypeOf(evt) == "result")
                        {
                            return Completed(evt, started);
                        }
                    }
                }

                // The channel completed, which only happens when the pump reached end of stream.
                return Died(started);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Ours, not the caller's: this turn ran past its timeout. The agent is left alive
                // deliberately -- see the remarks -- and its eventual result is owed to nobody.
                Interlocked.Increment(ref _abandonedResults);

                return new TurnOutcome(
                    Completed: false, IsError: false, StopReason: null,
                    _clock.GetElapsedTime(started), CostUsd: 0, ModelTurns: 0,
                    PermissionDenials: 0, TimedOut: true, ProcessDied: false);
            }
            catch (OperationCanceledException)
            {
                // The caller's, after the prompt was written: a node stopping while the process lives on, as a project
                // restarted under `serve` does. The agent still answers, and that answer is owed to nobody either.
                // Uncounted, it was read as the next turn's, and every turn after stayed one reply behind (lens 1 run 101).
                Interlocked.Increment(ref _abandonedResults);

                throw;
            }
        }
        finally
        {
            _turnLock.Release();
        }
    }

    /// <summary>
    /// Tells the agent to quit, and waits for it to.
    /// </summary>
    /// <param name="grace">How long to allow before killing it.</param>
    /// <param name="cancellationToken">Cancels the wait, after which the agent is killed.</param>
    /// <returns><see langword="true"/> if it exited on its own.</returns>
    /// <remarks>
    /// Closing standard input is the polite form and the runner honours it: end of input is end
    /// of conversation, and the process exits zero. The kill is the fallback for an agent wedged
    /// mid-turn, which cannot be ruled out — a turn holds no lock the shutdown can wait on,
    /// because waiting for one would let a stuck agent block the whole fleet from stopping.
    /// </remarks>
    public async Task<bool> ShutdownAsync(
        TimeSpan grace, CancellationToken cancellationToken = default)
    {
        _stopping = true;

        if (!IsAlive)
        {
            return true;
        }

        try
        {
            _process.StandardInput.Close();
        }
        catch (Exception ex)
            when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Already gone, or still being written to. The wait below settles it either way.
            //
            // **InvalidOperationException is the one this method's own design makes ordinary.** A
            // StreamWriter refuses Close() while an asynchronous write is still in flight, saying "the
            // stream is currently in use by a previous operation" — and the remark above deliberately
            // declines to synchronise a shutdown against a turn, because waiting on a turn's lock would
            // let one wedged agent stop the whole fleet from stopping. Dispatching a turn and stopping
            // the agent are therefore allowed to collide, and this is what the collision throws.
            //
            // Swallowing it is right rather than merely convenient: closing standard input is only the
            // polite form of stopping, and everything that actually guarantees the process is gone —
            // the bounded wait, then Kill — is below and runs regardless.
            //
            // Left uncaught it would escape ShutdownAsync into whoever is stopping the fleet, so one
            // agent being mid-turn could abandon the stop of every agent after it.
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(grace);

        try
        {
            await _process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);

            return true;
        }
        catch (OperationCanceledException)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                // It exited between the timeout and the kill.
            }

            return false;
        }
    }

    /// <summary>
    /// Waits until no turn is in progress, then tells the agent to quit while still holding the turn, so none can begin
    /// between the two (SPEC.md §11.1's third way a resident ends, the refresh).
    /// </summary>
    /// <param name="grace">How long to allow it to exit once told, before killing it.</param>
    /// <param name="cancellationToken">Cancels the wait for the turn, which leaves the agent running.</param>
    /// <returns><see langword="true"/> if it exited on its own.</returns>
    /// <remarks>
    /// <para>
    /// <b>The opposite choice to <see cref="ShutdownAsync"/>, and for the opposite reason.</b> That method refuses to
    /// wait on a turn, because stopping a fleet must not be held up by one wedged agent. A refresh is not a fleet stopping:
    /// it is one agent being restarted so it reads its launch configuration afresh, and doing that mid-turn would discard
    /// the work in flight for the sake of one sentence. So this waits, for as long as the turn takes — an agent that is
    /// never idle is never refreshed, and its line stays pending, which is the documented cost.
    /// </para>
    /// <para>
    /// <b>The turn is held across the stop.</b> Waiting for the lock and then releasing it before stopping would leave a
    /// window in which a queued turn starts and is then killed. A turn queued behind this finds the process gone and
    /// reports it, as any turn given to a departed resident does; the mail it was for waits for the next wake.
    /// </para>
    /// </remarks>
    public async Task<bool> ShutdownWhenIdleAsync(TimeSpan grace, CancellationToken cancellationToken = default)
    {
        await _turnLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await ShutdownAsync(grace, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _turnLock.Release();
        }
    }

    /// <summary>Stops the agent and releases the process.</summary>
    /// <returns>A task that completes when it has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        // Awaited rather than abandoned, so the pipes are drained and the reader tasks are not
        // left running against a disposed process.
        await Task.WhenAll(Quietly(_outputPump), Quietly(_errorPump)).ConfigureAwait(false);

        _turnLock.Dispose();
        _process.Dispose();
    }

    /// <summary>Reads standard output for the process's whole life, publishing each event.</summary>
    /// <returns>A task that completes at end of stream.</returns>
    private async Task PumpOutputAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0 || line[0] != '{')
                {
                    continue;
                }

                JsonNode? node;

                try
                {
                    node = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    // A runner is free to write things that are not protocol events. Skipping is
                    // right: refusing would let one stray line end an otherwise healthy agent.
                    continue;
                }

                if (node is not null)
                {
                    await _events.Writer.WriteAsync(node).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        finally
        {
            _events.Writer.TryComplete();
        }
    }

    /// <summary>Reads standard error into the bounded diagnostics tail.</summary>
    /// <returns>A task that completes at end of stream.</returns>
    private async Task PumpErrorAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_diagnosticsLock)
                {
                    if (_diagnostics.Length < DiagnosticsLimit)
                    {
                        _diagnostics.AppendLine(line);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Discards results owed to turns that already gave up waiting.</summary>
    /// <param name="cancellationToken">Cancels the drain.</param>
    /// <returns>A task that completes when the stream is back in step.</returns>
    /// <remarks>
    /// Bounded by a short wait rather than run to completion. An abandoned result that never
    /// arrives — because the agent wedged rather than merely ran long — must not stop the next
    /// turn from being attempted; the worst case is one turn reading a stale reply, which the
    /// outcome will show as a result that answers nothing.
    /// </remarks>
    private async Task DiscardAbandonedResultsAsync(CancellationToken cancellationToken)
    {
        while (Volatile.Read(ref _abandonedResults) > 0)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                var evt = await _events.Reader.ReadAsync(budget.Token).ConfigureAwait(false);

                Observe(evt);

                if (TypeOf(evt) == "result")
                {
                    Interlocked.Decrement(ref _abandonedResults);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller's, not the budget's: the replies are still owed to nobody, so the count stands for the next
                // turn to skip them.
                throw;
            }
            catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
            {
                // Nothing more is coming in the time allowed. Give up on the bookkeeping rather
                // than on the turn.
                Volatile.Write(ref _abandonedResults, 0);

                return;
            }
        }
    }

    /// <summary>Notes anything from an event that is worth keeping.</summary>
    /// <param name="evt">The event.</param>
    private void Observe(JsonNode evt)
    {
        if (TypeOf(evt) != "system")
        {
            return;
        }

        // Matched on the subtype rather than the type. A runner emits several kinds of `system`
        // event -- hook notifications among them -- and only `init` carries the session.
        if (evt["subtype"]?.GetValue<string>() is "init" &&
            evt["session_id"]?.GetValue<string>() is { Length: > 0 } id)
        {
            _sessionId = id;
        }
    }

    private static string? TypeOf(JsonNode evt)
    {
        try
        {
            return evt["type"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Builds the outcome from a result event.</summary>
    /// <param name="evt">The result.</param>
    /// <param name="started">When the turn began.</param>
    /// <returns>What the turn cost.</returns>
    private TurnOutcome Completed(JsonNode evt, long started) =>
        new(Completed: true,
            IsError: Bool(evt, "is_error"),
            StopReason: String(evt, "stop_reason") ?? String(evt, "subtype"),
            _clock.GetElapsedTime(started),
            Number(evt, "total_cost_usd"),
            (int)Number(evt, "num_turns"),
            evt["permission_denials"] is JsonArray denials ? denials.Count : 0,
            TimedOut: false,
            ProcessDied: false,
            String(evt, "result"));

    private TurnOutcome Died(long started) =>
        new(Completed: false, IsError: true,
            StopReason: _stopping ? "shutting down" : "process died",
            _clock.GetElapsedTime(started), CostUsd: 0, ModelTurns: 0,
            PermissionDenials: 0, TimedOut: false, ProcessDied: true);

    private static bool Bool(JsonNode evt, string name)
    {
        try
        {
            return evt[name]?.GetValue<bool>() ?? false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static string? String(JsonNode evt, string name)
    {
        try
        {
            return evt[name]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static double Number(JsonNode evt, string name)
    {
        try
        {
            return evt[name]?.GetValue<double>() ?? 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return 0;
        }
    }

    /// <summary>Awaits a pump, discarding any failure.</summary>
    /// <param name="pump">The pump task.</param>
    /// <returns>A task that completes when the pump does, however it does.</returns>
    private static async Task Quietly(Task pump)
    {
        try
        {
            await pump.ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception)
#pragma warning restore CA1031
        {
            // The process is going away and the pump's failure is a consequence of that, not a
            // cause worth reporting over the shutdown that provoked it.
        }
    }

    /// <summary>Renders the agent for a log line.</summary>
    /// <returns>Its name and where it runs.</returns>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Name.Value} in {Workspace}");
}
