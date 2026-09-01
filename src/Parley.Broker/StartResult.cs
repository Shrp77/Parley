namespace Parley.Broker;

/// <summary>
/// What the attach sequence did.
/// </summary>
/// <param name="AgentMarkedLive">
/// One if the attaching agent was marked live, zero if it is retired and stayed that way.
/// </param>
/// <param name="ExchangesOrphaned">
/// Open exchanges returned to the orphaned state, releasing their wait-for edges.
/// </param>
/// <remarks>
/// Reported rather than logged, for the same reason <see cref="Core.ReapResult"/> is: a caller
/// decides what is worth saying. An attach that orphans nothing is an ordinary quiet start; one
/// that orphans several says this agent's previous session was interrupted mid-conversation,
/// which is worth knowing before wondering why it is suddenly being re-asked things.
/// <para>
/// <see cref="AgentMarkedLive"/> is a count rather than a flag because it comes straight from
/// the rows affected, and a zero is meaningful: it says the registry refused to mark this agent
/// live, which happens exactly when an operator has retired it. An adapter that reports a clean
/// start on a retired agent would leave somebody wondering why every tool then refuses.
/// </para>
/// </remarks>
public sealed record StartResult(int AgentMarkedLive, int ExchangesOrphaned);
