# Data model: feature 041

- **`agents.held_utc`** (migration 011): `TEXT NULL`. ISO-8601 UTC when a person held the agent in this project; NULL when it
  is not held. Set by Hold, cleared by Resume, kept across restart, backup and restore. A purge removes it with the row;
  retire leaves it (a reinstated agent is still held until resumed).
- **`AgentRecord.HeldUtc`** (`DateTimeOffset?`): the column, read by every roster read. Held is `HeldUtc is not null`.
- **`HoldOutcome`**: `Held` (newly held), `AlreadyHeld`, with `Process`: `Stopping` (node asked), `NotRunning`, `OpenOutside`,
  `NoNode`.
- **Graph**: `GraphNode(Kind: Project | Agent | Conversation | Background, Id, Label, State, Count, X, Y)`,
  `GraphEdge(From, To, Kind: In | SpunOffFrom | Running)`, `AgentGraph(Projects, Nodes, Edges, Unreachable, NotDrawn)`.
- **State words** for an agent: `held`, `stopped`, `working`, `waiting on background work`, `idle`, `away`, `unknown`.
