namespace Parley.Broker;

/// <summary>
/// What attaching an agent did (§14).
/// </summary>
/// <param name="AgentMarkedLive">Rows marked live: one, or zero when the agent is retired.</param>
/// <param name="ExchangesRedelivered">
/// Exchanges whose work the agent had collected in a session that ended before its Result, now handed
/// over again on its next collection (§6). Nothing about those exchanges changed; they were open throughout.
/// </param>
public sealed record StartResult(int AgentMarkedLive, int ExchangesRedelivered);
