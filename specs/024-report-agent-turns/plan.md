# Implementation Plan: Report agent turns, so working and stopped are told apart

**Branch**: `019-layout` (with 019–023) | **Date**: 2026-10-03 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `specs/024-report-agent-turns/spec.md`

## Summary

The node already runs and measures every turn and tells the broker nothing about it. It will report a turn's start and
end (`Nodes.ReportTurn`); the broker records ended turns in the existing `invocations` table — making SPEC.md §17
question 1's claim true on the node path — and holds running turns in memory, cleared when the reporting node goes away.
The operator registry carries each agent's turn standing, `FlagRepository` derives a new permission-refused flag from
stored facts, and one derivation in `Parley.Client` turns it into working / waiting / stopped / unknown for both surfaces.
SPEC.md is amended first.

## Technical Context

**Language/Version**: C# on .NET 10
**Primary Dependencies**: gRPC (`proto/parley.proto`), SQLite via Dapper, Terminal.Gui v2, ASP.NET Core Razor Pages
**Storage**: SQLite; **no schema change** — `invocations` already has every column (data-model.md)
**Testing**: xUnit v3 on Microsoft.Testing.Platform, test executables run directly
**Target Platform**: Windows, Linux, macOS
**Project Type**: protocol + broker + node + two operator surfaces
**Performance Goals**: no new read per room refresh (rides on the registry read the room already makes)
**Constraints**: additive wire change and **no protocol version move** (research R9: moving it would refuse every
agent's adapter for a change agents never see); an older node degrades to "unknown", never to "stopped"
**Scale/Scope**: one RPC, five registry fields, one flag kind, one derivation moved and extended, two surfaces

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status |
|---|---|
| I. The specification is the product | **Pass, by ordering.** SPEC.md §10.2, §11/§12's node contract, §6's flags and §18 are amended in the first task, before any code (FR-015). It also settles a standing disagreement: §17 q1 says every turn is recorded and the node path records none — decided as a code defect (research R11). |
| II. No agent has authority over another | **Pass.** Nothing here lets an agent see or act on another's turns; it is the operator's view. |
| III. Identity fixed by the launch | **Pass.** No operation gains a sender. A node reports for agents on its own machine only (R10), checked by the broker. |
| IV. Terseness is a security control | **Pass.** Nothing new crosses to agents: the roster (`ListAgents`) gains no field, asserted by a test. The new fields are numbers, times and booleans on the operator's read; no free text. |
| V. Invariants live in storage | **Pass, with a note.** Ended turns use `invocations`' existing constraints. The running turn is deliberately **not** stored (R3): it is a fact about live connections, and stored it would be false after every crash — the same reason `connected` is cleared when a store opens. |
| Boundary: agent and operator operations are different surfaces | **Pass.** `ReportTurn` is on `Nodes`; the read is on `Operators`; `Agents` is unchanged. |
| Quality gate: probe, do not reason | Planned: forged reports from another machine, a report for an unknown agent, a late end after a newer start, a node dropping mid-turn — each written as the malicious case first. |

No violations; nothing to justify in Complexity Tracking.

## Project Structure

### Documentation (this feature)

```text
specs/024-report-agent-turns/
├── spec.md
├── plan.md              # this file
├── research.md          # R1–R11
├── data-model.md
├── quickstart.md        # the walk
├── contracts/turns.md   # wire and wording
└── checklists/requirements.md
```

### Source Code (repository root)

```text
SPEC.md                                   # §6, §10.2, §11/§12, §17 q1, §18 — first
proto/parley.proto                        # JoinRequest.reports_turns, Nodes.ReportTurn, registry fields, FlagKind
src/Parley.Core/                          # AgentRecord turn standing, FlagKind.PermissionRefused, TurnReport shape
src/Parley.Store/FlagRepository.cs        # the derived flag
src/Parley.Store/InvocationRepository.cs  # read of the latest turn per agent
src/Parley.Server/                        # NodesService.ReportTurn, running-turn table in BrokerRuntime, registry mapping
src/Parley.Cli/NodeCommand.cs             # report start and end around both turn sites; join with reports_turns
src/Parley.Client/                        # Activities moved here and extended; BrokerClient mapping; the words
src/Parley.Terminal/RoomRegion.cs         # draw stopped / waiting / unknown; legend
src/Parley.Web/Pages/Conversations/       # the same, on the participants list
tests/…                                   # per phase; probes in Parley.Server.Tests
```

**Structure Decision**: no new project. `Activities` moves from `Parley.Terminal` to `Parley.Client` because both
surfaces now need it (R8); `Parley.Client` reaches no store, and `GraphTests` keeps it so.

## Phases (for /speckit-tasks)

1. **SPEC.md first** — §11/§12 node reports turns; §10.2 registry turn standing; §6 permission-refused flag; §17 q1
   closed; §18 version entry (R9). Spec tests green.
2. **Wire and core** — proto, `AgentRecord`, `FlagKind`; the `Agents` service unchanged, with the test that says so.
3. **Broker** — `ReportTurn` with R10's guard, running-turn table cleared on node stream end, `invocations` row with
   broker-computed waiting counts, registry fields, the flag. Probes first.
4. **Node** — report start and end at both turn sites; `reports_turns` on join; a failed report never fails a turn.
5. **Surfaces (US1, US3)** — `Activities` in `Parley.Client`; terminal room and web participants list.
6. **Flag on both surfaces (US2)** — terminal Health, web flags page, `parley flags`.
7. **Polish** — README, CLAUDE.md pointer, quickstart walk, version bump at the first code commit.

## Complexity Tracking

None.
