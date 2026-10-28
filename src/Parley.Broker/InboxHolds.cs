using Parley.Core;

namespace Parley.Broker;

/// <summary>What one collection handed over and the agent has not yet acknowledged (SPEC.md §6).</summary>
/// <remarks>
/// A class rather than a record: two collections that happened to hold the same things are still two holds, and
/// releasing one must not release the other.
/// </remarks>
public sealed class InboxHold
{
    internal InboxHold(
        AgentName agent,
        IReadOnlyList<long> messages,
        IReadOnlyList<long> requests,
        IReadOnlyList<string> fileNotices,
        IReadOnlyList<string> roadmapNotices)
    {
        Agent = agent;
        Messages = messages;
        Requests = requests;
        FileNotices = fileNotices;
        RoadmapNotices = roadmapNotices;
    }

    /// <summary>Gets the agent that collected.</summary>
    public AgentName Agent { get; }

    /// <summary>Gets the messages handed over.</summary>
    public IReadOnlyList<long> Messages { get; }

    /// <summary>Gets the exchanges whose Requests were among them, recorded collected on acknowledgement.</summary>
    public IReadOnlyList<long> Requests { get; }

    /// <summary>Gets the keys of the gone notices handed over.</summary>
    public IReadOnlyList<string> FileNotices { get; }

    /// <summary>Gets the keys of the roadmap notices handed over.</summary>
    public IReadOnlyList<string> RoadmapNotices { get; }
}

/// <summary>Every collection in flight: handed over, not yet acknowledged (SPEC.md §6).</summary>
/// <remarks>
/// <para>
/// <b>In memory, never in the store.</b> What a hold covers is still undelivered there, so a broker that stops releases
/// every hold by stopping, and the next collection hands all of it over again: nothing is delivered that the agent did not
/// say it has.
/// </para>
/// <para>
/// A hold keeps a second collection by the same agent, from another session, from handing over what the first is still
/// carrying. It is released the moment its call ends, acknowledged or not, so nothing waits on a timer.
/// </para>
/// </remarks>
public sealed class InboxHolds
{
    private readonly Lock _gate = new();
    private readonly List<InboxHold> _held = [];

    /// <summary>Ends a hold, whether the collection was acknowledged or not.</summary>
    /// <param name="hold">The hold.</param>
    public void Release(InboxHold hold)
    {
        ArgumentNullException.ThrowIfNull(hold);

        lock (_gate)
        {
            _ = _held.RemoveAll(h => ReferenceEquals(h, hold));
        }
    }

    /// <summary>Takes a new hold.</summary>
    /// <param name="hold">The hold.</param>
    /// <remarks>Taken in the same turn of the store as the read it covers, so no collection between can miss it.</remarks>
    internal void Add(InboxHold hold)
    {
        lock (_gate)
        {
            _held.Add(hold);
        }
    }

    /// <summary>What collections in flight hold for an agent.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>The held messages, gone-notice keys and roadmap notices.</returns>
    internal (HashSet<long> Messages, HashSet<string> FileNotices, HashSet<string> RoadmapNotices) HeldFor(AgentName agent)
    {
        lock (_gate)
        {
            var mine = _held.Where(h => h.Agent == agent).ToList();

            return (
                [.. mine.SelectMany(h => h.Messages)],
                [.. mine.SelectMany(h => h.FileNotices)],
                [.. mine.SelectMany(h => h.RoadmapNotices)]);
        }
    }
}
