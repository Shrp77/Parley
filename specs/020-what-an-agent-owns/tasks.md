# Tasks: What an agent owns, asked rather than assumed

**Input**: Design documents from `specs/020-what-an-agent-owns/`
**Prerequisites**: plan.md, spec.md, research.md (R1–R7), data-model.md, contracts/asking.md,
contracts/refusals.md, contracts/operations.md, quickstart.md
**Constitution**: **3.0.0**, amended on 2026-09-26 in its own commit *before* this list existed. Principle
IV's finding is discharged and nothing here is blocked by it.

**Tests**: included, and not optional. The riskiest thing in this feature is a migration that **cannot
roll back**, and the second riskiest is a guarantee — *no line reaches the registry a person did not
accept* — that is only worth what the probe of it is worth. **Every guard task below names what it must
fail against before it is believed**, because a probe that cannot fail is the defect this repository has
recorded more often than any other.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: different files, no dependency on anything incomplete — safe to run together
- **[Story]**: the user story it serves
- Paths are repository-relative

---

## The five rules this feature is most likely to break

Read these before starting. Each fails the build rather than being a convention to remember.

1. **The migration runs outside a transaction and cannot roll back.** A failure part-way leaves the
   store with no `agents` table. Copy the store beside itself first, every time, including in tests.
2. **`Parley.Spec.Tests` executes every schema fragment in SPEC.md, in the order written.** The
   document's schema and the migration move in the **same task** or the build goes red.
3. **`GraphTests`** walks project references transitively: `Parley.Client`, `Parley.Terminal` and
   `Parley.Web` reach no store, and `Parley.Core` has no I/O and no package dependencies.
4. **`SurfaceTests`** allows a destructive verb in exactly one file, which must carry its door;
   **`DriftTests`** compares every `BrokerClient` operation against both surface trees with no
   exemption; **`DeadSurfaceTests`** reports a constant named only in its own file.
5. **No surface may ever print the integer that means *no cap*.** 0.25.8's precedent: printing
   `2147483647` tells somebody a bound was set at a value nobody chose.

---

## Phase 1: Setup

**Purpose**: the version, so nothing later has to remember it.

- [ ] T001 Confirm `.specify/feature.json` names `specs/020-what-an-agent-owns` and the working branch is `019-layout` (this repository builds features on the working branch; the name is cosmetic)
- [ ] T002 Bump `VersionPrefix` to `0.33.0` in `Directory.Build.props`, **in the same commit as T005's migration** rather than at the end — the first shipped behaviour, and the rule the build cannot catch

---

## Phase 2: Foundational — the storage, and the one thing that cannot be undone

**Purpose**: the bound comes off. **No user story can start until this phase is done**, and nothing in
this phase is parallel with anything else in it.

**Do this phase alone, on a clean tree, and commit it alone.** It is the only work in 020 that can
destroy a store.

- [ ] T003 Add an opt-out to `src/Parley.Store/SchemaMigrator.cs` so **one named migration** may run outside the wrapping transaction. Keyed on the migration's own file name, not a general flag: `PRAGMA foreign_keys` is a no-op inside a transaction (probed, `research.md` R1), and a general capability would let the next migration lose its rollback by accident. `user_version` is bumped by the same path either way
- [ ] T004 Add `tests/Parley.Store.Tests/MigratorOptOutTests.cs`: a transactional migration that throws leaves the store untouched, and the opt-out migration is the **only** one the migrator will run unwrapped. **Must fail against**: the opt-out applied by a boolean any migration can set
- [ ] T005 Write `src/Parley.Store/Migrations/012_ownership_is_asked.sql` — the rebuild of `agents` with `length(owns) <= 2147483647`. **The character class of FR-022 is copied from `001_initial.sql` verbatim and never retyped**: it was derived by sweeping the whole of Unicode against `OwnershipStatement`, and a hand-typed class is a class that has drifted. Header records, at length, that this is the first migration here to run outside a transaction and what that costs
- [ ] T006 In the same migration, take down `tr_agent_declares_its_machine` (on `agents`), `tr_result_is_well_formed` (on `messages`) and `tr_placer_is_registered` (on `shared_files`) before the rebuild and restore all three **verbatim** afterwards. The RENAME validates every trigger in the schema; with them standing it fails outright with `error in trigger tr_result_is_well_formed: no such table: main.agents` (probed). `tr_result_is_well_formed` is now written in three places and 011 left it alone deliberately — reproduce, do not improve
- [ ] T007 In the same migration, copy rows by **named columns**. `role` and `machine` are `GENERATED ALWAYS … STORED` and cannot be inserted; `SELECT *` fails
- [ ] T008 Add `src/Parley.Store/Migrations/012_ownership_is_asked.sql`'s second half: `CREATE TABLE agent_ownership_accepted` per `data-model.md`, with its three CHECKs and `ON DELETE CASCADE`
- [ ] T009 Add `tests/Parley.Store.Tests/OwnershipBoundMigrationTests.cs`, building a **schema-11 store with traffic** — rows in `invocations`, `exchanges` and `messages`, and an agent carrying a line — *before* upgrading. **Must fail against an empty fixture being used instead**: 006 records that this rebuild succeeds on every empty fixture and fails on every store that has ever carried traffic, and six of `agents`' eleven children refuse a cascade
- [ ] T010 In that suite, assert the post-migration state: `PRAGMA foreign_key_check` returns **no rows**, `PRAGMA integrity_check` returns `ok`, **fourteen** triggers are present, `role` and `machine` still generate from `name`, every child row survived, and **every pre-existing `owns` value is byte-identical**
- [ ] T011 In that suite, assert the rule did not move when the number did: 400 characters accepted; U+202E, U+2028, U+00AD, U+FEFF, a tag character from U+E0020–U+E007F and an embedded NUL each **refused**. **Must fail against** the character class being retyped rather than copied — check it by running `OwnershipTests`' Unicode sweep against the rebuilt table
- [ ] T012 Change `OwnershipStatement.MaxCharacters` in `src/Parley.Core/OwnershipStatement.cs` to the named sentinel (`Unbounded`, matching `ExchangePolicy.Unbounded`'s shape), keep the rule, and rewrite the `<remarks>` to carry the amended reasoning rather than the measured hundred. Core still does no I/O and takes no package dependency
- [ ] T013 [P] Add `tests/Parley.Core.Tests/OwnershipSentinelTests.cs`: the sentinel and the schema's ceiling are the same number, and `Validate` admits a 400-character statement while refusing every character in T011. **Must fail against** the schema and the constant drifting apart
- [ ] T014 Amend SPEC.md: §17 question 10 (FR-027 — a draft may come from the agent, on what conditions, at what cost, including that the claim narrows again and anchoring worsens), §18's 0.7 entry (FR-028, recording that 0.7 was unreleased so this folds in), and **the document's schema fragment for `agents`**. `Parley.Spec.Tests` executes every fragment in order, so this moves **with** T005 or the build is red

**Checkpoint**: the store is at 12, `dotnet build` is green, every suite passes, and the bound is off with the character rule intact.

---

## Phase 3 (US1, P1): Ask an agent what it owns

**Goal**: an operator asks, reads the answer attributed to the agent, and accepts or does not.
**Independent test**: one enrolled running agent — ask, read, accept, restart, see the line in the roster.

### The ask, once, for both surfaces

- [ ] T015 [US1] Add `src/Parley.Client/OwnershipAsk.cs`: the exact text of `contracts/asking.md`, and the conversation identifier rule `owns-<role>-<yyyyMMdd-HHmmss>`. **One implementation** — an answer given to one surface would have been given to the other
- [ ] T016 [P] [US1] Add `tests/Parley.Client.Tests/TheAskSaysWhatItIsForTests.cs`: one case per row of `contracts/asking.md`'s mapping table — the line is read by every agent on every roster listing (FR-003), nothing is routed by it (FR-030), both numbered halves (FR-004), naming a peer, and that nothing the agent says changes configuration (Principle III). **Must fail against** any passage being dropped; assert on the passages, not on the length
- [ ] T017 [P] [US1] Add `tests/Parley.Client.Tests/TheAskCarriesNoMarkerTests.cs`: the Request payload contains nothing a surface parses — no identifier, no sigil, no key. **Must fail against** a marker being added to the payload; mutation-check by adding one and watching it go red
- [ ] T018 [US1] Add `src/Parley.Client/OwnershipProposal.cs`: the proposal is the **latest** message from the agent in that exchange (0.30.1's rule), and the pending comparison of `agents.owns` against the accepted statement
- [ ] T019 [P] [US1] Add `tests/Parley.Client.Tests/TheProposalIsTheLatestThingSaidTests.cs`. **Must fail against** taking the first message: seed an exchange with a proposal, a clarify and a revision

### The operation

- [ ] T020 [US1] Add `AcceptOwnership` to `proto/parley.proto` on the **`Operators`** service, per `contracts/operations.md`
- [ ] T021 [US1] Add it to `IParleyBrokerClient` in `src/Parley.Core/` (§10.2's contract) and **not** to `IAcceptsOperatorInstructions`
- [ ] T022 [US1] Implement it on `src/Parley.Broker/OperatorConsole.cs` — **not** on `Broker`. `SurfaceBoundaryTests` enumerates the broker's public surface and fails when it grows, which is what makes this a decision rather than a habit
- [ ] T023 [US1] Add the repository write and the pending read to `src/Parley.Store/AgentRepository.cs`: write `agent_ownership_accepted`, and return `agents.owns` beside it so a surface can compare without reaching a store
- [ ] T024 [US1] Bind it in `src/Parley.Server/OperatorsService.cs`
- [ ] T025 [P] [US1] Add `tests/Parley.Broker.Tests/AcceptOwnershipTests.cs`: it records the acceptance and **does not write `agents.owns`**. **Must fail against** an implementation that writes the registry field — that is the whole of Principle III here
- [ ] T026 [P] [US1] Add `tests/Parley.Broker.Tests/WhatIsRecordedIsWhatTheAgentWroteTests.cs`: `source` of `proposed`/`edited`/`composed`, `proposed_length` being the **agent's** message length and `NULL` for `composed`. **Must fail against** a person's edited length being stored — 0.29.0's second gate finding, from the other side
- [ ] T027 [P] [US1] Add `tests/Parley.Server.Tests/OwnershipOverTheWireTests.cs`: the whole acceptance over a real loopback socket. **No `Broker` and no `SqliteConnection` in this project**
- [ ] T028 [P] [US1] Add `tests/Parley.Server.Tests/AgentsCannotSetTheirOwnLineTests.cs`: the `Agents` service exposes **no** method that writes `owns`, and `parley-mcp` lists no tool that does. **Must fail against** the operation being added to the wrong service

### Writing the launch configuration

- [ ] T029 [US1] Add the `owns` write to `src/Parley.Setup/` beside the existing launch-configuration writer, **composed then committed by rename** so nothing is left partly written (FR-021)
- [ ] T030 [US1] Add the machine check of `research.md` R3 in `Parley.Setup`: **no surface writes for an agent whose machine is not this one**, through `MachineName.TryNameHere`, refused in the words of `contracts/refusals.md`. One rule, both surfaces
- [ ] T031 [US1] Add the read-the-name-back check: the launch configuration is refused if it configures a different agent under that key. **005's gate finding**, reached from the other side — a withdrawal once deleted a different agent's configuration because the workspace came from the registry and may be long out of date
- [ ] T032 [P] [US1] Add `tests/Parley.Cli.Tests/OwnsIsWrittenWhereItIsReadTests.cs`: the written key is what the adapter reads back, and a failed write leaves the file byte-identical. **Must fail against** a partial write — probe by making the rename fail

### The terminal

- [ ] T033 [US1] Add the ask, the answer and the accept to the agents pane in `src/Parley.Terminal/`, through the action table so `EveryKeyIsOfferedTests` and the status line get it by construction
- [ ] T034 [US1] Show the current line beside the proposal (FR-014), attribute the proposal to the agent and mark it as the agent's words (FR-013), and offer decline first
- [ ] T035 [US1] After acceptance, say it takes effect when the agent next starts and offer the restart through `Operators.Start` (FR-019); mark the line **pending** in the roster until `agents.owns` agrees (FR-020)
- [ ] T036 [P] [US1] Add `tests/Parley.Terminal.Tests/AskingWhatItOwnsTests.cs`, **pressing keys rather than calling seams**. 0.30.4 records the worst defect of 017 being that no test had ever pressed Enter, in a file whose own summary said it was there to catch exactly that. Assert on what the broker recorded, not on what the box says
- [ ] T037 [P] [US1] Add `tests/Parley.Terminal.Tests/PendingUntilItRestartsTests.cs`. **Must fail against** the pending mark being a flag rather than the comparison — probe by changing `agents.owns` underneath and watching the mark clear itself

### The web UI

- [ ] T038 [US1] Add the same to `src/Parley.Web/`, including the write, per the owner's decision of 2026-09-26 — and the identical machine refusal
- [ ] T039 [P] [US1] Add `tests/Parley.Web.Tests/AskingWhatItOwnsTests.cs` and `tests/Parley.Web.Tests/NotThisMachineTests.cs`. **Must fail against** the web UI writing for an agent on another machine
- [ ] T040 [P] [US1] Extend `DriftTests` so `AcceptOwnership` is bound by both surface trees with no exemption

**Checkpoint**: US1 is independently usable — ask, read, accept, restart, see it.

---

## Phase 4 (US2, P1): Disagree, and settle it by discussing

**Goal**: the operator replies in their own words, the agent revises, and what is written is what was agreed.

- [ ] T041 [US2] Reply from the room, in the same conversation, as an ordinary message (FR-008). The composer already derives the act per recipient; nothing here special-cases it
- [ ] T042 [US2] Carry a question from either party as an ordinary Clarify that does not close the exchange (FR-009, §5.2)
- [ ] T043 [US2] Accept the **agreed** line rather than the first proposal (FR-010), which falls out of T018 if T018 is right
- [ ] T044 [US2] Allow the operator to edit a proposal before accepting, and to compose one with no ask at all (FR-015), recording `edited` and `composed` respectively
- [ ] T045 [P] [US2] Add `tests/Parley.Terminal.Tests/SettlingItByDiscussingTests.cs`: ask, disagree, receive a revision, accept, and find the **revision** in the launch configuration. **Must fail against** the first proposal being written
- [ ] T046 [P] [US2] Add `tests/Parley.Client.Tests/TheDiscussionIsFindableTests.cs`: the conversation is readable afterwards and reachable from the agent (FR-011). **Must fail against** `conversation_id` not being recorded
- [ ] T047 [P] [US2] Add the abandonment case: leaving writes nothing and the conversation stays readable (AC US2.4)

**Checkpoint**: US1 and US2 together are the feature. Everything after this is reach and polish.

---

## Phase 5 (US3, P2): Ask everybody at once

**Goal**: after enrolling a machine, ask all of them and work through the answers as they arrive.

- [ ] T048 [US3] Add the bulk ask to both surfaces: N ordinary exchanges, one per agent (FR-006, §5.5). No new operation
- [ ] T049 [US3] Show the count and *each costs that agent a turn* **before the first Request is sent** (FR-007), and take an answer
- [ ] T050 [US3] **Send every ask before waiting on anything** (0.25.1's rule): thirty seconds each over nineteen agents is nine and a half minutes on a serialised queue. Say what is happening while it happens — a surface that goes quiet for a minute is one somebody assumes has hung
- [ ] T051 [US3] Name an agent that cannot be asked and proceed with the rest (AC US3.4), grouped by reason with names abbreviated after four — 0.25.1's finding, whose report ran off the screen
- [ ] T052 [P] [US3] Add `tests/Parley.Terminal.Tests/AskingEveryoneTests.cs`, asserting the ordering **by ordering rather than by timing**, as `Every_start_is_asked_for_before_anything_is_waited_on` does. **Must fail against** a per-agent wait
- [ ] T053 [P] [US3] Assert the cost statement is shown before anything is sent, and that answering *no* sends nothing

---

## Phase 6 (US4, P3): See the claim against its neighbours

**Goal**: an overlap or a gap is visible at the moment of deciding.

- [ ] T054 [US4] Show the lines held by the other agents in that project beside the proposal (FR-017), on both surfaces, from the roster read that already exists
- [ ] T055 [US4] Let the operator go and ask a named peer without losing the discussion they are in (AC US4.2)
- [ ] T056 [P] [US4] Add `tests/Parley.Client.Tests/TheNeighboursAreShownTests.cs`: two agents with overlapping lines, both shown, **neither refused** (AC US4.3, FR-030, Principle II). **Must fail against** any arbitration creeping in

---

## Phase 7: Polish and cross-cutting

- [ ] T057 [P] Apply every refusal in `contracts/refusals.md` and add `tests/Parley.Cli.Tests/OwnershipRefusalsTests.cs` covering each row, including the ones that must **never** be said: `reachable`, the sentinel integer, `seen`, and a person's character count attributed to an agent
- [ ] T058 [P] Enrolment no longer asks for an ownership line anywhere, and says the line is obtained by asking. Sweep `src/Parley.Setup/`, `src/Parley.Terminal/`, `src/Parley.Web/`, `README.md` and `plugins/parley/` — **a command is replaced by finding everything that ever recommended it**, which is 019's most expensive lesson
- [ ] T059 [P] `DeadSurfaceTests` clean: every new label is reached through the action table or is private
- [ ] T060 [P] `SurfaceTests` clean: the new door appears in exactly one file, which carries it
- [ ] T061 Run every suite from the host executables directly — `dotnet test` reports `Zero tests ran` on this machine — and record the counts against the binary that produced them, not against a build made before the last edit
- [ ] T062 Walk `quickstart.md`, keeping the walk script beside it as 019 did. Record honestly what could not be walked, including the isolation check being vacuous if `node.json` is absent
- [ ] T063 Ask **this repository's own agent** what it owns, through the built feature, and record the answer in SPEC.md §17 question 10. It is the one measurement the walk cannot make and the whole of the feature's value; CLAUDE.md's *Surveying the peers* is the precedent for the person running it and this agent doing the asking
- [ ] T064 **Run the review gate** — three consecutive clean runs per lens, each probing something the previous runs did not, logged in `REVIEW-LEDGER.md` **including the clean runs**. Not before the last edit: this file has been caught three separate times reviewing while authoring

---

## Dependencies

```
Phase 1  ──►  Phase 2  ──►  Phase 3 (US1)  ──►  Phase 4 (US2)
                                │
                                ├──►  Phase 5 (US3)   ── needs US1's ask only
                                └──►  Phase 6 (US4)   ── needs US1's read only
                                                  │
                                            Phase 7 ◄─┘
```

- **Phase 2 blocks everything.** The bound must be off before a proposal longer than a hundred
  characters can be accepted, and it is the only work that can destroy a store.
- **US2 depends on US1** and on nothing else. US3 and US4 depend on US1 and are independent of US2 and
  of each other.
- **T014 must land in the same commit as T005.** `Parley.Spec.Tests` executes the document's schema.
- **T002 must land in the same commit as T005.** The version moves with the first shipped behaviour.

## Parallel opportunities

| Phase | Safe together |
|---|---|
| 2 | T013 alone. **Nothing else in phase 2 is parallel** — it is one migration and one migrator |
| 3 | T016, T017, T019 · then T025, T026, T027, T028 · then T032, T036, T037, T039, T040 |
| 4 | T045, T046, T047 |
| 5 | T052, T053 |
| 6 | T056 |
| 7 | T057, T058, T059, T060 |

## Implementation strategy

**MVP is Phase 1 + Phase 2 + Phase 3.** An operator who only ever accepts the first answer is already
better off than one facing a blank field — the spec says so, and it is true.

**Phase 4 is what makes the result trustworthy** and should not be deferred far: without it the only
options are accept or reject, and rejecting leaves the field empty, which is the state this feature
exists to remove.

**Phases 5 and 6 are reach.** US3 is where it earns its keep on a machine with nineteen repositories;
US4 is the check no previous source could perform.

**Phase 7's last two are in this order and not before**: the quickstart walk (T062), then the gate
(T064).
