# Implementation Plan: The person always has a seat

**Branch**: `019-layout` (no branch of its own, following 022) | **Date**: 2026-09-29 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `specs/023-always-a-seat/spec.md`

## Summary

Every project gets a person seat, `human-user@<project>`, which the broker provides the way it provides the
facilitator, and the facilitator is renamed `parley-facilitator@<project>` by the same rule. Neither has a
machine. It is never retired, purged or released. Surfaces act as it with no join step. Being
**here** or **away** is still read from whether a surface holds a connection. A surface drops the connection after
20 idle minutes and takes it back on the next keystroke, and nothing tells the person their seat was released,
because it was not. A message to the person is always accepted, and the sender is told the person has it and
will answer, never that they are unreachable. Agents are told that waiting for the person's decision is correct.

Technically: one broker-written row; the `agents.machine` column made empty for the two broker-provided
participants, with a check holding their second half to a project's grammar; storage rules for the person, split
between Phase 2 and Phase 7; two new presence values
and one new wake outcome in Core and on the wire; `SeatBook` taking its name from the registry instead of from a
form; the join, leave, cookie and reinstate paths removed from both surfaces; and the standing instructions and
flag wording changed. The constitution's Principle III is amended first.

## Technical Context

**Language/Version**: C# on .NET 10 (the repository's `global.json`)
**Primary Dependencies**: gRPC, Dapper over Microsoft.Data.Sqlite, Terminal.Gui v2, ASP.NET Core Razor Pages. All
already in use; **no new package**
**Storage**: SQLite, each project's own store; edited in place in `001_initial.sql` (research R2)
**Testing**: xUnit v3 on Microsoft.Testing.Platform; `Parley.Spec.Tests` executes SPEC.md's schema
**Target Platform**: Windows, Linux and macOS
**Project Type**: a protocol with a reference implementation: broker, CLI host, MCP adapter, two operator surfaces
**Performance Goals**: none new. One indexed lookup per broker start; no new polling
**Constraints**: the layering `GraphTests` enforces; `Broker`'s fixed public surface (`SurfaceBoundaryTests`); no
agent-facing operation gains a person-only capability; `SurfaceTests`' one-file-per-door rules
**Scale/Scope**: one person seat per project; about 53 test files seat somebody under a typed name today

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked after Phase 1 design.*

| Principle | Status | How |
|---|---|---|
| **I. The specification is the product** | Pass, with ordering | SPEC.md is amended before code: principle 2.6, §4.0, §5 *Blocked*, §8 rule 10, §10.1 `agents()`, §10.2's refusals, §12.3, §14's presence table, §17 q2, §18. |
| **II. No agent has authority over another** | Pass | The person was already a participant. Nothing gains an operation. "The facilitator is never the person seated" is kept and now enforced by storage (R2). |
| **III. Identity fixed by the launch** | **Amendment required first** | A second identity fixed by the broker rather than a launch, beside the facilitator. Any surface reaching the broker speaks as it, which §17 q2 already concedes of typed seats. **5.0.0, MAJOR**, because it changes what the principle permits (research R9). |
| **IV. Terseness is a security control** | Pass | No new text crosses except fixed sentences. The roster gains one row per project. |
| **V. Invariants live in storage** | Pass, with a recorded limit | Never retired, never purged, one per store, role-to-adapter, "no machine only for these two" and "no ownership line on either" are triggers, an index and checks, each probed failing first (data-model.md). **An attach under the existing name writes no row**, so the refusal at attach is code trusting the caller's reported adapter. The owner's call (`/speckit-analyze` issue 1): record it, as §16 already concedes, rather than move the check without strengthening it. |
| **Boundaries — agent vs operator surfaces** | Pass | No new operation on either side. Two enum values and a wake outcome. |
| **Boundaries — Core has no I/O** | Pass | `PersonSeat` is constants and a rule. |
| **Quality gates** | Owed | Whether 023 joins the combined 019–022 gate is the owner's call. |

**Gate result**: proceed to design **on condition** that the 5.0.0 amendment is its own commit and lands before
any code. That is task T001.

**Re-check after Phase 1**: the design holds. The only new capability is that every surface speaks as one broker
fixed name, which is what III's amendment names. The person gains nothing the typed seat did not have.

## Project Structure

### Documentation (this feature)

```text
specs/023-always-a-seat/
├── spec.md
├── plan.md               this file
├── research.md           R1–R11
├── data-model.md         the row, the triggers, the enum values
├── quickstart.md         the walk
├── contracts/
│   └── person-seat.md    identity, presence, sending, instructions, surfaces
├── checklists/
│   └── requirements.md
└── tasks.md              /speckit-tasks
```

### Source Code

```text
src/Parley.Core/          PersonSeat.cs (new); AgentTier.cs (presence values); AgentLiveness.cs;
                          WakeOutcome.cs; Seats.cs (folds into PersonSeat)
src/Parley.Store/         Migrations/001_initial.sql (machine column, checks, triggers, index);
                          AgentRepository.cs (EnsurePersonAsync; EnsureFacilitatorAsync takes the project)
src/Parley.Cli/           ServeCommand.cs (passes the project name to the broker)
src/Parley.Server/        BrokerRuntime.cs (Open takes the project; ensure on start; wake; outstanding-work
                          skip); AgentsService.cs
                          (attach and caller refusals); OperatorsService.cs (Subject refuses the person); Wire.cs
src/Parley.Mcp/           ParleyInstructions.cs; ParleyTools.cs (receiver_is, parley_result, parley_agents)
src/Parley.Setup/         EnrollmentPlan.cs (reserved role)
src/Parley.Client/        Seating/Seat.cs, SeatBook.cs, SeatHold.cs, SeatVoice.cs; Rendering (flag wording)
src/Parley.Cli/           OperatorCommands.cs (flags, registry)
src/Parley.Terminal/      Shell.cs, AgentsRegion.cs, RoomRegion.cs, LineRegion.cs, Participation.cs,
                          Withdrawing.cs, Health.cs, TerminalCommand.cs
src/Parley.Web/           Pages/Join.*, Leave.* (removed); Seating/SeatCookie.cs (removed);
                          Shared/_Layout.cshtml; Conversations/Detail.*; Inbox.*; Projects/Health.*
proto/parley.proto        two presence values, one wake outcome
SPEC.md, CLAUDE.md, README.md, .specify/memory/constitution.md
```

## Order of work

1. **The constitution, 5.0.0**, in its own commit.
2. **SPEC.md**, every section listed in the Constitution Check, with the schema fragments in §9.2 so
   `SpecSchemaTests` executes them.
3. **Storage**: the triggers and the index, each probed failing first; `EnsurePersonAsync`.
4. **Core and wire**: `PersonSeat`, presence, wake outcome, proto.
5. **Broker and server**: ensure on start, the wake special case, the refusals.
6. **Agents' instructions** (MCP).
7. **Client seating**: `Seat` from the registry; auto-take; drop on idle and re-take on activity. **Convert the
   terminal's `Seated` fixture here, before touching the surfaces**, because 37 test files rest on it.
8. **Terminal**, then **web UI**: remove join, leave, cookie and reinstate; waiting count always shown; flag
   wording.
9. **README and CLAUDE.md.**
10. **The walk** (quickstart.md), then the review gate.

## Risks, each with the probe that answers it

| Risk | Probe (watched to fail first) |
|---|---|
| An agent takes `human-user` | Attach, enrol and a direct insert as `human-user` with any other adapter: all refused |
| A second person seat appears | Rename the machine and restart; insert a second `parley-console` row: one person, still |
| The person is retired or purged | `retire` and `purge` through the operator and by direct SQL: refused |
| A sender still told Unreachable | Request to the person with no surface open and no node at all: `WITH_THE_PERSON` |
| A node asked to start the person | A node whose catalogue happens to contain role `human-user`: never asked (enrolment refuses it, but the wake must not depend on that) |
| The seat claims presence it does not have | Open a surface, let the idle seam fire: registry reads `away`; touch: `here` |
| A surface names the seat for itself | Surface with `PARLEY_MACHINE=laptop` against project `alpha`: speaks as `human-user@alpha` |
| The project half is read as a machine | `Nodes.Describe("alpha")` knows no such machine; `AgentsOnAsync("alpha")` finds neither participant; every C# `.Machine` read audited (T013a) |
| An agent still reports blocked because the person is away | The walk's step 7, with a real agent |
| A deleted test took a guard with it | tasks.md records each of the ~53 files as replaced, kept or deleted, with why |

## Complexity Tracking

| Violation | Why needed | Simpler alternative rejected because |
|---|---|---|
| A second broker-written registry row (§12.3 says an agent registers itself) | The person must exist before any surface opens (owner's answer A) | Registering on first attach leaves an unopened project with no person |
| Presence values that apply to one adapter only | One implementation of here/away | Four renderers each translating `running` would drift |
| A NULL machine for two roles in the table every other table references | The owner's call: these two have no machine | A `nodes` row named for the project was offered and declined; it would record a machine that does not exist |
