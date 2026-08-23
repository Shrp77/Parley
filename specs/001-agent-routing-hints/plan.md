# Implementation Plan: What each agent owns, in the registry

**Branch**: `001-agent-routing-hints` | **Date**: 2026-08-23 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/001-agent-routing-hints/spec.md`

## Summary

Give the registry one optional, bounded, single-line statement per agent of what that agent
owns; source it from the launch environment as `PARLEY_OWNS`; return it from the agent-facing
roster beside the name, and from the operator's listing beside the workspace. Resolve
`SPEC.md` §17 question 10 by recording the decision and its argument.

The technical shape follows almost entirely from where the value comes from. Because it
arrives in the launch environment rather than through a call, it needs no new operation, no
new permission, and no defence against an agent writing another's — the same three properties
that make identity unforgeable, obtained the same way. What it does need is a bound the schema
enforces, because the text crosses to every agent that lists the roster.

## Technical Context

**Language/Version**: C# 14, .NET 10
**Primary Dependencies**: Microsoft.Data.Sqlite, Dapper, ModelContextProtocol
**Storage**: SQLite; `STRICT` tables; migrations are embedded resources applied in name order
and tracked with the `user_version` pragma
**Testing**: xUnit v3 on Microsoft.Testing.Platform; the SPEC suite executes every schema
fragment in `SPEC.md` against SQLite in the order written
**Target Platform**: Windows, Linux, macOS — CI runs all three
**Project Type**: Domain library, storage library, broker library, MCP server, operator CLI
**Performance Goals**: None new. The field is read in an existing query and adds one bounded
column to a table with one row per agent.
**Constraints**: `Parley.Core` takes no I/O and no package dependency; the bound lives in the
schema, not in the code that writes it; the agent-facing projection widens by this field and
nothing else.
**Scale/Scope**: Tens of agents per database. The roster is listed on demand, not on a timer.

## Constitution Check

*GATE: passed before Phase 0. Re-check after Phase 1.*

| Principle | Assessment |
|---|---|
| I. The specification is the product | **Pass.** `SPEC.md` changes first: the §10.1 `agents()` description, the registry DDL fragment §12.1 executes, and §17 question 10 moves from open to decided. Code follows the document rather than the document being written up afterwards. |
| II. No agent has authority over another | **Pass.** The statement is advisory. Nothing is routed by it, refused by it, or compared against it; there is no uniqueness constraint and no arbitration. Two agents may claim the same ground and the broker will not say which is right. |
| III. Identity is fixed by the launch | **Pass, and load-bearing.** The statement comes from `PARLEY_OWNS` and no operation accepts it. An agent can no more edit its charter than its name, and cannot touch a peer's at all. This is the answer to the objection that a self-declared field is worth no more than the prose it replaces: a person declares it, in the file that already fixes identity. |
| IV. Terseness is a security control | **Pass.** 100 characters, one line, enforced in the schema. The number is measured rather than chosen: at the 160 this started with, a ten-agent roster cost 2,352 characters against a 2,000-character message, so listing peers cost more than asking one. See research.md. |
| V. Invariants live in storage | **Pass.** Length and the absence of control characters are `CHECK` constraints on the column. The server also refuses a bad value at start-up, but that is a courtesy to the operator, not the enforcement. |

**Boundary review — the agent-facing projection widens.** `AgentSummary` exists so that a new
column on the stored record does not reach a peer until somebody decides it should. This
feature is that decision, made once, for one field, and argued in the specification: the value
is authored by an operator for the express purpose of being read by peers. Everything else
`AgentSummary` withholds stays withheld, and the check that it does is part of the work.

No entry in Complexity Tracking: nothing here needs a justified violation.

## Decisions

### D1. The statement belongs to the launch, so it refreshes on every launch

**This is the finding that shapes the work.** `BrokerSession` registers through
`AgentRepository.RegisterIfAbsentAsync`, which writes *nothing* when the name is already
known. That is deliberate and well argued: a second launch must not revive a retirement by the
side door, and must not rewrite an agent an operator described in a manifest as Headless —
with the endpoint the broker needs to invoke it — into a configuration-free Pull agent because
somebody opened that repository once.

The consequence for this feature is fatal if left alone: `PARLEY_OWNS` would take effect on an
agent's *first* launch and never again. Changing it later would do nothing, and a statement
that cannot be corrected is worse than none.

What resolves it: a narrow update alongside registration, touching this one column, for an
agent that is already registered and not retired. It writes no status, so it cannot resurrect a
retirement, and no tier or configuration, so it cannot flatten a manifest. A launch that sets
no `PARLEY_OWNS` clears the statement (spec FR-004), because an operator removing the line has
withdrawn it and a coalescing update would keep a wrong statement for ever.

**`workspace` was going to ride along and does not.** It has the same staleness by the same
mechanism, and an existing test states the contract that forbids the same fix:
`Launching_over_an_operators_registration_leaves_it_exactly_alone` requires that a launch
saying nothing about a workspace leaves the manifest's alone. The two fields are not symmetric
— a launch supplies `owns` or withdraws it, and cannot be silent in a way that means "keep
what you have" — and treating them as one broke that test on the first attempt. Left as a
separate, named problem.

### D2. The bound is enforced twice, on purpose, and only one of them is the enforcement

The schema refuses a statement longer than the cap or containing a control character.
The server also refuses it at start-up, with a message naming the variable — the same
treatment a malformed agent name gets, and for the same reason: a misconfiguration belongs to
whoever wrote the launch configuration, and a server that starts and then fails one write
reports it to the model instead.

### D3. `SPEC.md` §17 question 10 is answered rather than deleted

The question stays, with its answer and the argument, in the form §17's closed items already
use. The disclosure trade-off it names is real and does not stop being real once the decision
is made; a reader of a future change needs to know it was weighed.

### D4. No new operation

`agents()` gains a field. There is no `set_owns`, no `describe`, and no registration call. The
surface check that enumerates the broker's public members should pass unchanged.

## Project Structure

### Documentation (this feature)

```text
specs/001-agent-routing-hints/
├── spec.md              # Feature specification
├── plan.md              # This file
├── research.md          # Phase 0 — what the existing code forces
├── data-model.md        # Phase 1 — the column, the projection, the migration
└── tasks.md             # Phase 2 — written by /speckit-tasks
```

No `contracts/` directory and no `quickstart.md`. The contract for this change is `SPEC.md`
itself, which is the repository's primary artifact and already has a suite that executes it; a
second copy of it under `specs/` would be a thing to drift.

### Source code

```text
SPEC.md                                     §10.1 agents(), the §12.1 registry DDL, §17 question 10

src/Parley.Core/
├── AgentSummary.cs                          the projection gains Owns
├── OwnershipStatement.cs                    NEW — the bound, and what a valid one is
└── DeploymentEnvironment.cs                 PARLEY_OWNS, shared with the operator's tool

src/Parley.Store/
├── Migrations/001_initial.sql               the column and its constraints — nothing is
│                                            deployed anywhere, so there is no migration
├── AgentRepository.cs                       AgentRecord gains Owns; RefreshOwnershipAsync
└── (no change to WriteScope or ParleyDatabase)

src/Parley.Broker/
└── Broker.cs                                ListAgentsAsync carries Owns through

src/Parley.Mcp/
├── ServerOptions.cs                         reads and validates PARLEY_OWNS
├── BrokerSession.cs                         registration applies the launch-owned update
├── ParleyTools.cs                           parley_agents returns owns
└── ParleyInstructions.cs                    says the roster carries what each peer owns

src/Parley.Cli/
└── OperatorCommands.cs                      agents shows it, and marks its absence

tests/Parley.Core.Tests/                     the bound, valid and invalid statements
tests/Parley.Store.Tests/                    the constraints refuse; migration on an old file
tests/Parley.Broker.Tests/                   the projection; the surface is unchanged
tests/Parley.Mcp.Tests/                      end to end through the real server
tests/Parley.Cli.Tests/                      the operator listing, present and absent
tests/Parley.Spec.Tests/                     the document's own DDL still executes
```

**Structure Decision**: no new project and no new layer. The change is one column, one
projection field, one environment variable and one migration, threaded through the existing
five assemblies. `OwnershipStatement` goes in `Parley.Core` beside `AgentName` because it is a
validated domain value with a rule and no I/O, and because the Core tests can then exercise
the rule without a database.

## Phases

**Phase 0 — research.** What the existing code forces on the design; see
[research.md](./research.md). Complete.

**Phase 1 — design.** The column, its constraints, the projection and the migration path; see
[data-model.md](./data-model.md). Complete.

**Phase 2 — tasks.** Written by `/speckit-tasks` into `tasks.md`.

**Phase 3 — implementation.** Specification first, then storage, then the value type, then the
three consumers. Each user story is independently testable and P1 is the first two together:
the field is useless if it can be recorded but not read, or read but not recorded.

**Phase 4 — review.** The three lenses, rotated after the last edit, run to convergence. The
security lens has specific work here: the statement is text an operator writes that lands in
every peer's working memory, which is an injection surface, and the check is that the
constraints hold against a reconstruction of each abuse rather than that they look right.

## Complexity Tracking

No constitutional violations to justify.
