# Implementation Plan: An instruction survives the turn it asks for

**Branch**: `008-instruction-readback` | **Date**: 2026-09-08 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `specs/008-instruction-readback/spec.md`

## Summary

The broker settles an instruction because it stopped waiting, which destroys the agent's real answer
when it arrives, and it records a reply that no operation can read back. This adds the readback to
§10.2, makes giving up produce *pending* rather than a false *failed*, and replaces the guarantee
that a record eventually settles with two mechanisms that are true — a node disconnecting, and a
sweep bounded by the longest turn a machine will permit.

**No schema change.** The table already carries everything. What was missing was never storage.

## Technical Context

**Language/Version**: C# / .NET 10
**Primary Dependencies**: Grpc.AspNetCore, Dapper, Microsoft.Data.Sqlite, Terminal.Gui v2 (terminal
surface only), ASP.NET Core Razor Pages (console only)
**Storage**: SQLite, opened by the broker process alone
**Testing**: xUnit v3 on Microsoft.Testing.Platform
**Target Platform**: Windows, Linux, macOS
**Project Type**: Multi-project solution — domain, store, broker, transport, server, two operator
surfaces, one adapter
**Performance Goals**: Issuing an instruction returns in under two seconds regardless of the turn
(SC-004); the readback is bounded by the broker whatever a caller asks for
**Constraints**: `Parley.Core` has no I/O and no package dependencies; the broker is the only
process that opens the store; agent and operator operations live on different types and different
wire services
**Scale/Scope**: One deployment is tens of agents on a handful of machines; instructions are issued
by hand, so the readback's volume is small and its bound exists for the same reason every other
bound does rather than because anybody expects to reach it

### Decisions settled, with reasoning

Full argument in [research.md](./research.md). The load-bearing ones:

| # | Decision |
|---|---|
| R1 | Ships **0.21.0**; the deprecated `console` spellings are **not** removed here. The deprecation moves to 0.22.0, recorded explicitly in CLAUDE.md rather than allowed to lapse. |
| R2 | `instruct` returns **`pending`** on give-up, carrying the instruction's identifier, and settles nothing. §10.2's four outcomes become five. |
| R3 | The broker's wait drops to **30 seconds** and decides nothing. `TurnBudget` goes in `Parley.Core` — the only assembly both the broker and the node's invoker can see. |
| R4 | **One** readback operation, narrowable by agent or identifier, bounded pages, opaque token. |
| R5 | Two settling mechanisms: **node disconnect** (precise) and a **sweep** on `Broker.TickAsync` (backstop). |
| R6 | The console fires and returns; an agent's recent instructions live on the page that already shows that agent. |
| R7 | Bound by all three surfaces; `parley instructions` is what satisfies `DriftTests` in step 1. |
| R8 | Retention aged on **issue**, not settlement — a running instruction has no settle instant, so ageing on settlement would skip exactly the rows that most need a bound. |

## Constitution Check

| Principle | Status | Note |
|---|---|---|
| I. The specification is the product | **Met, and exercised** | The spec is the incomplete artifact here and gains an operation. This was decided and argued, not assumed — see *The decision this feature records*. |
| II. No agent has authority over another | **Untouched** | Nothing here is reachable by an agent. `instruct` remains the operator's, and the readback joins it on the same side. |
| III. Identity is fixed by the launch | **Untouched** | No registration path changes. |
| IV. Terseness is a security control | **Held** | The instruction text keeps the message cap. The readback is bounded by the broker, which is rule 8.5's argument one level up. |
| V. Invariants live in storage | **Held, and it is why this is small** | First-come settlement is already a `WHERE` clause, so FR-016 is free and the sweep cannot corrupt a settled record however it is written. The unsettled state is already a schema-enforced pairing of outcome and timestamp. |
| Only question and answer text crosses | **Not engaged** | An instruction is not peer traffic and no peer can reach it. |
| Operator and agent surfaces are different | **Held, and checked** | The new operation goes on `Operators` only. `Agents` gains nothing, checked by the existing wire-separation test and by SC-006. |
| `Parley.Core` has no I/O | **Held** | `TurnBudget` is a `TimeSpan` constant. |

**No violations. Nothing in Complexity Tracking.**

The one place this comes close: adding `TurnBudget` to `Parley.Core` puts a number about process
invocation into the domain. It earns its place — `MessageLimits` is already there on the same
footing, and the alternative is two constants in assemblies that cannot see each other, which is the
defect being fixed.

## Project Structure

### Documentation (this feature)

```
specs/008-instruction-readback/
├── spec.md
├── plan.md              # this file
├── research.md          # the ten decisions
├── data-model.md        # the instruction record and its states
├── contracts/
│   └── operations.md    # the wire and client surface this adds
└── checklists/
    └── requirements.md
```

### Source Code (repository root)

Changed by this feature, and nothing else:

```
src/Parley.Core/
├── TurnBudget.cs                 NEW  the longest a turn may run; read by broker and node alike
└── Instruction.cs                     the record the readback returns

src/Parley.Store/
├── InstructionRepository.cs           gains ListAsync and a sweep; keeps GetAsync
└── Pruner.cs                          gains its own delete for instructions, aged on issue

src/Parley.Broker/
└── OperatorConsole.cs                 gains the readback and the sweep

src/Parley.Transport/
proto/parley.proto                     Instructions rpc; InstructReply gains instruction_id

src/Parley.Server/
├── OperatorsService.cs                stops settling on give-up; returns pending; binds readback
├── NodesService.cs                    unchanged reporting path, now the only truthful settler
└── BrokerRuntime.cs                   settles a departed node's running instructions

src/Parley.Client/
└── BrokerClient.cs                    binds the readback for both surfaces

src/Parley.Cli/
└── Program.cs                         parley instructions

src/Parley.Web/Pages/Projects/
├── Act.cshtml.cs                      stops blocking
└── Agent.cshtml(.cs)                  shows an agent's recent instructions

src/Parley.Top/
└── AgentsRegion.cs / Shell.cs         the terminal's binding

SPEC.md                                §9.2 commentary, §9.4, §10.2 table and prose, §14
```

**Untouched, deliberately**: `src/Parley.Mcp`, `src/Parley.Headless` beyond deriving its turn
timeout from `TurnBudget`, `src/Parley.Setup`, `src/Parley.WebUi`, `src/Parley.Configuration`.

## Sequencing

**Step 1 — the protocol and the broker (0.21.0).** Everything above except the two surfaces. The
loss probe already committed inverts: the test that documents the loss becomes the test that proves
it cannot happen. `parley instructions` satisfies `DriftTests` with a real binding rather than an
exemption.

**Step 2 — the surfaces (0.21.1).** Console stops blocking, shows recent instructions; terminal
binds the readback.

US1 and US2 ship together and the spec's priorities are departed from on purpose: US2 alone has no
observable effect, because a reply surviving into a row nothing can read cannot be told from one
that was discarded. US4 lands inside step 1 rather than after it, because removing the
settle-on-give-up removes the only thing that guaranteed a record eventually settled.

## Complexity Tracking

*No constitutional violations. This section is empty and that is not an omission.*
