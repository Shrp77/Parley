# Quickstart: trying agent history

1. Install a build with this feature (`./install.ps1 -Force -Hidden`). Every `parley-mcp` is replaced, so every agent has the two tools
   after its next restart.
2. In an agent's session, with no memory of an exchange, ask it to "continue from exchange N". It calls `parley_read_exchange`
   with `exchange_id: N` and reads the exchange, each message in its own envelope marked as history.
3. Ask it to "find where we talked about <phrase>". It calls `parley_search_history`, gets snippets with exchange numbers, and reads
   the one it needs.
4. In the web console, open the conversation. Under the exchange the agent read, a line says which agent read it, when and how often. Beside
   the conversation a line for each agent that searched says how many searches it made and its newest words.
5. Check nothing moved: the agents' inboxes, debts and turns are as they were.

Tests: `pwsh scripts/test.ps1 -Category Smoke -Build`. The new classes are `AgentHistoryBrokerTests` and `AgentHistoryBoundsTests` (Broker), `ReadingHistoryTests` and
`AgentsNeverSeeWhoReadTests` (Server), `HistoryToolTests` (Mcp), `HistoryReadsSchemaTests` (Store), `HistoryEnvelopeTests` (Core),
`WhoReadAnExchangeTests` (Client), `HistoryReadsAreShownTests` (Web) and `HistoryReadsAgreeTests` (Spec).
