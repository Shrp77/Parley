using System.Collections.Concurrent;
using System.Globalization;
using Parley.Core;
using Parley.Headless;

namespace Parley.Bridge;

/// <summary>One line of an agent's stream, as the console shows it.</summary>
/// <param name="Sequence">Monotonic within one hosted agent, so a reconnecting page can resume.</param>
/// <param name="Agent">Whose stream this is.</param>
/// <param name="AtUtc">When the bridge saw it.</param>
/// <param name="Kind">What sort of line: <c>event</c>, <c>lifecycle</c>, or <c>error</c>.</param>
/// <param name="Text">The line itself.</param>
public sealed record StreamLine(
    long Sequence, string Agent, string AtUtc, string Kind, string Text);

/// <summary>
/// Keeps agents this bridge started, and makes their streams watchable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only agents started here can be watched.</b> A process's standard output is readable by
/// whoever spawned it and by nobody else, so an agent a person launched in their own terminal is
/// visible through the store and no further: the board can say it is live, what it owns, and what
/// it has not collected, but not what it is doing. That is not a gap this can close by trying
/// harder; it is what the operating system offers.
/// </para>
/// <para>
/// Which makes the division honest rather than awkward. Hosting is opt-in, per agent, and the
/// deployment works untouched without it -- the bridge is a privileged client, not a dependency.
/// </para>
/// <para>
/// <b>This does not make the bridge an agent.</b> It starts processes and relays keystrokes; it
/// never sends a Parley message. A hosted agent talks to the broker through its own server, under
/// the identity in its own launch environment, exactly as it would if a person had opened the
/// session. Anything else would make the operator a forger of agent identity and every provenance
/// claim in the protocol unfalsifiable.
/// </para>
/// </remarks>
public sealed class AgentHost : IAsyncDisposable
{
    /// <summary>
    /// How many lines of each agent's stream are kept.
    /// </summary>
    /// <remarks>
    /// A window rather than a transcript. The session's own transcript is the durable record and
    /// this is a monitor: keeping everything would grow without bound for as long as the bridge
    /// runs, which for a fleet supervisor is measured in days.
    /// </remarks>
    public const int Window = 500;

    /// <summary>
    /// How long a stopping agent is given to quit before it is killed.
    /// </summary>
    /// <remarks>
    /// An agent asked to stop may be mid-turn. Long enough to finish writing whatever it was
    /// saying, short enough that an operator who pressed Stop does not wonder whether it worked.
    /// </remarks>
    public static TimeSpan Grace { get; } = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, Hosted> _hosted = new(StringComparer.Ordinal);
    private long _sequence;

    /// <summary>Raised for every line any hosted agent produces.</summary>
    public event Action<StreamLine>? LineWritten;

    /// <summary>The agents this bridge is currently running.</summary>
    public IReadOnlySet<string> Names => _hosted.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Starts an agent and begins watching its stream.
    /// </summary>
    /// <param name="name">The registry name.</param>
    /// <param name="workspace">Where it works.</param>
    /// <param name="template">The launch command recorded for it.</param>
    /// <returns>The reason it could not start, or <see langword="null"/> on success.</returns>
    /// <remarks>
    /// The command comes from the registry's <c>invoke_command</c>, which is where an agent
    /// records its consent to being started at all. An operator pressing the button is consent of
    /// a different kind and outranks <c>invoke_when_idle</c> -- a person at the console is not the
    /// unattended automation that setting governs -- but the command itself still has to have
    /// been configured. There is nothing safe to invent here.
    /// </remarks>
    public string? Start(string name, string? workspace, string? template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (_hosted.ContainsKey(name))
        {
            return $"{name} is already running here.";
        }

        if (string.IsNullOrWhiteSpace(template))
        {
            return $"{name} has no invoke_command recorded, so there is nothing to run. " +
                   "Set PARLEY_INVOKE_COMMAND in its .mcp.json and let it register again.";
        }

        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            return $"{name} works in '{workspace}', which is not on this disk.";
        }

        if (!AgentName.TryCreate(name, out var parsed))
        {
            return $"'{name}' is not an agent name.";
        }

        try
        {
            var command = InvocationCommand.Parse(template, name);
            var agent = ResidentAgent.Start(parsed, command, workspace, TimeProvider.System);
            var entry = new Hosted(agent, command);

            agent.Observer = node => Publish(name, "event", node.ToJsonString());

            if (!_hosted.TryAdd(name, entry))
            {
                // Lost a race with another operator pressing the same button. Shut the loser
                // down rather than leaving an orphan process nothing is tracking.
                _ = agent.ShutdownAsync(Grace);
                return $"{name} is already running here.";
            }

            Publish(name, "lifecycle", $"started: {command.FileName}");
            return null;
        }
        catch (Exception ex) when (ex is InvocationFailedException or ArgumentException
                                      or DirectoryNotFoundException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Gives a hosted agent a turn.
    /// </summary>
    /// <param name="name">The agent.</param>
    /// <param name="text">What to say to it.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The reason it could not, or <see langword="null"/> on success.</returns>
    /// <remarks>
    /// <para>
    /// This is the most dangerous capability in the project and it is worth naming as such: it is
    /// arbitrary instruction of a process that holds tool permissions in a real repository. It is
    /// also the point of the feature. The controls are in front of the endpoint, not here --
    /// loopback by default, a per-run token in a header, and an origin check -- because by this
    /// point the decision to trust the caller has already been made.
    /// </para>
    /// <para>
    /// One turn at a time is enforced by <see cref="ResidentAgent"/> itself; a second while one is
    /// in flight waits rather than interleaving on the stream.
    /// </para>
    /// </remarks>
    public async Task<string?> SendAsync(
        string name, string text, CancellationToken cancellationToken = default)
    {
        if (!_hosted.TryGetValue(name, out var entry))
        {
            return $"{name} is not running here.";
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return "Nothing to send.";
        }

        Publish(name, "lifecycle", $"operator: {text}");

        try
        {
            var outcome = await entry.Agent
                .TakeTurnAsync(text, entry.Command.Timeout, cancellationToken)
                .ConfigureAwait(false);

            Publish(name, "lifecycle", outcome.Describe());
            return null;
        }
        catch (Exception ex) when (ex is InvocationFailedException or InvalidOperationException)
        {
            Publish(name, "error", ex.Message);
            return ex.Message;
        }
    }

    /// <summary>
    /// Stops a hosted agent.
    /// </summary>
    /// <param name="name">The agent.</param>
    /// <returns>The reason it could not, or <see langword="null"/> on success.</returns>
    public async Task<string?> StopAsync(string name)
    {
        if (!_hosted.TryRemove(name, out var entry))
        {
            return $"{name} is not running here.";
        }

        await entry.Agent.ShutdownAsync(Grace).ConfigureAwait(false);
        Publish(name, "lifecycle", "stopped");
        return null;
    }

    /// <summary>
    /// The recent stream of one hosted agent, oldest first.
    /// </summary>
    /// <param name="name">The agent.</param>
    /// <returns>What has been seen, or empty if it is not hosted here.</returns>
    public IReadOnlyList<StreamLine> Transcript(string name) =>
        _hosted.TryGetValue(name, out var entry) ? entry.Recent() : [];

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var name in _hosted.Keys)
        {
            if (_hosted.TryRemove(name, out var entry))
            {
                await entry.Agent.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void Publish(string agent, string kind, string text)
    {
        var line = new StreamLine(
            Interlocked.Increment(ref _sequence),
            agent,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            kind,
            text);

        if (_hosted.TryGetValue(agent, out var entry))
        {
            entry.Remember(line);
        }

        LineWritten?.Invoke(line);
    }

    private sealed class Hosted(ResidentAgent agent, InvocationCommand command)
    {
        private readonly Queue<StreamLine> _recent = new();

        public ResidentAgent Agent { get; } = agent;

        public InvocationCommand Command { get; } = command;

        public void Remember(StreamLine line)
        {
            lock (_recent)
            {
                _recent.Enqueue(line);

                while (_recent.Count > Window)
                {
                    _recent.Dequeue();
                }
            }
        }

        public IReadOnlyList<StreamLine> Recent()
        {
            lock (_recent)
            {
                return [.. _recent];
            }
        }
    }
}
