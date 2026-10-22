# Implementation Plan: Roadmaps, and every agent in the Room

**Branch**: `019-layout` | **Date**: 2026-10-06 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `specs/026-roadmaps/spec.md`

## Summary

A roadmap is a first-level entity of a project: a person-confirmed, ordered list of items, each of which becomes its own conversation when work on it starts. An agent can only *propose* one, through a new dedicated agent operation. A person confirms, edits or declines it from any surface. Item state is never stored; it is derived by one shared function from the item's conversation, the wait-for graph and the registry. Starting an item is the person's ordinary line to an agent, sent through the seat in the item's reserved conversation. The Room is changed independently: it lists every agent in the project and marks participants.

The design leans on what is already here rather than adding mechanism (see `research.md`):

- The surfaces are clients of the broker, so no store is reached and every read and write is an operator operation (existing three-layer path).
- State derivation follows `Activities` and `WaitingOnYou` in `Parley.Client`: one derivation, two surfaces, no wire claim about state.
- Starting an item uses `SeatVoice.RequestAsync` unchanged. **No new "start" operation exists**; the broker only has to recognise the reserved conversation.
- Agents never *read* a roadmap. What reaches an agent is a request carrying one item's text, which already carries the message cap. This keeps Principle II and IV intact.

Version target: **0.42.0, protocol 0.9, schema 1 (edited in place)**. Nothing is released and the owner's rule is no compatibility.

## Technical Context

**Language/Version**: C# on .NET 10 (`net10.0`), as the rest of the repository  
**Primary Dependencies**: SQLite via Dapper (store), gRPC (transport), Terminal.Gui v2 (terminal), Razor Pages (web UI). No new package.  
**Storage**: SQLite schema 1, edited in place (`src/Parley.Store/Migrations/001_initial.sql`), mirrored in SPEC.md §9.2  
**Testing**: xUnit v3 on Microsoft.Testing.Platform. Run the host executables directly if `dotnet test` reports zero tests (CLAUDE.md).  
**Target Platform**: Windows, Linux, macOS, as today  
**Project Type**: multi-project .NET solution (protocol, broker, client, two surfaces, MCP adapter; the CLI adds nothing, per SPEC §10.2)  
**Performance Goals**: a roadmap view costs a fixed number of operator calls however many items it has: `Roadmap` (which carries each item's facts, see contracts), `DescribeWaiting` and `DescribeRegistry`. It never exports a conversation per item.  
**Constraints**: Core has no I/O; Client, Web and Terminal reach no store; agents' service gains exactly one operation; warnings are errors; every public member documented  
**Scale/Scope**: a roadmap of tens of items per project; hundreds at most. No caps (spec clarification 4)

## Constitution Check

*Gate: passed with two amendments to be made before code, as 020, 022 and 023 did.*

| Principle | Result |
|---|---|
| I. Specification is the product | **Pass, by ordering.** SPEC.md is written first (new §5.7 Roadmaps, §9.2 tables, §9.3 invariants, §10.1 and §10.2 operations, §18 entry for 0.9). `Parley.Spec.Tests` executes the new DDL. Code follows. |
| II. No agent has authority over another | **Pass, with a clarifying amendment (6.0.0).** A roadmap is a plan a *person* confirms, and it binds no agent. An agent proposes and the person decides. No agent operation reads, changes or marks done a roadmap (FR-012), and a structural test holds that. The amendment states: "a roadmap is the person's, never an agent's plan". The facilitator gains nothing (FR-013). |
| III. Identity fixed by launch | **Pass.** `ProposeRoadmap` takes no sender; the proposer is the attached caller. The person's act uses the seat, as today. |
| IV. Terseness is a security control | **Recorded reduction.** Spec clarification 4 sets no cap on roadmap text. Constitution amendment 6.0.0 records it as a reduction, argued like the ownership statement's: the text is read by a person, not an agent. What crosses to an agent is one item inside a Request, which keeps the 8,000 cap in storage. The character class that stops text breaking a surface's lines is **not** relaxed (`research.md` R9). |
| V. Invariants live in storage | **Pass.** Items belong to one roadmap, an item has at most one conversation, a reserved item conversation's first message must be the person's, a proposal is not a roadmap, and the proposer must be an attached non-person agent: all as CHECK, trigger, unique index or FK (`data-model.md`). Each gets a store test that is watched to fail with the rule removed. |
| Boundaries | **Pass.** Nothing crosses between agents except messages. A roadmap is not a context. |
| Quality gates | Review gate: three lenses, three consecutive clean runs each, run after the last edit, with the 019 to 025 combined gate still owed and ordered before this one is gated. |

**Complexity Tracking**: no unjustified violation. The two amendments are the cost.

## Project Structure

### Documentation (this feature)

```text
specs/026-roadmaps/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── operations.md
└── tasks.md            # /speckit-tasks, not created here
```

### Source Code (repository root)

```text
SPEC.md                              # §3 terms, §5.7 Roadmaps, §9.2 DDL, §9.3, §10.1 propose, §10.2 roadmap ops, §17, §18
.specify/memory/constitution.md      # 6.0.0 (II clarification, IV reduction), own commit first

proto/parley.proto                   # Agents.ProposeRoadmap; Operators roadmap reads/writes; Nodes untouched
src/Parley.Core                      # RoadmapId, RoadmapItemId, ItemConversation naming, proposal/roadmap/item records,
                                     #   IParleyBrokerClient + roadmap ops; IAcceptsOperatorInstructions untouched;
                                     #   the agent contract gains ProposeRoadmap (still no operator operation)
src/Parley.Store
├── Migrations/001_initial.sql       # roadmap_proposals, roadmap_proposal_items, roadmaps, roadmap_items,
│                                    #   roadmap_item_deps, reserved item conversations, roadmap_notices
├── RoadmapRepository.cs             # proposals, confirmation, edits, reads
└── (MessageRepository, ConversationRepository: reserved-name rule, "formerly" marker)
src/Parley.Broker
├── Broker.cs                        # agent: ProposeRoadmap (adds to §10.1 surface list)
└── OperatorConsole.cs               # Roadmaps, Roadmap, Proposals, ConfirmProposal, DeclineProposal, EditRoadmap
src/Parley.Server                    # AgentsService.ProposeRoadmap; OperatorsService roadmap methods
src/Parley.Mcp                       # tool parley_propose_roadmap; drain of roadmap notices in parley_inbox
src/Parley.Client
├── BrokerClient.cs                  # maps proto to domain
├── Roadmaps/ItemStates.cs           # THE derivation: state, blocker, next item, waiting-on-you
├── Roadmaps/RoadmapDraft.cs         # edit model both surfaces share
├── Rendering/RoomRoster.cs          # every agent in the project + participant mark (shared)
└── Seating/WaitingOnYou.cs          # extended: proposals and waiting items counted
src/Parley.Terminal                  # RoadmapsRegion (list), RoadmapRegion (one roadmap), Proposing dialog,
                                     #   RoomRegion roster change, menu entries via RegionAction
src/Parley.Web/Pages/Roadmaps/       # Index, Detail, Propose (review/confirm), item actions; _Conversation gains room roster
plugins/parley                       # skill text for the tool; PluginTests verb sets

tests/Parley.Spec.Tests              # DDL executes, new section references resolve
tests/Parley.Store.Tests             # each storage rule watched to fail
tests/Parley.Broker.Tests            # SurfaceBoundaryTests declared list; confirm/decline/edit lifecycle
tests/Parley.Core.Tests              # ContractSeparationTests; naming
tests/Parley.Server.Tests            # AgentsCannotChangeARoadmapTests; end to end over a socket
tests/Parley.Client.Tests            # ItemStates table of cases; RoomRoster
tests/Parley.Terminal.Tests          # driven views; DriftTests bindings; SurfaceTests door for edit/decline
tests/Parley.Web.Tests               # pages, no store on the graph
```

**Structure Decision**: no new project. Every piece lands in the project that already owns that kind of work. The derivation sits in `Parley.Client` so both surfaces share it and nothing in `Parley.Broker` learns about presentation. `GraphTests` stays valid.

## Phasing

Spec first, then the constitution, then code, as 020 and 022 did.

1. **Amend**: constitution 6.0.0 in its own commit; SPEC.md sections and §18 entry; `Parley.Spec.Tests` pass against the new DDL.
2. **Room (story 5)**: independent. `RoomRoster`, terminal and web wiring. Shippable alone.
3. **Storage and broker (stories 1 and 2 foundations)**: schema, repository, operations, the boundary tests.
4. **Derivation and read surfaces (story 2)**: `ItemStates`, roadmap views, project-level waiting count.
5. **Propose and confirm (story 1)**: agent tool, review screens, notices.
6. **Start and navigate (stories 3 and 4)**: start from item, links both ways.
7. **Command line, plugin, docs, quickstart walk, review gate.**

Phase 2 needs none of the rest, so it is built first and is the easiest to test in isolation. **It still ships with the rest, as one release**: the owner asked for both items to ship together.
