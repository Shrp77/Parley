# Implementation Plan: Three-view operator surfaces, and conversations that can be archived and destroyed

**Branch**: `006-three-view-operator` | **Date**: 2026-09-05 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/006-three-view-operator/spec.md`

## Summary

Replace the four-regions-on-one-screen arrangement of 005 with three drill-down views — projects,
one project, one conversation — on **both** surfaces, and add the four operator operations those
views need and the protocol does not have: archiving a conversation, reversing that, destroying one,
and starting a catalogued agent on demand.

The protocol work lands first, because the surfaces are clients of it and building them against
operations that do not exist yet means building them twice.

Four things the spec left open are settled here, three of them from the protocol rather than from
judgement. The largest is that **005's central refusal was answered by reading §12.3 to the end**:
the specification already says spawn-on-demand agents are reached through §12's manifest rather than
by self-registration, and `NodesService.RequestStart` already starts a role from a node's catalogue
with no registry lookup at all. Nothing has to bend.

## Technical Context

**Language/Version**: C# 13 on .NET 10
**Primary Dependencies**: Terminal.Gui 2.4.17 (terminal), ASP.NET Core Razor Pages (console),
Grpc.Net.Client, Dapper + Microsoft.Data.Sqlite (store, broker process only)
**Storage**: SQLite, one file per project, opened by the broker process alone (§4)
**Testing**: xUnit v3 on Microsoft.Testing.Platform; the terminal driven headless through
`Driving`, the console through `WebApplicationFactory`, the protocol through
`tests/Parley.Spec.Tests` which executes every schema fragment in `SPEC.md`
**Target Platform**: Windows, Linux and macOS; the terminal must work over SSH with no browser
**Project Type**: CLI tool plus a web console, over a gRPC broker, against a local store
**Performance Goals**: A view answers within the broker read it needs; no polling, no timers moving
a person's selection under their hand
**Constraints**: `Parley.Core` no I/O and no packages; no SQLite on either surface's project graph;
message cap 8,000 characters enforced in the schema; every public member documented; warnings are
errors
**Scale/Scope**: Tens of projects per machine, tens of agents per project, thousands of messages per
conversation. Three views, four new operations, one schema migration.

## Decisions settled here

The spec named three things planning had to settle and must not assume. All three are settled, plus
two corrections the spec got wrong.

### Q1 settled — an added agent is startable at once, and §12.3 is not weakened

`research.md` R1 carries the argument. In short: 005 rejected this by ruling out a *surface*
registering an agent, which is correct and is a different question from starting the agent so that it
registers itself. §12.3's closing paragraph already routes spawn-on-demand through §12's manifest —
the node catalogue — and `NodesService.RequestStart`'s only guard is that the node's catalogue names
the role. No registry lookup exists on that path.

**So: a new operator operation asks the node to start a catalogued role once, and enrolment calls it
after writing the two files.** The adapter registers the agent it attaches, which is exactly what
§12.3 permits.

**This reverses 005's amendment, and 005's record is kept rather than edited.** Its User Story 3
still carries the paragraph explaining why the easy version was refused; that reasoning was right
about the thing it examined. What changed is that somebody looked at the sentence after it.

### Q2 settled — an archived conversation that receives a message stays archived

`research.md` R4. Delivery is untouched and no agent learns anything. Auto-un-archiving would let an
agent reverse an operator's decision by sending a message, which is the one direction of authority
Principle II does not have. The busy-conversation-hides objection is answered in the view: the count
of archived conversations is always shown, and ones with activity since archiving are marked.

### Q3 settled — a destroyed conversation leaves a tombstone of facts and no content

`research.md` R3. Identifier, when, how many messages, which agents. No text, no payloads. Enough to
answer "what happened to it", nothing that survives §9.4's argument against keeping content, and it
stops the identifier being reused under somebody's existing export.

### Correction 1 — colour is global, not per conversation

The spec assumed per-conversation colour. `Parley.Client.Rendering.Palette` already assigns from a
hash of the agent's name so an agent is one colour everywhere, and argues why position-derived colour
would be actively misleading. That is stronger than FR-017 asked for. `Palette` is kept; the spec's
assumption is what is wrong.

### Correction 2 — a conversation's name is its identifier

The spec speaks of a conversation's "name" as if it were a field beside the identifier. It is the
identifier: `001_initial.sql:342` records that the primary key is the name, constrained because it
reaches filenames and headings. No naming scheme is introduced and "sort by name" is an ordinal sort
on the id.

## Constitution Check

*GATE: passed before Phase 0, re-checked after Phase 1.*

| Principle | How this feature stands |
|---|---|
| **I — The specification is the product** | Four operations are added to `SPEC.md` §10.2 and one column to the §9 schema **before** any surface reads them. The archived state, the destroy consequence and the start operation are specified so another implementation could build them without this code. `tests/Parley.Spec.Tests` executes the new DDL as it does the rest. |
| **II — No agent has authority over another** | Nothing here is reachable by an agent. Q2 is decided *on* this principle: an archived conversation does not un-archive on a message, because that would be an agent reversing an operator's decision. |
| **III — Identity is fixed by the launch** | The whole content of Q1. The new start operation causes an adapter to launch and register itself under its own launch environment; no operation gains a sender parameter and no registration is created on anybody's behalf. |
| **IV — Terseness is a security control** | No new text crosses between agents. The tombstone holds no message content. The new operations carry identifiers and timestamps, and `conversations()` gains a boolean, not a field that could hold a paragraph. |
| **V — Invariants live in storage** | Destroying a conversation must leave no message of it: a foreign key with `ON DELETE CASCADE`, plus a test that reconstructs the defect. Archived is a column with the closure lifecycle left alone, so no check can be routed around by a repository method. |

**Boundaries**: agent and operator operations stay on different types and different gRPC services;
the four new ones are `Operators` only, and `SurfaceBoundaryTests` will fail if any reaches `Broker`.
No new field discloses more about a peer than an asker needs. `Parley.Core` gains no I/O. Neither
surface gains a route to a store — `GraphTests` walks both transitively.

**No violations to justify.** The Complexity Tracking section is therefore removed.

## Project Structure

### Documentation (this feature)

```text
specs/006-three-view-operator/
├── plan.md              # This file
├── research.md          # Phase 0 — R1 to R10
├── data-model.md        # Phase 1
├── quickstart.md        # Phase 1
├── contracts/
│   ├── operations.md    # extended from 005: which surface binds what
│   └── views.md         # the three views, what each shows and what it can do
├── checklists/
│   └── requirements.md
└── tasks.md             # /speckit-tasks, not created here
```

### Source Code (repository root)

```text
SPEC.md                          §10.2 gains archive, unarchive, destroy, start;
                                 §9 schema gains conversations.archived_utc and the
                                 destroyed_conversations tombstone; §14 gains the
                                 destroy consequence beside the purge one

proto/parley.proto               Operators service gains four rpcs; ConversationSummary
                                 gains archived; ListConversations gains include_archived

src/Parley.Store/
  Migrations/004_archive.sql     archived_utc, destroyed_conversations, the cascade
  ConversationRepository.cs      archive, unarchive, destroy, and the summary read

src/Parley.Broker/
  OperatorConsole.cs             the four operations; Broker.cs unchanged and must stay so

src/Parley.Server/
  OperatorsService.cs            the four rpcs
  BrokerRuntime.cs               start reaches the node that holds the role

src/Parley.Client/
  BrokerClient.cs                four client methods
  Rendering/Transcript.cs        line breaks, paragraphs, JSON laid out
  Rendering/LocalTime.cs         new — one way to write a local time, shared
  Rendering/Deep.cs              new — console routes built once, for F8
  Views/                         new — what each of the three views shows, derived
                                 from broker reads, with no toolkit in it

src/Parley.Cli/Top/              the three views replace the four regions;
                                 Shell.cs becomes a host for one view at a time
src/Parley.Web/Pages/            the same three views as pages

tests/Parley.Spec.Tests/         the new DDL executes; the new names do not span §10.1/§10.2
tests/Parley.Store.Tests/        destroy leaves nothing; archive survives a reopen
tests/Parley.Broker.Tests/       SurfaceBoundaryTests still fails on growth
tests/Parley.Client.Tests/       view derivations, local time, deep links, GraphTests
tests/Parley.Cli.Tests/Top/      the three views driven headless
tests/Parley.Web.Tests/          the same three as pages
```

**Structure Decision**: no new project. The view *derivations* — what a view shows, given what the
broker returned — go in `Parley.Client` beside seating and the transcript, because that is the split
004 made for exactly this reason and 005 proved: two surfaces that each derived "who is in this
conversation" separately disagreed about it twice in one week. What stays surface-specific is only
the drawing: Terminal.Gui views in `Parley.Cli/Top`, Razor pages in `Parley.Web/Pages`.

## Phase ordering

The order is a dependency order, not a preference. Each phase leaves the build green.

1. **Protocol.** `SPEC.md` §10.2, §9 schema, §14 consequence. `Parley.Spec.Tests` proves the DDL
   executes and the names do not span the boundary. Nothing in `src` yet.
2. **Store.** Migration 004, repository methods, the cascade and its reconstructed-defect test.
3. **Broker and wire.** `OperatorConsole`, the proto, `OperatorsService`, `BrokerClient`. The start
   operation reaches `NodesService.RequestStart`.
4. **Shared derivations.** `Parley.Client/Views`, `LocalTime`, `Deep`, and the transcript's
   readability work. Tested without either surface.
5. **Terminal.** Three views over those derivations. F8. The narrow-terminal rule holds throughout.
6. **Console.** The same three as pages. `DriftTests` extended so the two cannot part.
7. **Enrolment closes the loop.** Adding an agent calls start, and says it will.
8. **Review gate.** Three consecutive clean runs per lens, after the last edit.

## Risks, and what is done about each

| Risk | What is done |
|---|---|
| The terminal rewrite loses a 005 guard while moving code | The guards move as whole units and their tests move with them, unedited. A guard whose test needed changing to keep passing is the signal to stop. |
| `Transcript.Write` gains display concerns and the export starts lying | R8: local time is converted at the views, never in the writer. The export keeps UTC and keeps saying so. A test asserts the export contains no local time. |
| Destroy cascades further than intended | The migration is written before the repository method, and the test reconstructs the defect — a message of a destroyed conversation surviving, and a message of a *different* conversation not surviving. |
| Starting an agent from enrolment surprises somebody | The description that names the two files also names the process it is about to start. FR-023's door is unchanged. |
| The two surfaces drift while both are being rewritten | `DriftTests` runs from phase 3 onward, against operations that exist before either surface binds them. |
| A view truncates on a narrow terminal | The 005 rule holds: where something does not fit, the surface says so. Tested at the narrowest supported width for all three views. |

## What this plan does not decide

- The exact wording that replaces *catalogued, not yet registered* and *not in this catalogue*.
  FR-009 fixes the two facts to convey; the words are chosen with the owner during implementation,
  because the whole reason they are changing is that the last set was chosen without them.
- Whether the console's existing pages are re-routed or replaced. Both satisfy the contract; it is
  settled in `/speckit-tasks` against how much of the existing page furniture survives.
