# Contracts: feature 041

## Operators service (proto/parley.proto)

- `rpc Hold(AgentRequest) returns (HoldReply)`: records the hold, then asks the agent's node to stop it after its turn.
  `HoldReply { bool changed = 1; string process = 2; }` where `changed` is false when it was already held and `process` is one
  of `stopping`, `not_running`, `open_outside`, `no_node`. Refuses the facilitator, the person seat, and an unknown agent.
- `rpc Resume(AgentRequest) returns (DidItReply)`: clears it; `changed` false when it was not held. Then, as after a restart,
  the agent is asked to start if it owes work.
- `Start` on a held agent is `FailedPrecondition`: "held; resume it first".
- `ListAgentsReply`'s agent row gains `held_utc` (string, empty when not held).
- `Agents` service: **no** Hold or Resume (`AgentsCannotSetTheirOwnLineTests` matches by word).

## IParleyBrokerClient (SPEC 10.2)

`Task<HoldResult> HoldAsync(AgentName agent, CancellationToken)`, `Task<bool> ResumeAsync(AgentName agent, CancellationToken)`.

## Surfaces

- Web: `Projects/Act` arms `hold`, `resume` (one agent, every project it serves) and `holdall`, `resumeall` (a project's agents);
  each described on GET and done on POST. The graph and the agent page link them.
- Web: `GET /Graph` returns the panel's fragment. Collapsed, nothing calls it.
- Terminal: agent row menu entries *Hold* and *Resume*; CLI: `parley hold <agent>` and `parley resume <agent>` exit 64 naming the
  terminal, like every operator verb.
