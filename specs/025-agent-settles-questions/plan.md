# Implementation Plan: The person talks; the agent decides what it settled

**Branch**: `019-layout` (no branch of its own, the owner's arrangement for 019–025) | **Date**: 2026-10-05 |
**Spec**: [spec.md](spec.md)
**Input**: Feature specification from `specs/025-agent-settles-questions/spec.md`

## Summary

The terminal's box stops guessing which of an agent's questions a person's line answers: every line is a new request,
so the agent must reply (research R1). In that reply the agent may name its own questions to the person that the line
settled; the broker closes each as `settled`, citing the person's request, with no message written on it (R3), or hands
the turn back to the agent where the question was the agent's clarification of the person's own request (R4). The
whole reply is refused if any name is not one the agent may settle (R5), and storage holds the rule (R6). The agent is
told which of its questions are open whenever the person's request is delivered (R8). The room shows *waiting on you*
as one row per agent with a count, and Enter lists the questions and moves the transcript to the one chosen; the web
conversation page shows the same (R7). The person's registry row stops reading open outside Parley (R9). Protocol
0.7 → 0.8 (R10); minor version bump, 0.41.0.

## Technical Context

**Language/Version**: C# on .NET 10
**Primary Dependencies**: gRPC (Grpc.AspNetCore / Grpc.Net.Client), Dapper over SQLite, Terminal.Gui 2.4.17, Razor
Pages, the MCP C# SDK in `parley-mcp`
**Storage**: SQLite, schema 1 edited in place (`exchanges.outcome` admits `settled`; new `exchanges.settled_by`; new
trigger `tr_settled_by_its_requester`)
**Testing**: xUnit v3 on Microsoft.Testing.Platform, run through the test executables; `Parley.Spec.Tests` executes the
schema fragments in SPEC.md
**Target Platform**: Windows, Linux, macOS (CI matrix)
**Project Type**: protocol reference implementation — broker, adapter, two operator surfaces
**Performance Goals**: none new; the settleable list is one indexed query per person's request drained
**Constraints**: Principle IV — the settleable list is bounded (20 entries × 160 characters), `settles` at most 50 ids
**Scale/Scope**: one conversation's exchanges between one agent and the person

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Check | Result |
|---|---|---|
| I. The specification is the product | SPEC.md is amended first (§5.6 new, §5.3, §9 schema, §10.1, §12.3, §18), in its own commit, before the code. | Pass |
| II. No agent has authority over another | An agent can settle only **its own** questions to the person, or hand **itself** the turn on the person's request. It cannot close another agent's work, a request it received, or anything between two agents (FR-009). It gains no authority over anyone; the person loses only obligations the agent who created them says are met. | Pass |
| III. Identity is fixed by the launch | `settles` names exchanges, never a party. Who settles is the caller the adapter already is. | Pass |
| IV. Terseness is a security control | New text crossing to an agent — the settleable list — is bounded (R8); it is the agent's own words returned to it. | Pass |
| V. Invariants live in storage | `settled` and `settled_by` are held by CHECKs and a trigger (R6); each gets a store test that removes the rule and watches it fail. | Pass |
| Boundaries: agent and operator operations apart | `settles` is on the agent-facing Result/Clarify; no operator operation is added. The registry fix is a read. | Pass |

**Post-design re-check**: unchanged. No violation; Complexity Tracking is empty.

## Project Structure

### Documentation (this feature)

```text
specs/025-agent-settles-questions/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── wire.md
│   └── surfaces.md
├── checklists/requirements.md
└── tasks.md            (/speckit-tasks)
```

### Source Code (repository root)

```text
SPEC.md                                   §5.6 new; §5.3, §9 (exchanges DDL), §10.1, §12.3, §13, §18
proto/parley.proto                        settles, SettleRefusal, SettleProblem, CANNOT_SETTLE, settleable, SETTLED, settled_by
src/Parley.Core/                          ProtocolVersion 0.8; ExchangeOutcome.Settled; Exchange.Settle / TurnBack / SettledBy;
                                          TransitionError.CannotSettle; SettleProblem
src/Parley.Store/Migrations/001_initial.sql   outcome 'settled', settled_by, tr_settled_by_its_requester
src/Parley.Store/ExchangeRepository.cs    read/write settled_by; settleable query
src/Parley.Broker/Broker.cs               SayAsync settles; DrainInboxAsync settleable
src/Parley.Server/                        Wire mapping; AgentsService Result/Clarify; registry open_outside skip
src/Parley.Mcp/ParleyTools.cs, ParleyInstructions   settles parameter, inbox list, wording
src/Parley.Client/Seating/                Composing.Plan → always Ask; Pairing keeps every owed exchange; Waiting-on-you derivation
src/Parley.Client/Rendering/Reading.cs    "settled by <agent> in exchange <n>"
src/Parley.Terminal/                      RoomRegion rows with counts; Shell question list and transcript jump
src/Parley.Web/Pages/Conversations/       Waiting on you section with anchors
tests/…                                   Core, Store (rule removed → fails), Broker, Server (over the wire), Mcp,
                                          Client, Terminal (drawn), Web, Spec
```

**Structure Decision**: The existing projects; no new project. The waiting-on-you derivation lives in
`Parley.Client` so both surfaces read one implementation (SC-005).

## Complexity Tracking

None.
