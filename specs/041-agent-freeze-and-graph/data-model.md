# Data model: feature 041

- **`agents.frozen_utc`** (migration 011): `TEXT NULL`. ISO-8601 UTC when a person froze the agent in this project; NULL when it
  is not frozen. Set by Freeze, cleared by Unfreeze, kept across restart, backup and restore. A purge removes it with the row;
  retire leaves it (a reinstated agent is still frozen until unfrozen).
- **`AgentRecord.FrozenUtc`** (`DateTimeOffset?`): the column, read by every roster read. Frozen is `FrozenUtc is not null`.
- **`FreezeOutcome`**: `Frozen` (newly frozen), `AlreadyHeld`, with `Process`: `Stopping` (node asked), `NotRunning`, `OpenOutside`,
  `NoNode`.
- **Graph**: `GraphNode(Kind: Project | Agent | Conversation | Background, Id, Label, State, Count, X, Y)`,
  `GraphEdge(From, To, Kind: In | SpunOffFrom | Running)`, `AgentGraph(Projects, Nodes, Edges, Unreachable, NotDrawn)`.
- **State words** for an agent: `frozen`, `stopped`, `working`, `waiting on background work`, `idle`, `away`, `unknown`.
