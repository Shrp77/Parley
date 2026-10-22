namespace Parley.Core;

/// <summary>
/// A directed graph of which participants are waiting on which others, used to record a Request
/// that closes a cycle (SPEC.md §7.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>A cycle is admitted, not refused.</b> Refusing any question that would close one assumes a cycle is
/// a set of questions none of which can be answered. That holds only when each answer depends on its own
/// outcome, and nothing at admission can tell that
/// case from the ordinary one — <c>orders</c> asks <c>billing</c> for work, <c>billing</c> asks
/// <c>inventory</c>, and <c>inventory</c> asks <c>orders</c> which field the price lives in. Refusing
/// every cycle refused the communication that resolves blockers. The broker records the cycle on the
/// conversation instead, where the person running the deployment sees it.
/// </para>
/// <para>
/// Each open exchange contributes one edge, from the participant waiting to the participant named in
/// <see cref="Exchange.OwedBy"/>. The direction is easy to state backwards, and edges the other way
/// produce a graph in which no cycle is ever found and nothing reports the omission.
/// </para>
/// <para>
/// A transient snapshot, built from the open set inside the admitting transaction and discarded.
/// </para>
/// </remarks>
public sealed class WaitForGraph
{
    private readonly Dictionary<AgentName, List<AgentName>> _edges = [];

    /// <summary>Records that one participant is waiting on another.</summary>
    /// <param name="waiting">The participant that is waiting.</param>
    /// <param name="owedBy">The participant that owes the next move.</param>
    /// <remarks>Duplicate edges are ignored.</remarks>
    public void AddWait(AgentName waiting, AgentName owedBy)
    {
        if (!_edges.TryGetValue(waiting, out var owing))
        {
            owing = [];
            _edges[waiting] = owing;
        }

        if (!owing.Contains(owedBy))
        {
            owing.Add(owedBy);
        }
    }

    /// <summary>Builds a graph from a set of exchanges, using only those that are open.</summary>
    /// <param name="exchanges">The exchanges to derive edges from.</param>
    /// <returns>A snapshot of the current waits.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exchanges"/> is null.</exception>
    public static WaitForGraph FromOpenExchanges(IEnumerable<Exchange> exchanges)
    {
        ArgumentNullException.ThrowIfNull(exchanges);

        var graph = new WaitForGraph();

        foreach (var exchange in exchanges)
        {
            if (exchange.State == ExchangeState.Open)
            {
                graph.AddWait(exchange.Waiting, exchange.OwedBy);
            }
        }

        return graph;
    }

    /// <summary>Determines whether a prospective Request would close a cycle.</summary>
    /// <param name="requester">The participant that would start waiting.</param>
    /// <param name="receiver">The participant that would owe the next move.</param>
    /// <param name="cyclePath">
    /// When this returns <see langword="true"/>, the participants in the order each waits on the next,
    /// beginning at <paramref name="requester"/>, each named once. Empty otherwise.
    /// </param>
    /// <returns><see langword="true"/> if the Request would close a cycle.</returns>
    /// <remarks>
    /// The prospective edge is <paramref name="requester"/> waiting on <paramref name="receiver"/>. It
    /// closes a cycle exactly when the requester is already reachable from the receiver.
    /// </remarks>
    public bool ClosesCycle(AgentName requester, AgentName receiver, out IReadOnlyList<AgentName> cyclePath)
    {
        var path = new List<AgentName>();

        if (DepthFirst(receiver, requester, [], path))
        {
            // path runs receiver -> ... -> requester; the requester leads, and is named once.
            var full = new List<AgentName>(path.Count) { requester };
            full.AddRange(path.Take(path.Count - 1));
            cyclePath = full;
            return true;
        }

        cyclePath = [];
        return false;
    }

    private bool DepthFirst(AgentName current, AgentName target, HashSet<AgentName> visited, List<AgentName> path)
    {
        if (!visited.Add(current))
        {
            return false;
        }

        path.Add(current);

        if (current == target)
        {
            return true;
        }

        if (_edges.TryGetValue(current, out var owing))
        {
            foreach (var next in owing)
            {
                if (DepthFirst(next, target, visited, path))
                {
                    return true;
                }
            }
        }

        path.RemoveAt(path.Count - 1);
        return false;
    }
}
