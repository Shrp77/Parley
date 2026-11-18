# Quickstart: trying agent history

1. Install a build with this feature (`./install.ps1 -Force -Hidden`). Every `parley-mcp` is replaced, so every agent has the two tools
   after its next restart.
2. In an agent's session, with no memory of an exchange, ask it to "continue from exchange N". It calls `parley_read_exchange`
   with `exchange_id: N` and reads the exchange, each message in its own envelope marked as history.
3. Ask it to "find where we talked about <phrase>". It calls `parley_search_history`, gets snippets with exchange numbers, and reads
   the one it needs.
4. In the web console, open the conversation. Under the exchange the agent read, a line says which agent read it and when. The
   agent's page lists the searches it made with the words and how many matches.
5. Check nothing moved: the agents' inboxes, debts and turns are as they were.

Tests: `pwsh scripts/test.ps1 -Category Smoke -Build`. The new classes are `AgentHistoryTests` (Store and Broker),
`ReadingHistoryChangesNothingTests` (Server, SC-003), `HistoryEnvelopeTests` (Core, SC-004), `HistoryIsOneOperationTests` (Spec).
