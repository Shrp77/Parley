namespace Parley.Broker;

/// <summary>
/// What the reboot sequence did.
/// </summary>
/// <param name="AgentsMarkedOffline">
/// Agents whose liveness was cleared. Retired agents are not among them.
/// </param>
/// <param name="ExchangesOrphaned">
/// Open exchanges returned to the orphaned state, releasing their wait-for edges.
/// </param>
/// <remarks>
/// Reported rather than logged, for the same reason <see cref="Store.ReapResult"/> is: a caller
/// decides what is worth saying. A restart that orphans nothing is an ordinary quiet
/// deployment; one that orphans dozens says the previous run was interrupted mid-conversation,
/// which an operator may want to know before wondering why every agent is suddenly idle.
/// </remarks>
public sealed record StartResult(int AgentsMarkedOffline, int ExchangesOrphaned);
