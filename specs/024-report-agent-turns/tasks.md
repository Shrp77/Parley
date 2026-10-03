# Tasks: Report agent turns, so working and stopped are told apart

**Input**: Design documents from `specs/024-report-agent-turns/`
**Prerequisites**: plan.md, spec.md, research.md (R1–R11), data-model.md, contracts/turns.md, quickstart.md

**Tests**: included. This repository's rule is that a new invariant is verified by reconstructing the defect and watching
the check fail, and every probe in contracts/turns.md is written as the malicious case first.

**Branch**: `019-layout`. **Version**: bump `VersionPrefix` in `Directory.Build.props` at the first commit that changes
`src` (CLAUDE.md "Versioning"), with a line in CLAUDE.md's 024 pointer — the version test reads it.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (different files, no dependency on an unfinished task)
- **[Story]**: US1 see stopped and why · US2 told at once · US3 both surfaces agree

---

## Phase 1: Setup — SPEC.md first (Principle I, FR-015)

**Purpose**: the protocol change is a specification change before it is code.

- [X] T001 Amend SPEC.md §11/§12's node contract: a node that runs an agent reports each turn's start and end to the
  broker, with the facts in contracts/turns.md; a failed report never fails a turn; reports are accepted only for agents
  on the reporting node's machine (research R10); in SPEC.md
- [X] T002 Amend SPEC.md §10.2's registry read: each agent's turn standing (`turns_reported`, running since, last ended
  turn's end, clean, permission refusals) on the operator's read and **never** on the agent roster (FR-005); in SPEC.md
- [X] T003 Amend SPEC.md §6's flags table with the permission-refused flag — raised at once when a turn ends owing work
  with at least one refusal, cleared by derivation (research R6) — and say why a turn ending owing work *without*
  refusals is not flagged early; in SPEC.md
- [X] T004 Close SPEC.md §17 question 1's gap: the turn record was promised for every turn and the node path wrote none;
  record the decision (research R11) and that this feature makes it true; in SPEC.md
- [X] T005 Add SPEC.md §18's entry under the **current** protocol version as an addition to the node and operator
  contracts, saying why the version does not move (research R9: agents see nothing, and moving it would force every
  adapter to upgrade); in SPEC.md
- [X] T006 Run `tests/Parley.Spec.Tests` and fix any reference, table or schema fragment the amendments broke

**Checkpoint**: SPEC.md describes the feature; spec tests green.

---

## Phase 2: Foundational — wire, core, broker, node (blocks every story)

### Wire and core

- [ ] T007 Add `JoinRequest.reports_turns`, `rpc ReportTurn(TurnReport)` with `TurnStarted`/`TurnEnded`, the five
  registry-row fields and `FlagKind.PERMISSION_REFUSED`, exactly as contracts/turns.md, in proto/parley.proto
- [ ] T008 [P] Add the turn standing to `AgentRecord` (`TurnsReported`, `TurnRunningSince`, `LastTurnEndedUtc`,
  `LastTurnClean`, `LastTurnPermissionDenials`), documented with *why* in remarks, in src/Parley.Core/AgentRecord.cs
- [ ] T009 [P] Add `FlagKind.PermissionRefused` with its remarks, in src/Parley.Core/FlagReport.cs
- [ ] T010 [P] Test that no message the `Agents` service returns carries any turn field — reflect over the generated
  `ListAgentsReply` and every Agents reply type — modelled on `AgentsCannotSetTheirOwnLineTests`, in
  tests/Parley.Server.Tests/AgentsCannotSeeTurnsTests.cs

### Broker (probes first)

- [ ] T011 Write the probes before the code, each watched to fail: a report for an agent on another machine is ignored;
  a report from a node not joined is ignored; a report for an unknown agent stores nothing; an end older than a newer
  start does not clear it; a node's stream ending clears its running turns; an ended turn writes one `invocations` row
  with broker-computed waiting counts; **an end whose start the broker never saw (it restarted mid-turn) is still
  recorded, and nothing is shown as running** (spec edge case, `/speckit-analyze` C1) — in
  tests/Parley.Server.Tests/TurnReportingTests.cs
- [ ] T012 Add the running-turn table to the broker runtime (agent → start, machine), cleared when a node's join stream
  ends (research R3), in src/Parley.Server/BrokerRuntime.cs
- [ ] T013 Implement `ReportTurn` with research R10's guard: start sets the running turn; end clears it if not newer and
  records `invocations` via `InvocationRepository.RecordAsync` with waiting counts read from the store at start and end
  (research R2), in src/Parley.Server/NodesService.cs
- [ ] T014 Record `reports_turns` per joined node and expose it per agent, in src/Parley.Server/NodesService.cs and
  src/Parley.Server/BrokerRuntime.cs
- [ ] T015 Add a read of each agent's most recent ended turn, in src/Parley.Store/InvocationRepository.cs, with a store
  test in tests/Parley.Store.Tests/LatestTurnTests.cs
- [ ] T016 Fill the registry row's turn fields from T012–T015, in src/Parley.Server/OperatorsService.cs and
  src/Parley.Server/Wire.cs
- [ ] T017 Map the registry turn fields into `AgentRecord`, in src/Parley.Client/BrokerClient.cs

### Node

- [ ] T018 Join with `reports_turns = true`; report `TurnStarted` before and `TurnEnded` after both turn sites
  (`GiveTurnAsync` and the instruction path, research R1), mapping `TurnOutcome`; a report that fails is logged and never
  fails the turn — in src/Parley.Cli/NodeCommand.cs
- [ ] T019 Test over a real loopback broker that a node's turn appears as running and then as an ended turn with its
  permission refusals, in tests/Parley.Cli.Tests/NodeReportsTurnsTests.cs

**Checkpoint**: the broker knows every turn the node runs; nothing on screen has changed yet.

---

## Phase 3: User Story 1 — See that an agent has stopped, and why (P1) 🎯 MVP

**Goal**: the room shows working only while a turn runs, and stopped — with permission refusals — when one ended owing.

**Independent Test**: an agent's turn ends silently with a refused permission while owing work; the room says stopped
and "1 permission refused", never working.

- [ ] T020 [US1] Move `Activities`, `Doing` and `Activity` from src/Parley.Terminal/Activity.cs to
  src/Parley.Client/Views/Activities.cs (research R8), keeping 0.37.5's tests passing from their new home
- [ ] T021 [US1] Write the derivation tests first: working only with a turn running; waiting-on when the agent is the
  waiter (research R7); stopped when the last turn ended after the pickup and work is still owed; refusals named; unknown
  when turns are not reported (research R5), in tests/Parley.Client.Tests/ActivitiesTests.cs
- [ ] T022 [US1] Extend `Activities.Of` with `Waiting`, `Stopped` and `Unknown` from the registry's turn standing and the
  wait-for graph, in src/Parley.Client/Views/Activities.cs
- [ ] T023 [US1] Add the stopped mark, its words ("stopped 2h48m — 1 permission refused"), waiting-on, and the legend
  line, per contracts/turns.md, in src/Parley.Terminal/RoomRegion.cs; split long rows as 0.37.8 does
- [ ] T024 [US1] Room tests: the stopped row and its refusals, a waiting row, no working claim for an unknown agent, and
  the legend, in tests/Parley.Terminal.Tests/RoomActivityTests.cs

**Checkpoint**: US1 complete — the owner's three hours would have read "stopped" within one refresh.

---

## Phase 4: User Story 2 — Be told at once (P2)

**Goal**: a turn that ends owing work with a refused permission is flagged immediately.

**Independent Test**: such a turn ends; the flags list shows it at once; it clears when the agent answers.

- [ ] T025 [US2] Write the flag tests first: raised at once with refusals; not raised early without refusals; cleared when
  the agent answers or the exchange closes, in tests/Parley.Store.Tests/PermissionRefusedFlagTests.cs
- [ ] T026 [US2] Derive `FlagKind.PermissionRefused` per research R6, in src/Parley.Store/FlagRepository.cs
- [ ] T027 [US2] Map it on the wire both ways, in src/Parley.Server/Wire.cs and src/Parley.Client/BrokerClient.cs
- [ ] T028 [P] [US2] Show it in the terminal's Health dialog with the agent, the conversation and the refusals, in
  src/Parley.Terminal/Health.cs
- [ ] T029 [P] [US2] Show it on the web UI's health page, in src/Parley.Web/Pages/Projects/Health.cshtml(.cs)
- [ ] T030 [P] [US2] Word it in `parley flags`, in src/Parley.Cli/OperatorCommands.cs

**Checkpoint**: US2 complete.

---

## Phase 5: User Story 3 — Both surfaces agree (P3)

- [ ] T031 [US3] Show each participant's activity on the conversation page from the same `Activities` and words,
  **inside the followed region** so it refreshes with the transcript every few seconds — the participants list in
  Detail.cshtml today sits outside `_Followed` and would show the state at page load for ever (`/speckit-analyze` U2) —
  in src/Parley.Web/Pages/Conversations/_Followed.cshtml and Detail.cshtml.cs (the follow handler reads the registry and
  the wait-for graph as the page does)
- [ ] T032 [US3] Test that a stopped agent with refusals reads identically on both surfaces, in
  tests/Parley.Web.Tests/ActivityOnTheWebTests.cs

---

## Phase 6: Polish

- [ ] T033 [P] README: what working, waiting, stopped and unknown mean, and where the flag appears, in README.md
- [ ] T034 [P] CLAUDE.md: move 024 from queued to latest with what building it found, in CLAUDE.md
- [ ] T035 Walk quickstart.md against packed tools in a scratch root, recording each step's result
- [ ] T036 Hygiene grep (sibling names, hyphenated and spaced forms, machine name; untracked files included) on its own,
  before each commit
- [ ] T037 Point `.specify/feature.json` back at specs/023-always-a-seat to resume 023's phases 5–8
- [ ] T038 Ask the owner whether to run the review gate for 024 alone or fold it into the combined 019–024 gate

---

## Dependencies & Execution Order

- **Phase 1** blocks everything: SPEC.md first.
- **Phase 2** blocks every story. Within it: T007 before T008–T017; T011's probes before T012–T014; T018 after T013.
- **US1 (Phase 3)** needs Phase 2. **US2 (Phase 4)** needs T007, T009 and T015 only, so it can run beside US1.
  **US3 (Phase 5)** needs T020–T022.
- **Polish** last.

### Parallel opportunities

- T008, T009, T010 together after T007.
- T028, T029, T030 together after T027.
- US1 and US2 side by side once Phase 2 is done.

## Implementation Strategy

**MVP is Phases 1–3**: SPEC.md, the reporting, and the room telling working from stopped. That alone fixes what the owner
met. US2 makes it reach the person without looking; US3 makes the web UI agree. Commit at each checkpoint, with the
hygiene grep run on its own first.
