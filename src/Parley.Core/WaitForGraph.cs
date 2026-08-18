namespace Parley.Core;

/// <summary>
/// A directed graph of which agents are blocked waiting on which others, used to reject a
/// question that would close a cycle.
/// </summary>
/// <remarks>
/// <para>
/// Pair serialisation alone is not sufficient to prevent deadlock. Given the open exchanges
/// <c>orders → billing</c>, <c>billing → inventory</c> and <c>inventory → orders</c>, every
/// pair slot is legitimately held by exactly one exchange, no pair rule is violated, and all
/// three agents are nonetheless blocked forever. Pair locking plus cycle detection on insert
/// is complete for cycles of every length.
/// </para>
/// <para>
/// Each open exchange contributes exactly one edge: the participant waiting for a response
/// is blocked by the participant who owes it. That direction follows
/// <see cref="Exchange.OwedBy"/> rather than <see cref="Exchange.Asker"/>, because a
/// clarification reverses it — while the asker owes a clarification reply, it is the
/// answerer that is waiting.
/// </para>
/// <para>
/// This type is a transient snapshot, not a live structure. Build it from the current open
/// set inside the same transaction that performs admission, and discard it afterwards.
/// </para>
/// </remarks>
public sealed class WaitForGraph
{
    private readonly Dictionary<AgentName, List<AgentName>> _edges = [];

    /// <summary>
    /// Records that one agent is waiting on another.
    /// </summary>
    /// <param name="blocked">The agent that is waiting.</param>
    /// <param name="blocker">The agent that owes a response.</param>
    /// <remarks>
    /// Duplicate edges are ignored, so the same wait may be added more than once safely.
    /// </remarks>
    public void AddWait(AgentName blocked, AgentName blocker)
    {
        if (!_edges.TryGetValue(blocked, out var blockers))
        {
            blockers = [];
            _edges[blocked] = blockers;
        }

        if (!blockers.Contains(blocker))
        {
            blockers.Add(blocker);
        }
    }

    /// <summary>
    /// Builds a graph from a set of exchanges, using only those that are open.
    /// </summary>
    /// <param name="exchanges">
    /// The exchanges to derive edges from. Closed and orphaned entries are skipped, since
    /// neither holds a pair slot nor blocks anyone.
    /// </param>
    /// <returns>A snapshot graph of the current waits.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exchanges"/> is <see langword="null"/>.</exception>
    public static WaitForGraph FromOpenExchanges(IEnumerable<Exchange> exchanges)
    {
        ArgumentNullException.ThrowIfNull(exchanges);

        var graph = new WaitForGraph();
        foreach (var exchange in exchanges)
        {
            if (exchange.State != ExchangeState.Open)
            {
                continue;
            }

            var waiting = exchange.OwedBy == exchange.Answerer ? exchange.Asker : exchange.Answerer;
            graph.AddWait(waiting, exchange.OwedBy);
        }

        return graph;
    }

    /// <summary>
    /// Determines whether a prospective question would close a cycle and deadlock.
    /// </summary>
    /// <param name="asker">The agent that wants to ask, and would therefore start waiting.</param>
    /// <param name="answerer">The agent that would owe the answer.</param>
    /// <param name="cyclePath">
    /// When this method returns <see langword="true"/>, the agents forming the cycle,
    /// beginning at <paramref name="asker"/> and running through
    /// <paramref name="answerer"/> back around. Returned so the broker can tell the asker
    /// which knot it hit, rather than issuing a bare refusal it cannot act on. Empty when
    /// the method returns <see langword="false"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if admitting the question would create a cycle; otherwise
    /// <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// The prospective edge is <paramref name="asker"/> waiting on
    /// <paramref name="answerer"/>. It closes a cycle exactly when
    /// <paramref name="asker"/> is already reachable from <paramref name="answerer"/> by
    /// following existing waits.
    /// </remarks>
    public bool WouldDeadlock(AgentName asker, AgentName answerer, out IReadOnlyList<AgentName> cyclePath)
    {
        if (TryFindPath(answerer, asker, out var path))
        {
            var full = new List<AgentName>(path.Count + 1) { asker };
            full.AddRange(path);
            cyclePath = full;
            return true;
        }

        cyclePath = [];
        return false;
    }

    private bool TryFindPath(AgentName from, AgentName target, out List<AgentName> path)
    {
        path = [];
        var visited = new HashSet<AgentName>();
        return DepthFirst(from, target, visited, path);
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

        if (_edges.TryGetValue(current, out var blockers))
        {
            foreach (var next in blockers)
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
