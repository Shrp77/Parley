# Implementation Plan: Parley as facilitator

**Branch**: `019-layout` (022 has no branch of its own — the owner's call of 2026-09-28) | **Date**: 2026-09-28 |
**Spec**: [spec.md](spec.md)
**Input**: Feature specification from `specs/022-parley-facilitator/spec.md`

## Summary

Every project gets **Parley**, a participant the broker provides, which asks each attached agent what it owns
once, with no action from anybody, and — with the project's **Auto Accept** switch on, which it is by default —
makes the answer that agent's ownership line and refreshes the agent once it is idle so peers read it. A new
ownership review on both surfaces shows every line, where it came from, and any answer waiting, and lets a person
accept, edit, discard, reply or ask again without holding a seat.

Technically: the facilitator is a registry row with a reserved role (`parley-facilitator@<machine>`, shown as **Parley**), written by the broker
and refused to every attach and seat in storage; a `Facilitator` type in `Parley.Broker` does the store work;
`BrokerRuntime` runs it under the gate; the serve host runs it on the existing per-project sweep and writes
`.mcp.json` through 020's `OwnershipLine`; a new node command refreshes an agent when its turn is over. Research
found SPEC.md and the constitution both saying the broker is not a participant, so both are amended **first**.

## Technical Context

**Language/Version**: C# on .NET 10 (the repository's `global.json`)
**Primary Dependencies**: gRPC (Grpc.AspNetCore / Grpc.Net.Client), Dapper over Microsoft.Data.Sqlite, Terminal.Gui
v2, ASP.NET Core Razor Pages — all already in use; **no new package**
**Storage**: SQLite, the project's own store; changes made in place in `001_initial.sql` (research R5)
**Testing**: xUnit v3 on Microsoft.Testing.Platform; `Parley.Spec.Tests` executes the schema written in SPEC.md
**Target Platform**: Windows, Linux and macOS (CI matrix)
**Project Type**: a protocol with a reference implementation — broker service, CLI host, two operator surfaces
**Performance Goals**: one indexed query per project per sweep (5 s); one Request per agent, once
**Constraints**: the layering GraphTests enforces; `Broker`'s and `OperatorConsole`'s fixed public surfaces; no
agent-facing operation may mention ownership; no agent is restarted mid-turn
**Scale/Scope**: deployments of tens of agents per project (the owner's is nineteen and four)

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked after Phase 1 design.*

| Principle | Status | How |
|---|---|---|
| **I. The specification is the product** | Pass, with ordering | SPEC.md is amended before code (§3, §5, §9, §10, §11, §12.3, §14, §17 q2/q10, §18). Research found SPEC.md saying the opposite of the feature in four places; each is amended, not worked around. |
| **II. No agent has authority over another** | **Amendment required first** | The facilitator is a new asymmetry beside the operator. It may ask, reply with a person's words, and agree its own conversation is done — nothing else (FR-003). The constitution names it and what it may never do before any code exists. |
| **III. Identity fixed by the launch** | **Amendment required first** | The facilitator's identity is fixed by the broker for its own store, not by a launch; it is unclaimable by any launch or call, and storage enforces that (research R1). |
| **IV. Terseness is a security control** | **Finding, recorded** | Auto Accept is a further reduction in the ownership control, on top of 020's removed bound. Recorded as one, in the constitution's own words for 020's, with what replaces it: truthful provenance (`automatic`) and a review. The question text is fixed and bounded; a person's reply is held to the message cap. |
| **V. Invariants live in storage** | Pass | The reserved role, the facilitator's adapter, "never asked twice", and "accepted only with a line" are triggers and indexes, not code checks (data-model.md). |
| **Boundaries — agent vs operator surfaces** | Pass | Every new operation is §10.2; §10.1 is unchanged; `agents()` omits the facilitator. |
| **Boundaries — Core has no I/O** | Pass | New Core types are records and rules only. |
| **Quality gates** | Owed | One combined gate for 019–022 (the owner's call); 022's T-last is that gate. |

**Gate result**: proceed to design **on condition** that the constitution amendment (4.0.0, MAJOR) is its own
change and lands before implementation — Governance: "A principle changes in its own amendment, never inside a
feature's implementation." That is task T001; nothing else starts before it.

**Re-check after Phase 1**: the design holds the conditions above. No new violation: the facilitator gains no
operation outside FR-003, no agent gains anything, and the one new node command can only be sent by the broker
about an agent the node itself started.

## Project Structure

### Documentation (this feature)

```text
specs/022-parley-facilitator/
├── spec.md              # the feature, with clarifications and planning corrections
├── plan.md              # this file
├── research.md          # 15 findings from reading, 12 decisions
├── data-model.md        # the facilitator's row, ownership_questions, facilitation, the automatic source
├── quickstart.md        # the walk
├── contracts/
│   ├── operations.md    # §10.2 additions, the node command, both surfaces
│   └── asking.md        # the two question texts
└── checklists/
    └── requirements.md
```

### Source Code

```text
.specify/memory/constitution.md        # 4.0.0 amendment (T001, first)
SPEC.md                                # amended before code

src/Parley.Core/                       # OwnershipSource.Automatic; Facilitation records; reserved role rule; the two question texts
src/Parley.Store/Migrations/001_initial.sql   # facilitator triggers; ownership_questions; facilitation; source
src/Parley.Store/                      # repositories for questions and the switch; roster omits the facilitator
src/Parley.Broker/Facilitator.cs       # new: find, ask, read back, settle, agree done
src/Parley.Broker/OperatorConsole.cs   # the five §10.2 operations
src/Parley.Server/BrokerRuntime.cs     # FacilitateAsync under the gate; wake the receiver; RefreshAgent to the node
src/Parley.Server/{Operators,Nodes,Agents}Service.cs  # wire; refuse the reserved role at attach
proto/parley.proto                     # §10.2 rpcs, registry fields, RefreshAgent
src/Parley.Client/                     # BrokerClient ops; OwnershipAsk's two texts; recognition by requester
src/Parley.Cli/ServeCommand.cs         # per-project facilitation loop beside the sweep; writes .mcp.json
src/Parley.Cli/NodeCommand.cs          # handle RefreshAgent
src/Parley.Headless/ResidentAgent.cs   # WhenIdleAsync on the turn lock
src/Parley.Terminal/                   # ownership review; Auto Accept switch; Parley shown apart; ask again
src/Parley.Web/                        # the same, each with its GET description

tests/...                              # beside each change; SurfaceBoundaryTests gains the Facilitator surface
```

**Structure Decision**: no new project. The work splits across the three layers that already exist, each doing
only what its references allow (research R3): store work in `Parley.Broker`, the gate and the wire in
`Parley.Server`, and the one step that writes a repository's file in `Parley.Cli`, the only host that references
both `Parley.Setup` and `Parley.Server`.

## Order of work

The order is the plan; the reasons are in research.md.

1. **The constitution, 4.0.0** — its own change, first (Governance).
2. **SPEC.md** — the facilitator as a third participant; §5's "writes no message" and §9.3's "writes nothing else";
   §10.1's "which it is not"; principle 2.10's starters; §11.1's list of what ends a resident; §12.3; §17 q2 and
   q10 (020's owed amendment and 022's together); §18 as a further 0.7 amendment; the `accept_ownership` misname.
   Schema fragments in §9.2 so `SpecSchemaTests` executes them.
3. **Storage** — the facilitator's rules, the two tables, the new source. Each rule probed failing first.
4. **Core and the broker** — `Facilitator`, the operator operations, the roster omission.
5. **Server and wire** — the gate, the waking, the attach refusal, `RefreshAgent`.
6. **The serve host and the node** — the loop, the file write, the refresh.
7. **Surfaces** — the review and the switch on both; Parley shown apart; ask again without a seat.
8. **The walk** (quickstart.md), then the combined 019–022 review gate.

## Risks, each with the probe that answers it

| Risk | Probe (must be watched to fail first) |
|---|---|
| An agent claims the facilitator's name | Attach as `parley-facilitator@x`, seat as `parley-facilitator`, insert a row with the role directly — all refused; a folder named `Parley` still enrols |
| An agent sends work to Parley | A Request addressed to it, a Clarify on an exchange it did not open — refused |
| The open question steers an agent's work | An agent in `asked_back` sends a Request naming no conversation; it is not assigned to the `owns-` one |
| A discarded answer is re-asked | Discard with no line; nothing is asked on the next tick |
| A question asked twice | Restart agent and broker; reattach from two windows; the partial unique index refuses the second |
| An agent's own `owns-` conversation auto-accepted | Agent opens `owns-orders-…` and answers itself; nothing is accepted |
| A restart mid-turn | Refresh sent while a turn holds the lock; the stop waits for the turn to end |
| A person's words shown as Parley's | Reply from the review; the transcript attributes `parley:person` |
| Conversations never close | An ownership conversation concludes once its question is settled |
| Research finding 14 (no cascade) | Destroy an `owns-` conversation that produced an accepted line; purge the agent |

## Complexity Tracking

| Violation | Why needed | Simpler alternative rejected because |
|---|---|---|
| A broker-written registry row (§12.3 says only an agent registers) | Every sender must be a row (research finding 5) | Relaxing the sender keys admits every unregistered sender; schema note 002 refused it for the operator |
| A new node command | Only the node knows a turn is in progress (finding 7) | The broker restarting blind would stop agents mid-work |
| A third public surface in `Parley.Broker` | `Broker` and `OperatorConsole` have fixed surfaces for a reason | Adding to `Broker` hands every agent connection a facilitator method |
