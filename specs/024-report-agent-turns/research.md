# Research: Report agent turns (024)

Every finding below was read out of the code on 2026-10-03, not reasoned about.

## R1 — Where a turn happens, and what is measured

**Decision**: the node's two turn sites report — `NodeCommand.GiveTurnAsync` (a turn because there is mail) and the
instruction path (`ReportInstruction`'s caller, a turn because a person instructed). Both call
`ResidentAgent.TakeTurnAsync`, which returns a `TurnOutcome` carrying `Completed`, `IsError`, `Elapsed`, `CostUsd`,
`ModelTurns`, `PermissionDenials`, `TimedOut` and `ProcessDied`.

**Rationale**: those are the only two places on the node path that run a turn. Today `GiveTurnAsync` writes a failed
outcome to the node's own log and nothing else; the success path says nothing at all.

**Alternatives considered**: reporting from inside `ResidentAgent` — rejected, because it lives in `Parley.Headless`,
which `parley fleet` also uses with a store of its own (`AgentFleet` already writes `invocations` directly), and a
resident that reported over gRPC would need the transport inside a type that has no business with it.

## R2 — Who knows how much work an agent still has

**Decision**: the broker fills in `waiting_before` and `waiting_after` itself, from its own store, at the moment it
receives the start and the end.

**Rationale**: the node cannot know — inbox depth is the broker's. `invocations` already requires both columns, and
`AgentFleet` computes them the same way. A count the node supplied would be the node's opinion of the broker's data.

**Alternatives considered**: the node asking `Waiting` before and after — two extra calls per turn, and still a
second-hand number.

## R3 — Where "running now" lives

**Decision**: in the broker's memory, keyed by agent, set by a start and cleared by the matching end or by the
reporting node's stream ending.

**Rationale**: it is a fact about connections, like `connected` and presence, and the broker already clears those when
it opens a store because a broker that has just started holds no connections. A running turn persisted in a table would
need exactly that clearing, plus a rule for a broker that died mid-turn; held in memory, a restart forgets it by
construction (spec edge case "the broker restarts during a turn"). Ended turns are durable, in `invocations`.

**Alternatives considered**: a `turn_started_utc` column on `agents` — durable state that is false after every crash.

## R4 — How an operator surface reads it

**Decision**: the operator registry read (`DescribeRegistry` → `AgentRecord`) gains the turn standing: whether a turn
is running and since when, and the last ended turn's end time, clean finish, permission refusals, and whether this
agent's turns are reported at all.

**Rationale**: the room already reads the registry every follow tick (0.37.5), so this costs no new read; and
`AgentRecord` is the operator's type. **The agent roster (`ListAgents`) is a different reply and must not gain these
fields** — how another agent's turns went is operator knowledge (§10.1/§10.2, FR-005). A test asserts the agent-facing
reply has no such field, in the style of `AgentsCannotSetTheirOwnLineTests`.

**Alternatives considered**: a new `DescribeTurns` operation — one more read per tick, and a second list of agents to
keep in step with the registry.

## R5 — Telling "stopped" from "nobody reports"

**Decision**: `JoinRequest` gains `reports_turns`; the broker records it per node, and an agent whose node did not set it
— or who is attached from an editor with no node — is **unknown**, never stopped.

**Rationale**: without it, a node one version behind reports nothing and every agent it runs reads as stopped the moment
its first turn ends — the defect this feature exists to remove, inverted. FR-010.

## R6 — The early flag

**Decision**: a new `FlagKind.PermissionRefused`, derived in `FlagRepository` from what is stored: an open exchange owed
by an agent whose most recent ended turn finished after it collected that exchange, recorded at least one permission
refusal, and was followed by no message from that agent in the conversation.

**Rationale**: flags are already a derivation over stored facts, never a stored state, so the flag clears itself when
the agent answers or the exchange closes (FR-012) with no bookkeeping.

**Read off the most recent turn, deliberately** (`/speckit-analyze` A1): a later turn that ends cleanly with no refusals
clears this flag even if the agent still has not answered. That is intended — the refusal is no longer what is stopping
it — and the exchange falls back to §6's ordinary thresholds, while the room goes on showing it stopped. A turn that ended owing work **without**
refusals is not flagged early (spec, US2 scenario 2): it may be pausing legitimately, and §6's thresholds stand.

## R7 — Waiting is not stopped

**Decision**: the derivation checks the wait-for graph: an agent that is itself the waiter on an open exchange in the
conversation is shown as **waiting on** that participant, not stopped.

**Rationale**: FR-009. An agent that asked somebody else and ended its turn to wait is doing exactly what it should.

## R8 — One derivation for both surfaces

**Decision**: `Activities` moves from `Parley.Terminal` to `Parley.Client`, gains the turn standing, and both surfaces
draw from it; the words live beside it.

**Rationale**: FR-013, and this repository's record of two surfaces describing one thing two ways.

## R9 — Protocol version

**Decision**: the protocol version does **not** move. The change is recorded in SPEC.md §18 under the current version as
an addition to the node and operator contracts, with the reason below. (`/speckit-analyze` finding I1, 2026-10-03.)

**Rationale**: the version is what an agent's adapter announces when it attaches, and the broker refuses any adapter
announcing a different one (`AgentsService`, the §18 check). Moving it would force every `parley-mcp` on every machine to
be upgraded in step with the broker — for a change no agent can see, since the `Agents` service is untouched (FR-005,
T010). Nodes are not version-checked at all, and every addition here is optional on the wire: an older node sends no
reports and its agents read as unknown (R5); an older surface ignores fields it does not know. §18's own test for a
version change is behaviour a second implementation's *agent* could observe, and there is none.

**Alternatives considered**: 0.8, as 010 did — rejected, because 010 changed what agents receive (a notification woke
them) and this does not.

## R10 — Guarding the report

**Decision**: the broker accepts a turn report only for an agent whose machine is the reporting node's machine, and only
while that node's stream is joined; anything else is ignored, not stored.

**Rationale**: §16 concedes anything that reaches a broker can claim to be anything, but a node claiming turns for an
agent on another machine is the cheap forgery, and refusing it costs one comparison. Probe it (constitution: probe, do
not reason).

## R11 — The spec/code disagreement this closes

SPEC.md §17 question 1 says `invocations` "records every turn with its duration and cost". On the node path nothing
writes it. **Decided: the code is wrong, the spec stands** — the evidence that question relies on has not been
collected for any deployment run through a node, which is every deployment since 012. Recorded in §18 when amended.
