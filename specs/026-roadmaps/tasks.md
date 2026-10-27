---
description: "Tasks for roadmaps, and every agent in the Room (0.43.0, protocol 0.9)"
---

# Tasks: Roadmaps, and every agent in the Room

> Built beyond these tasks in the same PR, at 0.44.x: migration 002 (proposal ids never given again; the reserved-conversation trigger asks of exchanges, because retention removes messages; a copy of the store before it is upgraded), recorded in plan.md and data-model.md.

**Input**: `specs/026-roadmaps/` (spec.md, plan.md, research.md, data-model.md, contracts/operations.md, quickstart.md)
**Tests**: included. This repository's rules require a case for every storage invariant, watched to fail with the rule removed, and every boundary guard extended. They are part of each task's definition of done.
**Version**: `VersionPrefix` in `Directory.Build.props` goes to **0.43.0** and `ProtocolVersion` (`src/Parley.Core/ProtocolVersion.cs`) to **0.9**, in the change that alters shipped behaviour (CLAUDE.md). Both ship together, as one release.

> Schema 1 is frozen as this feature left it, and every later schema change is a migration (CLAUDE.md, SPEC.md §9.2). T011 edited `001_initial.sql` before that rule; no later task may.

**Conventions for every task below**: every public member documented, `<remarks>` carries why; `ConfigureAwait(false)` in library code; parameterised SQL; `TestContext.Current.CancellationToken` on awaited calls in tests; no `--` inside a `.props`/`.csproj` comment; use Edit/Write on C# and never a scripted regex; copy a file before any mutation loop and restore from the copy, never `git checkout` (uncommitted terminal work is in the tree). A mutation must compile to prove anything. Run the test host executables directly if `dotnet test` reports zero tests.

## Format: `- [ ] T### [P?] [US?] Description with file path`

`[P]` = different files, no dependency on an incomplete task.

---

## Phase 1: Specification first (blocks all code, Principle I)

The constitution is already at 6.0.0 (amended 2026-10-06, uncommitted at the time of writing). SPEC.md follows, then the code matches it.

- [X] T001 Commit the constitution amendment (`.specify/memory/constitution.md`, and the two standing-constraint paragraphs in `CLAUDE.md`) in its own commit, before anything else
- [X] T002 Add the terms *roadmap*, *roadmap item*, *proposal* and *item conversation* to SPEC.md §3 Terminology
- [X] T003 Write SPEC.md §5.7 Roadmaps (including FR-006b single-line text, FR-009a validation of intended agents and dependencies, FR-009b title-as-hint, FR-014a text changed since): what a roadmap is, that only a person confirms, edits and declines, the proposal template (title, verb-led items, `for`, `needs`), item states and their derivation (R4), the *next item* rule, starting an item as a person's request, item conversations and their reserved names, removal keeping the conversation (R7), and that an agent never reads a roadmap
- [X] T004 Add the new tables, indexes and triggers from `data-model.md` to SPEC.md §9.2 (DDL in the order it will execute) and the rules to §9.3 (each as a constraint, trigger or index)
- [X] T005 Add `ProposeRoadmap` and the roadmap notice to SPEC.md §10.1; the eight operator operations (`Roadmaps`, `Roadmap`, `Proposals`, `ProposalCount`, `Proposal`, `ConfirmProposal`, `DeclineProposal`, `EditRoadmap`) to §10.2, with the §10.2 note that no operator operation is reachable from the agent side
- [X] T006 Amend SPEC.md principle 2 text, §16 (the unbounded-proposal risk, stated as `agents.owns` was), §17 (a new open question: whether Parley should ever propose; and a note on proposal size evidence), and set §18's protocol version to 0.9
- [X] T007 Run `tests/Parley.Spec.Tests` and fix until every section reference resolves, every table is well formed, and the new DDL executes against SQLite in the order written; add one assertion that the DDL contains no `status` column on `roadmaps` or `roadmap_items` (state is derived, never stored)

**Checkpoint**: SPEC.md, constitution and plan agree. If code and spec disagree later, decide which is wrong and record it; do not silently change either.

---

## Phase 2: Foundational (blocks stories 1 to 4; story 5 does not need it)

### Core

- [X] T008 [P] Add `RoadmapNumber`, `ItemNumber` and `ItemConversation.Name(roadmapNumber, itemNumber)` (derived `rm<n>-<m>`, legal conversation id by construction) in `src/Parley.Core/Roadmaps/`, with the domain records (`RoadmapProposal`, `ProposedItem`, `Roadmap`, `RoadmapItem`, `RoadmapSummary`, `ProposalState`, `RoadmapOrigin`) and tests in `tests/Parley.Core.Tests/RoadmapNamingTests.cs` (every legal pair is a legal `ConversationName`; no two pairs collide)
- [X] T009 [P] Add the eight operator operations to `IParleyBrokerClient` in `src/Parley.Core/IParleyBrokerClient.cs` and the agent-side `ProposeRoadmap` to the agent contract; update `tests/Parley.Core.Tests/ContractSeparationTests.cs` (declared lists, no shared method names, no operator method on the agent contract)
- [X] T010 [P] Reuse `OwnershipStatement`'s character class for roadmap text: a `RoadmapText` validator in `src/Parley.Core/Roadmaps/RoadmapText.cs` that refuses runs breaking, hiding or reordering a surface's lines, and refuses a blank; **no length bound**; tests including U+2028, NUL and an RLO override

### Store

- [X] T011 Edit `src/Parley.Store/Migrations/001_initial.sql` in place: `roadmap_proposals`, `roadmap_proposal_items`, `roadmap_proposal_needs` (needs point only backwards), `roadmaps`, `roadmap_items` (never deleted; `removed_rev`; partial unique position among live items), `roadmap_item_needs`, `roadmap_item_conversations` (no FK to `conversations`), `roadmap_notices`; update the header counts; keep it identical to SPEC.md §9.2 (T004)
- [X] T012 Add the `messages` trigger from R5: the first message into a reserved item conversation must be from the person; and the trigger refusing a proposer that is the person or the facilitator. Both in `001_initial.sql` and SPEC.md §9.3
- [X] T013 `src/Parley.Store/RoadmapRepository.cs` (after T011 and T012): propose, read proposals, confirm (one transaction: roadmap or revision, items, reserved names, supersede rivals, write the notice), decline (writes the notice), edit with expected revision, reads. Provenance `proposed` versus `edited` is computed here by comparing final content to the stored proposal (R10), never passed in. Validate final content on confirm and edit (R4a, FR-009a): intended agents exist in the project, `needs` name live earlier items, single-line text (FR-006b); and return each live item's facts (data-model.md) from the roadmap read
- [X] T014 Store tests in `tests/Parley.Store.Tests/`: **one case per rule in data-model.md, and each watched to fail with its rule removed** (a CHECK written with `=` instead of `IS` is the known trap): decided_utc paired with state; needs point backwards; item belongs to one roadmap; position unique among live items; removed item keeps its link row; two proposals against one roadmap, confirming one supersedes the other; the reserved-name trigger (see T015); no `status` column; confirm refuses an unknown intended agent and a `needs` that is not live and earlier; edit refuses a reorder or removal that breaks a dependency; a proposal whose title matches an existing roadmap is flagged
- [X] T015 **Probe, do not reason**: in `tests/Parley.Store.Tests/ReservedItemConversationTests.cs`, an agent opens `rm1-1` before the person and must be refused; the person's first message is accepted; a later agent reply is accepted; a conversation not reserved is unaffected. Watch it fail with the trigger dropped
- [X] T016 Make `ConversationRepository.ListAsync` and the export carry the item reference (roadmap title, number, item number, and *formerly* when the item is removed) and `MessageRepository` unaffected; tests that a payload containing `rm1-1` or a roadmap's words makes no conversation look like an item's (FR-024)
- [X] T017 Include roadmaps, items, revisions and unresolved proposals in the project export, and extend retention (find the pruning code under `src/Parley.Store` first; SPEC.md §9.4 text) so a waiting proposal, a roadmap and its items are never pruned; decided proposals are pruned with the rest, and the roadmap keeps `origin_proposal` as text; destroying an item's conversation leaves the item reading *closed*, never *not started*; test all three

### Transport and broker

- [X] T018 `proto/parley.proto`: `Agents.ProposeRoadmap` (request, reply with proposal id or refusal) and the roadmap notice in the inbox reply; `Operators.Roadmaps`, `Roadmap`, `Proposals`, `ProposalCount`, `Proposal`, `ConfirmProposal`, `DeclineProposal`, `EditRoadmap`; item reference on `ConversationSummary`. Map enums so none reads as `UNSPECIFIED` (`TransportEnumTests`), and run the wire-against-document check
- [X] T019 `src/Parley.Broker/Broker.cs`: `ProposeRoadmap` (caller is the proposer; refuse an empty list, the person, the facilitator; record size). Update the declared surface in `tests/Parley.Broker.Tests/SurfaceBoundaryTests.cs`
- [X] T020 `src/Parley.Broker/OperatorConsole.cs`: the eight operations; update the declared list in `SurfaceBoundaryTests` (operations naming a roadmap also name the person)
- [X] T021 `src/Parley.Server/AgentsService.cs` and `OperatorsService.cs`: the handlers, each with its deadline and the existing `Console(...)` pattern
- [X] T022 **`tests/Parley.Server.Tests/AgentsCannotChangeARoadmapTests.cs`**: reflection over the generated `Agents` client and messages; fails on any member naming a roadmap beyond `ProposeRoadmap` and the notice. **Mutation-checked** by putting `EditRoadmap` on `Agents`
- [X] T023 `src/Parley.Client/BrokerClient.cs`: map every new operation to domain records, proto types staying inside the `Wire` namespace; `tests/Parley.Client.Tests` for the mapping
- [X] T024 End to end over a real socket in `tests/Parley.Server.Tests/RoadmapProposalTests.cs`: propose through `Agents`, confirm through `Operators`, nothing opens a store, an agent's attempts to change the roadmap are impossible, the notice reaches the proposer's inbox and wakes nothing

**Checkpoint**: a proposal can be made, confirmed, declined and edited over the wire, and every boundary guard is green.

---

## Phase 3: User Story 5 - Every agent in the Room (P2)

**Goal**: the Room lists every non-retired agent of the project, participants marked apart, ticks unchanged.
**Independent test**: quickstart section 1. **Needs no Phase 2 task**, and is built first for that reason; it ships with the rest.

- [X] T025 [P] [US5] `src/Parley.Client/Rendering/RoomRoster.cs`: `Build(export, registry, me)` returning participants first then the rest, each flagged `TookPart`; retired agents only if they took part, marked retired, never recipients; the person's seat and Parley kept apart and filtered **before** the join; `tests/Parley.Client.Tests/RoomRosterTests.cs` covering FR-001, FR-003, FR-004 and a project agent that joined later
- [X] T026 [US5] Terminal: replace the seated-only `others` list in `RoomRegion` and `Shell` (confirm their files under `src/Parley.Terminal`) with `RoomRoster`; headings *In this conversation* and *Elsewhere in the project*; the participant mark `◈` and a legend line; narrow rule (mark and name survive before status, via `RoomRegion.Fit`); ticking a non-participant addresses and brings them in
- [X] T027 [P] [US5] Web: `src/Parley.Web/Pages/Conversations/Detail.cshtml.cs` and `_Conversation.cshtml` use `RoomRoster` in place of `Visibility.Participants` plus `Roll`, so the two surfaces share the derivation (FR-005)
- [X] T028 [US5] Terminal tests in `tests/Parley.Terminal.Tests/RoomRosterOnScreenTests.cs` that read the **drawn rows** (0.37.7: a property the suite holds that the screen never shows) at 40 and 80 columns; ticking and sending to a non-participant delivers to them and moves them under the first heading; a retired non-participant is absent. Web test in `tests/Parley.Web.Tests`

**Checkpoint**: story 5 passes quickstart section 1.

---

## Phase 4: User Story 1 - A coordinated list becomes a roadmap (P1)

**Goal**: an agent proposes; the person reviews, edits, confirms or declines. **Independent test**: quickstart section 2.

- [X] T029 [P] [US1] `src/Parley.Mcp`: tool `parley_propose_roadmap` taking title and items (text, for, needs). Its description carries the template from `contracts/operations.md` and says a proposal creates nothing and assigns nobody; `parley_inbox` returns roadmap notices beside file notices; the channel nudge does not count them. Tests in `tests/Parley.Mcp.Tests` including that the tool takes no sender
- [X] T030 [P] [US1] The standing instructions every agent receives (`ParleyInstructions` (locate it first with a grep; it is not assumed to live in `src/Parley.Mcp`)) gain the template and the rule *items are one verb-led line; no background or narrative*; test that the text is delivered
- [X] T031 [P] [US1] `src/Parley.Client/Roadmaps/RoadmapDraft.cs`: the edit model both surfaces share (edit text, reorder, add, remove, set agent, set needs; validates through `RoadmapText`; computes whether it differs from the proposal) with tests
- [X] T032 [US1] Terminal proposals: a *Proposals* entry reached from the projects view and the room's waiting list (FR-008), a review dialog built on `Prompting` with edit, confirm and decline, declining behind a description and a deliberate act; actions go through `RegionAction` so the bar, status line and `?` get them; keys follow `NoTwoActionsShareAKeyTests`; new files hold the doors for `SurfaceTests`
- [X] T033 [P] [US1] Web proposals: `src/Parley.Web/Pages/Roadmaps/Proposals.cshtml(.cs)` and a review page with an edit form; GET describes, POST performs; links reachable from the project page (`EveryActionHasADoorTests`)
- [X] T034 [P] [US1] ~~CLI verbs~~ **Dropped, and recorded as a decision rather than an omission.** SPEC.md §10.2 keeps the command line to what a script or an installer reads, and 019 moved every operator operation to the terminal on the owner's call. Roadmaps are a person's decisions, so they are the terminal's and the web UI's. FR-028 and `contracts/operations.md` amended to say so
- [X] T035 [US1] Project-level count: proposals waiting are counted with work waiting on the person (FR-008, FR-017), extending `src/Parley.Client/Seating/WaitingOnYou.cs` and the projects row on both surfaces
- [X] T036 [US1] Tests (the DriftTests bindings of `RoadmapAsync` and `EditRoadmapAsync` follow with T039, T040 and T053; the other four operations are bound on both surfaces already): a proposal naming an existing title is shown with the match and can be confirmed as a revision or as new (FR-009b); terminal driven review, confirm and decline end to end against a loopback broker; web page tests with no broker listening for the guards that apply before anything is sent; `DriftTests` bindings for the new operations on both surfaces

**Checkpoint**: story 1 passes quickstart section 2.

---

## Phase 5: User Story 2 - See state and what is next (P1)

**Goal**: roadmap views showing derived state and the next item. **Independent test**: quickstart section 3.

- [X] T037 [US2] `src/Parley.Client/Roadmaps/ItemStates.cs`: the one derivation of R4, signature `Of(roadmap, waiting, me)` taking item facts from `Roadmap` with no per-item export (analysis finding C1), also reporting *text changed since* (FR-014a) (not started, in progress, waiting on an agent, waiting on the person, blocked with its blocker, done, closed by the person, could not be read) and the next-item rule with dependencies; no state stored anywhere
  - *Text changed since* is the broker's fact `text_edited_since_started`, from the item's own edit record, never a comparison of words: the person may change the words in the box before sending.
- [X] T038 [P] [US2] `tests/Parley.Client.Tests/ItemStatesTests.cs`: a table of cases, one per state and per Acceptance Scenario of story 2; a read failure is *could not be read* and **never** *not started* (FR-018); dependencies hold a later item back; complete roadmap names no next item; mutation-check the derivation by changing one state's rule
- [X] T039 [US2] Terminal: a roadmaps list (reached from the projects view) and a roadmap view (items, state, holder, blocker, a *next* mark, one key to jump to next for long roadmaps); built on `Lists`, plain-text marks that survive colour being stripped, narrow-terminal rule honoured; the read is contained so a failure says so and keeps the last view
  - Built as `PlansRegion` and `PlanView`. The view starts on the next item, or on the item whose conversation it was reached from, and Alt+n returns to the next item (`PlansTests`).
- [X] T040 [P] [US2] Web: `src/Parley.Web/Pages/Roadmaps/Index` and `Detail`, rendering `ItemStates`; no script needed for correctness
  - Built as `Roadmaps/Index` and `Roadmaps/Roadmap`; `item=n` opens that item's row with no script.
- [X] T041 [P] [US2] ~~CLI~~ Dropped with T034: the command line adds no roadmap verb
- [X] T042 [US2] Count items waiting on the person into `WaitingOnYou` and the projects row (FR-017); tests
  - An item waiting on the person is a question in its conversation, so the inbox counts it with every other question the person owes (`WaitingOnYou`); proposals are counted by `ProposalCount` on both projects rows.
- [X] T043 [US2] Tests: a roadmap view makes a fixed number of operator calls whatever its length (count them against a scripted broker with 5 and 50 items); a surface reaching a broker without roadmap support says so and acts as no one (FR-014b); two key presses reach an item's conversation and two return (SC-003); terminal drawn-screen tests at two widths and with the broker unreachable; web render tests; the CLI output
  - Covered by `PlansTests`, `RoadmapOnThePageTests` and `RoadmapCallCountTests`. FR-014b dropped (see spec.md), and the CLI adds nothing (T034).

**Checkpoint**: story 2 passes quickstart section 3.

---

## Phase 6: User Story 3 - Instruct agents from the roadmap (P1)

**Goal**: start an item, sending the person's request into the item's conversation. **Independent test**: quickstart section 4 steps 1 to 3 and 7.

- [X] T044 [US3] `src/Parley.Client/Roadmaps/StartItem.cs`: resolves the agents (the item's intended agent, or the person's choice from `RoomRoster`'s project list), builds the ask from the item's text plus optional words, checks the 8,000 cap before sending and names it, refuses with a sentence when the item is already in progress unless a further line is chosen (FR-021), and sends through `SeatVoice.RequestAsync` with the reserved conversation id. Tests: one request per agent, cap refusal sends nothing, already-in-progress sends nothing
  - **Replaced by the design SPEC.md §5.7 settled on**: starting an item opens its conversation with the item's words in the box and its own agent ticked, and the person's send is the start. No `StartItem` type exists. Ticking only the item's own agent, not every agent the roadmap names, was fixed on 2026-10-24.
- [X] T045 [US3] Terminal: a *Start* action on an item that describes what will be sent and to whom first (a deliberate act), offers the project's agents when none is intended, and uses the held seat; without a seat it says a request is sent as somebody and asks for one (0.28.2), and the door lives in one file for `SurfaceTests`
  - Replaced as T044: Enter on an item in `PlanView` opens its conversation through the held seat; nothing is sent until the person sends.
- [X] T046 [P] [US3] Web: a start form on the roadmap page, GET describes, POST sends, and a missing seat is said plainly rather than falling through to the operator's voice
  - Replaced as T044: the roadmap page links each item to its conversation with `With`, and the conversation page fills the box with the item's words from its reservation; following the link sends nothing.
- [X] T047 [US3] The start is the **person's words in the transcript**, not an operator instruction: a test that sending an item adds a message in the transcript from `human-user` and no row to `instructions`
  - Held by `RoadmapOverTheWireTests.Starting_an_item_is_the_persons_request_into_the_reserved_conversation`: the agent collects an ordinary request from the person.
- [X] T048 [US3] End to end test over a socket, including that roadmaps survive a broker restart (SC-007): start an item for an unstarted item, assert the conversation now exists and equals the reserved name, the agent holds one request, the state reads in progress
  - The same test over a socket; surviving a restart is `RoadmapSurvivesARestartTests` on a file store.

**Checkpoint**: story 3 passes quickstart section 4.

---

## Phase 7: User Story 4 - Item conversations navigable both ways (P2)

**Goal**: roadmap to item conversation and back. **Independent test**: quickstart section 4 steps 4 to 6.

- [X] T049 [US4] Terminal: Enter on a started item opens its conversation; on an unstarted item it shows the text and offers to start (no conversation exists, per clarification 3); in a conversation, an action *Roadmap* opens the roadmap on that item, and says so plainly for an ordinary conversation
  - Enter opens the item's conversation; in it, Alt+o opens the roadmap on that item, offered only for a live item's conversation.
- [X] T050 [P] [US4] Web: item links to `Conversations/Detail`, and `Detail` shows a line naming the item with a link back; conversation lists mark item conversations (FR-026)
  - Conversation pages name their item and link back to the roadmap opened on it; the project's conversation list tags item conversations.
- [X] T051 [P] [US4] The conversation list on both surfaces and `parley` carries the item reference from `ConversationSummary`; a removed item's conversation says *formerly* (FR-023a)
  - Both surfaces read the item from the broker's reservation (`ItemWording.Belongs`); a removed item's conversation says *formerly*.
- [X] T052 [US4] Tests: round trip lands on the same item; removing an item through `EditRoadmap` leaves the conversation intact and marked formerly; a conversation that merely mentions a roadmap in a payload gets no link
  - `PlansTests` and `RoadmapOnThePageTests`: the round trip, *formerly*, and a conversation named like an item that no item reserved.
- [X] T053 [US4] Editing: `EditRoadmap` on both surfaces, refused when the expected revision is stale; removing an item whose conversation has an open exchange is described first; `SurfaceTests` holds `EditRoadmap` and `DeclineProposal` each in exactly one file per surface carrying its door
  - Built on both surfaces. Removing an item whose work is open is described first (`Removal`) and saved only on a second, deliberate choice.

**Checkpoint**: story 4 passes quickstart section 4 steps 4 to 6.

---

## Phase 8: Polish and release

- [X] T054 [P] README.md: a section on roadmaps that stands alone (no spec section references) saying how an agent learns to propose, with the template; the Room's new rows
  - `docs/operating.md` carries roadmaps; the README links it.
- [X] T055 [P] `plugins/parley`: the operator skill lists the new verbs and says a roadmap is the person's; `PluginTests` equal sets
  - The operator skill lists where roadmaps are decided, read and started, and that a roadmap is the person's. No verb was added, so `PluginTests` is unchanged.
- [X] T056 (if any earlier phase is committed alone, that commit carries the bump) Bump `VersionPrefix` to 0.43.0 and `ProtocolVersion` to 0.9; run the full build (warnings are errors), then every test host executable directly
  - 0.43.0 and protocol 0.9.
- [X] T057 Run `GraphTests`: Client, Web, Terminal and Setup still reach no store; `DeadSurfaceTests` clean (nothing public used only by itself)
  - Run with the full suite.
- [X] T058 Hygiene: case-insensitive `git grep` for the originating project's names; `git status` read by name before any `add`, since the terminal edits and the staged deletions in the tree are not this feature's
  - Run before committing.
- [X] T059 Walk `quickstart.md` against packed tools in a scratch root with every `PARLEY_` variable cleared; record each check in `REVIEW-LEDGER.md` (untracked)
  - Walked on 2026-10-24 with the 0.43.0 packages, four `parley-mcp` agents over stdio and the web console as the person: 40 checks, all passing. Steps 1.4, 1.5 and 2.5 rest on `RoomRosterTests`, `RoomActivityTests` and the structural tests. Steps 4.1, 4.3 and 4.4 were reworded to the design SPEC.md §5.7 settled on.
- [X] T060 Update the `CLAUDE.md` plan entry from *planned* to *built*, with what building it found
  - CLAUDE.md's State names 026 as merged and states the store rule that replaced "nuke and rebuild".
- [ ] T061 **Review gate**, owed and ordered after the combined 019 to 025 gate. Three lenses (correctness and concurrency; security and data integrity; API surface and maintainability), three consecutive clean runs each, run after the last edit. Probes owed by the plan: an agent opening a reserved item conversation; an agent calling anything on a roadmap; a payload shaped like an item reference; a proposal with line-breaking characters; two confirmations racing on one roadmap; the Room's drawn rows after the filter

---

## Dependencies and order

```
Phase 1 (spec) ──► Phase 2 (foundations) ──► US1 ──► US2 ──► US3 ──► US4 ──► Polish
                └► Phase 3 (US5 Room) ─────────────────────────────────────┘
```

- Phase 1 blocks everything: code matches the spec before review, and nothing is written ahead of it.
- Phase 2 blocks stories 1 to 4. **Phase 3 (US5) needs none of Phase 2** and can run alongside it.
- US2 needs US1's roadmaps to exist; US3 needs US2's derivation (to say in progress) and the reserved names from Phase 2; US4 needs US3 (a started conversation to navigate to).
- Within a phase, `[P]` tasks touch different files and run together.

## Parallel examples

- **Phase 2**: T008, T009 and T010 together; then T013 beside T018; T022 only after T018 to T021.
- **Phase 3 with Phase 2**: T025 and T027 touch different projects than any Phase 2 task, so US5 can proceed on a second track.
- **Phase 4**: T029, T030, T031, T033, T034 together; T032 and T035 after T031.
- **Phase 5**: T038 beside T037 (test first against a stub), then T039, T040, T041 together.

## Implementation strategy

1. **MVP**: Phases 1 and 2, then US1 and US2: an agent proposes, the person confirms, and the person can see state and what is next. That alone stops lists vanishing into transcripts, which the owner named as the key outcome.
2. Add US3 so the roadmap drives work, then US4 for navigation.
3. US5 rides alongside and is the quickest to test in isolation.
4. **All of it ships together as one release** (owner's call), so nothing is tagged or installed until Phase 8 is done and the quickstart walked.
5. The probes in T015, T022 and T061 are the point of the exercise: each is written as the malicious case and watched to be refused. Do not reason them.

## Counts

61 tasks. Per phase: Phase 1 seven, Phase 2 seventeen, US5 four, US1 eight, US2 seven, US3 five, US4 five, Polish eight.
