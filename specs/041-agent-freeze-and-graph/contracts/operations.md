# Contracts: feature 041

## Operators service (proto/parley.proto)

- `rpc Freeze(FreezeRequest) returns (FreezeReply)`: records the freeze, then asks the agent's node to stop it after its turn.
  `FreezeRequest { string agent = 1; bool keep_frozen = 2; }`: without `keep_frozen` the broker's next start lifts the freeze.
  `FreezeReply { bool changed = 1; string process = 2; }` where `changed` is false when it was already frozen and `process` is one
  of `stopping`, `not_running`, `open_outside`, `no_node`. Refuses the facilitator, the person seat, and an unknown agent.
- `rpc Unfreeze(AgentRequest) returns (DidItReply)`: clears it; `changed` false when it was not frozen. Then the agent is asked to start if it owes work, as after a restart, or if a
  message or a closure poll is waiting for it.
- `Restart` of a frozen agent is `FailedPrecondition`, as `Start` is.
- `Start` on a frozen agent is `FailedPrecondition`: "frozen; unfreeze it first".
- `ListAgentsReply`'s agent row gains `frozen_utc` (string, empty when not frozen).
- `Agents` service: **no** Freeze or Unfreeze (`AgentsCannotSetTheirOwnLineTests` matches by word).

## IParleyBrokerClient (SPEC 10.2)

`Task<FreezeResult> FreezeAsync(AgentName agent, CancellationToken)`, `Task<bool> UnfreezeAsync(AgentName agent, CancellationToken)`.

## Surfaces

- Web: `Projects/Act` arms `freeze`, `unfreeze` (one agent, every project it serves) and `freezeall`, `unfreezeall` (a project's agents);
  each described on GET and done on POST. The graph and the agent page link them.
- Web: `GET /Graph` returns the panel's fragment. Collapsed, nothing calls it.
- Terminal: agent row menu entries *Freeze* and *Unfreeze*; CLI: `parley freeze <agent>` and `parley unfreeze <agent>` exit 64 naming the
  terminal, like every operator verb.
