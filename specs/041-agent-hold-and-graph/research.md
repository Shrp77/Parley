# Research: feature 041, the graph and the hold

## Decisions

- **One place decides a start: `BrokerRuntime.Ask`.** Every wake funnels through it: a message arriving, the restart wake
  (`WakeForOutstandingWorkAsync` through `RequestAttentionForMail`), a person's Start (`RequestAttention`). A held agent is
  refused there with a new `WakeOutcome.Held`, before any node is asked. No second check anywhere. Alternative: the resume gate
  (`IResumeGate`); rejected, it is asked per exchange only under the resume policy and a Start does not pass through it.
- **The hold is a column, not a table: `agents.held_utc TEXT`, migration 011.** A table referencing `agents` would have to be
  taught to purge (`PurgeToll`); a nullable column on the row goes with the row. Migration 011 is free: feature 027 is not
  committed, so its change moves to 012 (CLAUDE.md names 011 for it and is updated). Alternative: `held_by` and a reason;
  rejected, only the person holds, and the reason is the person's own business.
- **Hold ends the process by the existing stop.** `RequestStop` already waits for the turn and ends only a process a node
  started. Hold records first, then asks the node to stop, so a turn that finishes and a message that arrives in between find
  the hold. An agent open outside Parley, or with no node, is still held (nothing will wake it) and the reply says its
  process was not ended.
- **Across projects the surface does the looping, not the broker.** A broker knows one project. An agent is the same
  `role@machine` in each project it serves, so `AgentHolding` (Parley.Client, beside `BrokerDirectory`) lists the projects whose
  registry holds the name and calls each broker's Hold. Web, terminal and CLI share it, as `AgentWithdrawal` is shared.
  Alternative: a machine-wide table; rejected, it breaks "a store has one broker".
- **The graph is derived in Parley.Client, drawn in Razor.** `AgentGraph.Build` takes what the brokers returned (registry,
  conversations) and returns nodes and edges with a deterministic layout (columns by kind, rows by project), so the derivation is
  unit-testable and the page only draws. The panel body is a fragment fetched when the tab is opened. Alternative: a script
  charting library; rejected, no new dependency and a graph this size is a table of boxes with lines.
- **The pin is `localStorage`**, per the console's convention for unsent drafts; a broker never learns of it.
- **The held state reads before the turn state.** `AgentStanding` precedence: held, then stopped, working, waiting on
  background work, idle, as `ConversationsView`/wording already order the rest.
- **
  says "held by the person".
