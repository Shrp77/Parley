# Implementation Plan: A pinned graph of the work, and freezing an agent

**Branch**: `035-material-3-web-ui` | **Date**: 2026-11-20 | **Spec**: [spec.md](spec.md)

## Summary

Add a person-only **freeze** on an agent (stored in the project's store, honoured at the one place the broker decides to start or
wake an agent) and a **graph panel**, pinned from any console page, that draws projects, agents, conversations, spin-offs and
background work and carries the freeze and unfreeze acts. See [research.md](research.md) for each decision.

## Technical Context

**Language/Version**: C# on .NET 10; Razor Pages; gRPC (`proto/parley.proto`). **Storage**: SQLite, migration 011 (one nullable
column). **Testing**: xUnit v3 on Microsoft.Testing.Platform; unit, integration and a spec-text check. **Target**: Windows, Linux,
macOS (CI runs all three). **Constraints**: no new package; warnings are errors; every public member documented; the
dependency graph rules in CLAUDE.md. **Scale**: tens of agents, hundreds of conversations per project.

## Constitution Check

- *The specification is the product*: SPEC.md gains the freeze (§9 schema, §10.2 operations, §14 restart wake, §11.6 state
  wording) in the same change. Code and spec move together.
- *No agent has authority over another*: Freeze and Unfreeze are on `Operators` only; a test fails if `Agents` gains either.
- *Nothing times out; work stays open*: a freeze closes, moves and answers nothing. It is a recorded reduction of the 2026-11-18
  decision that a restart wakes whoever owes work, and it is the person's, per agent, cleared by the person or, unless they asked it to outlast one, by the broker's next start.
- *Invariants live in storage*: the column's CHECK refuses a value that is not an ISO-8601 UTC instant and refuses a freeze on the
  person seat or the facilitator.
- *Surfaces*: web, terminal (and the CLI's exit 64 naming it) carry both acts; `DriftTests` freezes it.
- *Boundaries*: no network bind change, no credential, the plugin ships no server configuration.

No gate is violated; the one standing rule narrowed is named above.

## Design

| Layer | Change |
|---|---|
| Store | `011_agent_hold.sql`: `ALTER TABLE agents ADD COLUMN frozen_utc TEXT` with a CHECK; `AgentRepository` reads and sets it. |
| Core | `AgentRecord.FrozenUtc`; `FreezeResult`/`FreezeProcess`; `IParleyBrokerClient.FreezeAsync`/`UnfreezeAsync`. |
| Broker | `OperatorConsole.FreezeAsync`/`UnfreezeAsync` (the store writes). |
| Server | `Ask` refuses a frozen agent (`WakeOutcome.Frozen`); `Operators.Freeze` records then `RequestStop`; `Unfreeze` clears then asks it to start if it owes work or anything is waiting for it; `Start` refuses a frozen agent; `frozen_utc` on the agent row. |
| Transport | `Freeze`, `Unfreeze`, `FreezeReply`, `frozen_utc`. |
| Client | `BrokerClient` calls; `AgentFreezing` (every project the agent serves, one result); `AgentGraph` (derivation and layout); frozen in agent wording. |
| Web | `Pages/Graph` fragment; layout tab, pin and script; `Act` arms `freeze`, `unfreeze`, `freezeall`, `unfreezeall`; the agent page and the graph link them. |
| Terminal | Freeze and Unfreeze in an agent's row menu through `AgentFreezing`. |
| Docs | SPEC.md, docs, CLAUDE.md state (version 0.64.0, migration 011, 027 to 012). |

## Phases

1. **Freeze in the store and broker** (US2 core): migration, record, repository, console ops, `Ask` check, server ops, proto.
2. **Client and surfaces for freeze**: `BrokerClient`, `AgentFreezing`, wording, `Act` arms, terminal entries.
3. **The graph** (US1): `AgentGraph`, the fragment, the panel.
4. **Project-wide** (US3): `freezeall`/`unfreezeall`.
5. **Spec, docs, version; full test run.**

## Risks

- Freezing from the graph ends a process serving other projects: the confirmation lists them (spec decision).
- `DriftTests`, `EveryActionHasADoorTests`, `SurfaceTests`, `KeysTakenBeforeTheTableTests` and `FormBindingTests` will each
  need the new acts to follow their rules; they are run after every phase.
