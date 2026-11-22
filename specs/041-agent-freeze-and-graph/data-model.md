# Data model: feature 041

- **`agents.frozen_utc`** (migration 011): `TEXT NULL`. ISO-8601 UTC when a person froze the agent in this project; NULL when it
  is not frozen. Set by Freeze; cleared by Unfreeze and, unless `keep_frozen`, by the broker's next start. A purge removes it with the row, a
  rename carries it to the new name, and retire leaves it (a reinstated agent is still frozen until unfrozen).
- **`agents.frozen_run`**: the number the broker gave itself at the start under which the freeze was made. A start under another
  number lifts the freeze unless `keep_frozen`.
- **`agents.keep_frozen`**: the person's choice that this freeze outlasts a start of the broker; cleared with the freeze.
- **`AgentRecord.FrozenUtc`** (`DateTimeOffset?`): the column, read by every roster read. Frozen is `FrozenUtc is not null`.
- **`FreezeOutcome`**: `Frozen` (newly frozen), `AlreadyHeld`, with `Process`: `Stopping` (node asked), `NotRunning`, `OpenOutside`,
  `NoNode`.
- **Graph**: `GraphNode(Kind: Project | Agent | Conversation | Background, Id, Label, State, Count, X, Y)`,
  `GraphEdge(From, To, Kind: In | SpunOffFrom | Running)`, `AgentGraph(Projects, Nodes, Edges, Unreachable, NotDrawn)`.
- **State words** for an agent: `frozen`, `stopped`, `working`, `waiting on background work`, `idle`, `away`, `unknown`.
