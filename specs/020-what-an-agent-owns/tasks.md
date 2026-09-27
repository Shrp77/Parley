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

1. **The schema is one file again, and the next change to it is a migration.** ~~The migration runs
   outside a transaction and cannot roll back.~~ Superseded on 2026-09-26: there is no migration, the
   eleven were folded into `001_initial.sql`, and the number is 1. **That collapse was available
   exactly once and has been used** — from the first release, every schema change is a migration, and
   `MigrationDiscoveryTests` moving to 2 is how that is noticed.
2. **`Parley.Spec.Tests` executes every schema fragment in SPEC.md, in the order written.** The
   document's schema and `001_initial.sql` move in the **same commit** or the build goes red.
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

- [X] T001 Confirm `.specify/feature.json` names `specs/020-what-an-agent-owns` and the working branch is `019-layout` (this repository builds features on the working branch; the name is cosmetic) — done
- [X] T002 Bump `VersionPrefix` to `0.33.0` in `Directory.Build.props`, **in the same commit as the schema change** rather than at the end — the first shipped behaviour, and the rule the build cannot catch — done

---

## Phase 2: Foundational — the storage

**Purpose**: the bound comes off. **No user story can start until this phase is done.**

> **REPLANNED AND DONE, 2026-09-26.** This phase was fourteen tasks built around a migration that
> rebuilt `agents` outside a transaction and could not roll back. The owner's call once the probes
> had priced it: *"don't worry about migration. Go ahead and make the changes and call it schema #1.
> Nothing we have is worth keeping right now."*
>
> So T003–T011 do not exist. The eleven migrations were folded into one `001_initial.sql`, the schema
> number went from 11 to 1, and the two edits this feature needs were made in it directly. **No
> migrator change, no out-of-transaction migration, no rollback hazard, no trigger choreography.**
>
> The collapse was **derived, not hand-merged** — a store migrated through all eleven, its schema read
> out of `sqlite_master` in creation order — and verified by a one-time comparison: 41 shared
> objects, identical names and identical creation order, and exactly one differing for any reason
> other than carriage returns inside stored DDL text. That comparison cannot be kept as a test,
> because what it compared against has been deleted, and `001_initial.sql` says so rather than
> claiming a guard that does not exist.
>
> **It cost fifty-three tests**, in six files whose whole subject was an upgrade path that no longer
> exists: `WorkAcrossRepositoriesMigrationTests`, `FileNameSetMigrationTests`,
> `FileStoreMigrationTests`, `RequestIsTheAskMigrationTests`, `ForceConcludedWorkMigrationTests` and
> `MigratedItemIdTests`. The rules they protected are still held elsewhere — `ItemIdBoundTests` for
> the item-id bound, `OldShapeRendersTests` and `OldShapeOnThePageTests` for rendering a pre-0.7
> payload, which needs no store at all.

- [X] T003 Add an opt-out to `src/Parley.Store/SchemaMigrator.cs` so **one named migration** may run outside the wrapping transaction. Keyed on the migration's own file name, not a general flag: `PRAGMA foreign_keys` is a no-op inside a transaction (probed, `research.md` R1), and a general capability would let the next migration lose its rollback by accident. `user_version` is bumped by the same path either way — **Not built.** The migrator needed no opt-out once there was no migration.
- [X] T004 Add `tests/Parley.Store.Tests/MigratorOptOutTests.cs`: a transactional migration that throws leaves the store untouched, and the opt-out migration is the **only** one the migrator will run unwrapped. **Must fail against**: the opt-out applied by a boolean any migration can set — **Not built.** Nothing to test: the migrator is unchanged.
- [X] T005 Write `src/Parley.Store/Migrations/012_ownership_is_asked.sql` — the rebuild of `agents` with `length(owns) <= 2147483647`. **The character class of FR-022 is copied from `001_initial.sql` verbatim and never retyped**: it was derived by sweeping the whole of Unicode against `OwnershipStatement`, and a hand-typed class is a class that has drifted. Header records, at length, that this is the first migration here to run outside a transaction and what that costs — **Not built.** Replaced by folding the change into `001_initial.sql`.
- [X] T006 In the same migration, take down `tr_agent_declares_its_machine` (on `agents`), `tr_result_is_well_formed` (on `messages`) and `tr_placer_is_registered` (on `shared_files`) before the rebuild and restore all three **verbatim** afterwards. The RENAME validates every trigger in the schema; with them standing it fails outright with `error in trigger tr_result_is_well_formed: no such table: main.agents` (probed). `tr_result_is_well_formed` is now written in three places and 011 left it alone deliberately — reproduce, do not improve — **Not built.** No triggers are taken down, because nothing is rebuilt.
- [X] T007 In the same migration, copy rows by **named columns**. `role` and `machine` are `GENERATED ALWAYS … STORED` and cannot be inserted; `SELECT *` fails — **Not built.** No copy happens.
- [X] T008 Add `src/Parley.Store/Migrations/012_ownership_is_asked.sql`'s second half: `CREATE TABLE agent_ownership_accepted` per `data-model.md`, with its three CHECKs and `ON DELETE CASCADE` — Done, in `src/Parley.Store/Migrations/001_initial.sql` rather than a new migration.
- [X] T009 Add `tests/Parley.Store.Tests/OwnershipBoundMigrationTests.cs`, building a **schema-11 store with traffic** — rows in `invocations`, `exchanges` and `messages`, and an agent carrying a line — *before* upgrading. **Must fail against an empty fixture being used instead**: 006 records that this rebuild succeeds on every empty fixture and fails on every store that has ever carried traffic, and six of `agents`' eleven children refuse a cascade — **Not built.** There is no upgrade path to test; the one-time collapse comparison is in `REVIEW-LEDGER.md`.
- [X] T010 In that suite, assert the post-migration state: `PRAGMA foreign_key_check` returns **no rows**, `PRAGMA integrity_check` returns `ok`, **fourteen** triggers are present, `role` and `machine` still generate from `name`, every child row survived, and **every pre-existing `owns` value is byte-identical** — **Not built**, as above.
- [X] T011 In that suite, assert the rule did not move when the number did: 400 characters accepted; U+202E, U+2028, U+00AD, U+FEFF, a tag character from U+E0020–U+E007F and an embedded NUL each **refused**. **Must fail against** the character class being retyped rather than copied — check it by running `OwnershipTests`' Unicode sweep against the rebuilt table — Done, by `OwnershipTests` in `tests/Parley.Store.Tests` — 400 characters accepted, the whole character class still refused.
- [X] T012 Change `OwnershipStatement.MaxCharacters` in `src/Parley.Core/OwnershipStatement.cs` to the named sentinel (`Unbounded`, matching `ExchangePolicy.Unbounded`'s shape), keep the rule, and rewrite the `<remarks>` to carry the amended reasoning rather than the measured hundred. Core still does no I/O and takes no package dependency — Done. `OwnershipStatement.MaxCharacters` is `int.MaxValue` and `Bounded` is the companion that stops any surface printing it.
- [X] T013 [P] Add `tests/Parley.Core.Tests/OwnershipSentinelTests.cs`: the sentinel and the schema's ceiling are the same number, and `Validate` admits a 400-character statement while refusing every character in T011. **Must fail against** the schema and the constant drifting apart — Done, as `OwnershipStatementTests` — no length is refused while no bound is in force, and the sentinel never reaches a person.
- [X] T014 Amend SPEC.md: §17 question 10 (FR-027 — a draft may come from the agent, on what conditions, at what cost, including that the claim narrows again and anchoring worsens), §18's 0.7 entry (FR-028, recording that 0.7 was unreleased so this folds in), and **the document's schema fragment for `agents`**. `Parley.Spec.Tests` executes every fragment in order, so this moves **with** T005 or the build is red — Done. SPEC.md §9.2 carries the new table and the relaxed CHECK, and §18's 0.7 entry has the amendment. `Parley.Spec.Tests` is green.

**Checkpoint**: the store is at **schema 1** -- the owner collapsed the eleven migrations rather than adding a twelfth, so this phase's checkpoint reads differently from how it was written -- `dotnet build` is green, every suite passes, and the bound is off with the character rule intact.

---

## Phase 3 (US1, P1): Ask an agent what it owns

**Goal**: an operator asks, reads the answer attributed to the agent, and accepts or does not.
**Independent test**: one enrolled running agent — ask, read, accept, restart, see the line in the roster.

### The ask, once, for both surfaces

- [X] T015 [US1] Add `src/Parley.Client/OwnershipAsk.cs`: the exact text of `contracts/asking.md`, and the conversation identifier rule `owns-<role>-<yyyyMMdd-HHmmss>`. **One implementation** — an answer given to one surface would have been given to the other — Done, as `src/Parley.Client/OwnershipAsk.cs` — the text, the derived identifier `owns-<role>-<yyyyMMdd-HHmmss>`, and the cost sentence. **No new operation**: it is sent through the existing `SeatVoice.RequestAsync`.
- [X] T016 [P] [US1] Add `tests/Parley.Client.Tests/TheAskSaysWhatItIsForTests.cs`: one case per row of `contracts/asking.md`'s mapping table — the line is read by every agent on every roster listing (FR-003), nothing is routed by it (FR-030), both numbered halves (FR-004), naming a peer, and that nothing the agent says changes configuration (Principle III). **Must fail against** any passage being dropped; assert on the passages, not on the length — Done, as `AskingWhatAnAgentOwnsTests` — 17 tests, one per mapping row, asserting the passages rather than the length.
- [X] T017 [P] [US1] Add `tests/Parley.Client.Tests/TheAskCarriesNoMarkerTests.cs`: the Request payload contains nothing a surface parses — no identifier, no sigil, no key. **Must fail against** a marker being added to the payload; mutation-check by adding one and watching it go red — Done, in the same file: the ask contains no `owns-` prefix and none of the shapes a reader might key on. **Fails the moment somebody puts the identifier in the words**, which is the tempting implementation.
- [X] T018 [US1] Add `src/Parley.Client/OwnershipProposal.cs`: the proposal is the **latest** message from the agent in that exchange (0.30.1's rule), and the pending comparison of `agents.owns` against the accepted statement — Done, as `src/Parley.Client/OwnershipProposal.cs`. The message travels with the text so a surface can attribute it (FR-013).
- [X] T019 [P] [US1] Add `tests/Parley.Client.Tests/TheProposalIsTheLatestThingSaidTests.cs`. **Must fail against** taking the first message: seed an exchange with a proposal, a clarify and a revision — Done, as `TheProposalIsTheLatestThingSaidTests` — 8 tests. **Mutation-checked**: swapping the ordering to take the first message fails two of them.

### The operation

- [X] T020 [US1] Add `AcceptOwnership` to `proto/parley.proto` on the **`Operators`** service, per `contracts/operations.md` — Done, as `RecordOwnership` on the `Operators` service. **Renamed from `AcceptOwnership`** — see `contracts/operations.md`.
- [X] T021 [US1] Add it to `IParleyBrokerClient` in `src/Parley.Core/` (§10.2's contract) and **not** to `IAcceptsOperatorInstructions` — Done, as `RecordOwnershipAsync`. `ContractSeparationTests` passed on the first run, which means the SPEC row and the interface method matched.
- [X] T022 [US1] Implement it on `src/Parley.Broker/OperatorConsole.cs` — **not** on `Broker`. `SurfaceBoundaryTests` enumerates the broker's public surface and fails when it grows, which is what makes this a decision rather than a habit — Done. `SurfaceBoundaryTests`' hand-kept list gained the entry with its justification, which is the one guard that had to be told.
- [X] T023 [US1] Add the repository write and the pending read to `src/Parley.Store/AgentRepository.cs`: write `agent_ownership_accepted`, and return `agents.owns` beside it so a surface can compare without reaching a store — Done. The write and the live-line read are **one act under one transaction**, because a surface derives *pending* by comparing them and a second call would let an attach land between the two.
- [X] T024 [US1] Bind it in `src/Parley.Server/OperatorsService.cs` — Done. Proto3's zero and empty are mapped to null rather than to 0 and "", so `proposed_length` stays a fact about an agent's message.
- [X] T025 [P] [US1] Add `tests/Parley.Broker.Tests/AcceptOwnershipTests.cs`: it records the acceptance and **does not write `agents.owns`**. **Must fail against** an implementation that writes the registry field — that is the whole of Principle III here — Done, as `tests/Parley.Broker.Tests/RecordingOwnershipTests.cs` — 13 tests, each naming what it must fail against.
- [X] T026 [P] [US1] Add `tests/Parley.Broker.Tests/WhatIsRecordedIsWhatTheAgentWroteTests.cs`: `source` of `proposed`/`edited`/`composed`, `proposed_length` being the **agent's** message length and `NULL` for `composed`. **Must fail against** a person's edited length being stored — 0.29.0's second gate finding, from the other side — Done, in the same file: `source` per route, and a composed line records **no length an agent never wrote**.
- [X] T027 [P] [US1] Add `tests/Parley.Server.Tests/OwnershipOverTheWireTests.cs`: the whole acceptance over a real loopback socket. **No `Broker` and no `SqliteConnection` in this project** — **Done**, 4 tests, and the fixture gained an `owns` on the attach because **that is the only route a line has into the registry** (§12.3): a test needing an agent whose peers already read a line has to attach as one, which is 020's whole point. The first assertion is that `owns` is untouched, read back through `DescribeRegistry` rather than out of a store.
- [X] T028 [P] [US1] Add `tests/Parley.Server.Tests/AgentsCannotSetTheirOwnLineTests.cs`: the `Agents` service exposes **no** method that writes `owns`, and `parley-mcp` lists no tool that does. **Must fail against** the operation being added to the wrong service — **Done**, 4 tests, matched on the *words* `Ownership`/`Owns` rather than on an exact method name, since the mistake being guarded is an operation added under a name nobody predicted. **Mutation-checked by putting the rpc on `Agents`**: both guards fail, and so does `BrokerV1Tests.An_agent_cannot_reach_the_operator_surface` — which already covered reachability, where these cover the name on the client. Complementary rather than redundant.

### Writing the launch configuration

- [X] T029 [US1] Add the `owns` write to `src/Parley.Setup/` beside the existing launch-configuration writer, **composed then committed by rename** so nothing is left partly written (FR-021) — Done, as `src/Parley.Setup/OwnershipLine.cs`. It reuses enrolment's own `HoldAsync`/`OverwriteHeldAsync`, so the exclusive handle, the symlink refusal, the byte-order mark surviving, and cancellation being honoured before the write and never during it are not reimplemented beside them.
- [X] T030 [US1] Add the machine check of `research.md` R3 in `Parley.Setup`: **no surface writes for an agent whose machine is not this one**, through `MachineName.TryNameHere`, refused in the words of `contracts/refusals.md`. One rule, both surfaces — Done, as `OwnershipLine.NotHere`, applied by **both** surfaces — and by the terminal *before the form opens* rather than after it is filled in, on 0.25.2's record of what the other order costs.
- [X] T031 [US1] Add the read-the-name-back check: the launch configuration is refused if it configures a different agent under that key. **005's gate finding**, reached from the other side — a withdrawal once deleted a different agent's configuration because the workspace came from the registry and may be long out of date — Done. An entry naming a different agent is refused and the file is left byte-identical, which is 005's gate finding reached from the other side.
- [X] T032 [P] [US1] Add `tests/Parley.Cli.Tests/OwnsIsWrittenWhereItIsReadTests.cs`: the written key is what the adapter reads back, and a failed write leaves the file byte-identical. **Must fail against** a partial write — probe by making the rename fail — Done, as `tests/Parley.Cli.Tests/OwnershipLineTests.cs` — 11 tests. Every refusal asserts the file's **bytes** are unchanged, and one asserts other people's MCP servers survive the write.

### The terminal

- [X] T033 [US1] Add the ask, the answer and the accept to the agents pane in `src/Parley.Terminal/`, through the action table so `EveryKeyIsOfferedTests` and the status line get it by construction — **Done.** `Ownership` holds all three routes: `n` records a composed line, `a` asks (offered whether or not a seat is held, because 0.28.2's defect was an action hidden until one was), and `n` in a transcript accepts what was proposed. **The machine rule deliberately does not gate the ask** — it guards writing a file on the agent's machine, and asking an agent on another machine is the case the ask is worth most in.
- [X] T034 [US1] Show the current line beside the proposal (FR-014), attribute the proposal to the agent and mark it as the agent's words (FR-013), and offer decline first — **Done** in `Ownership.Reviewed`: the live line, the sender, the local time, and "its words, not this screen's". **The discussion is read again at the accept rather than taken off the screen**, because a transcript is followed every few seconds and an agent that revised in that moment would otherwise have its retracted sentence written. Whose words the accepted line is falls out of a comparison (`Ownership.Chosen`) rather than a flag.
- [X] T035 [US1] After acceptance, say it takes effect when the agent next starts and offer the restart through `Operators.Start` (FR-019); mark the line **pending** in the roster until `agents.owns` agrees (FR-020) — **Done, and it needed the registry read to carry both halves.** `registry()` now returns the accepted line beside the live one, because the surfaces reach no store and a comparison needs two things to compare; `AgentRecord.OwnsPending` is that comparison and `Roster.PendingMark` is the one sentence both surfaces append. **The roster keeps showing the live line** — it is what peers route by, and showing the accepted one would tell somebody peers read a sentence they never have. The restart is **offered and not taken**: §12 says the process is what held everything the agent learned, so leaving it is a complete answer and the mark stays until the two agree.
- [X] T036 [P] [US1] Add `tests/Parley.Terminal.Tests/AskingWhatItOwnsTests.cs`, **pressing keys rather than calling seams** — **Done**, 20 tests, and the half I recorded as owed is closed: the accept is driven through a real broker with an **attached agent** in `SettlingItByDiscussingTests`, asserting the file's bytes. **The fixture I said that needed did not need building** — `Parley.Server.Tests` already had `LoopbackBroker` and `AttachedClient`, which is where T027 said the whole acceptance belonged. Worth correcting rather than leaving my estimate standing.
- [X] T037 [P] [US1] Add `PendingUntilItRestartsTests`. **Must fail against** the pending mark being a flag rather than the comparison — **Done**, 7 tests in `Parley.Core.Tests` plus 3 in `Parley.Store.Tests` and 3 in `Parley.Client.Tests`. **The probe changes the live line underneath and the mark clears itself with nobody told**, which a flag set at acceptance fails while passing every other case. The store pair also pin the left join: an inner one would drop every agent nobody had accepted a line for, so a roster would lose most of its rows the day it shipped.

### The web UI

- [X] T038 [US1] Add the same to `src/Parley.Web/`, including the write, per the owner's decision of 2026-09-26 — and the identical machine refusal — **Done.** The console records a composed line, and now also asks: `askownership` sends the same `OwnershipAsk.Text` from the seat the person holds, described before it is offered so a link cannot spend anybody's turn. **A missing seat is said plainly rather than falling through to the operator's voice**, which would put the question in front of the agent with nothing in the transcript and nothing owed back.
- [X] T039 [P] [US1] Add `tests/Parley.Web.Tests/AskingWhatItOwnsTests.cs` — **Done**, 3 tests, all reached with **no broker listening**, which is the point rather than a compromise: each asserts a guard that must be applied before anything is sent or written, so a console deferring either check downstream reports a transport error instead of the sentence asserted. The machine refusal is one of them, which is what `NotThisMachineTests` was for.
- [X] T040 [P] [US1] Extend `DriftTests` so `AcceptOwnership` is bound by both surface trees with no exemption — Done — `DriftTests` passes with no exemption.

**Checkpoint**: US1 is independently usable — ask, read, accept, restart, see it.

---

## Phase 4 (US2, P1): Disagree, and settle it by discussing

**Goal**: the operator replies in their own words, the agent revises, and what is written is what was agreed.

- [X] T041 [US2] Reply from the room, in the same conversation, as an ordinary message (FR-008) — **Done by construction and driven rather than asserted.** The composer already derives the act per recipient from who owes whom, and an ownership discussion is an ordinary conversation, so nothing special-cases it. `SettlingItByDiscussingTests` sends the person's disagreement into the open exchange over a real socket, which is what makes this a fact rather than a claim about the design.
- [X] T042 [US2] Carry a question from either party as an ordinary Clarify that does not close the exchange (FR-009, §5.2) — **Done, and 0.30.7's correction is what makes the sequence legal.** The agent Clarifies (the receiver asking the requester for more, which is what §5.2 actually gives the Clarify to), and the person answers *with a Clarify rather than a Result* — a move owed on an exchange you opened is the answer to a clarification. Both are driven in T045, so the exchange staying open is measured.
- [X] T043 [US2] Accept the **agreed** line rather than the first proposal (FR-010) — **Done**, and it does fall out of T018: `OwnershipProposal.Latest` takes the latest thing the agent said. The ordering itself is mutation-checked in `Parley.Client.Tests`; T045 checks the *wiring* — the field opens holding the revision and the revision is what reaches the file.
- [X] T044 [US2] Allow the operator to edit a proposal before accepting, and to compose one with no ask at all (FR-015), recording `edited` and `composed` respectively — **Done.** The field opens holding the agent's sentence, so editing is ordinary typing and `Ownership.Chosen` decides which was recorded **by comparing the two** rather than by a flag somebody sets. Composing with no ask is the agents pane's `n`, which records `composed` and names no conversation — refused by `OwnershipAcceptance.Problem()` if it tried to.
- [X] T045 [P] [US2] Add `tests/Parley.Terminal.Tests/SettlingItByDiscussingTests.cs`: ask, disagree, receive a revision, accept, and find the **revision** in the launch configuration. **Must fail against** the first proposal being written — **Done**, 2 tests, over a real socket with an attached agent and a real temp repository. It asserts the file's bytes and the dialog's pre-filled text, never the screen alone. **A first draft passed `_ => { }` as the work queue**, so the acceptance was handed to nothing and the test would have passed with the write deleted; the `Written()` assertion is what caught it, and is why the test demonstrably exercises the write.
- [X] T046 [P] [US2] The discussion is readable afterwards and reachable from the agent (FR-011). **Must fail against** `conversation_id` not being recorded — **Done, and not as its own file, because three places already held most of it**: `RecordingOwnershipTests` refuses a `proposed` acceptance naming no conversation, the store's real foreign key refuses an identifier pointing at nothing, and `OwnershipProposal.Subject` finds the agent again. What was genuinely missing was picking one agent's discussions out of a deployment's history, added to `AskingWhatAnAgentOwnsTests` — including that **`owns-infra-` does not match `owns-infrastructure-`**, which 018 records being bitten by when a role query matched on a substring.
- [X] T047 [P] [US2] Add the abandonment case: leaving writes nothing and the conversation stays readable (AC US2.4) — **Done**, and it asserts the stronger thing: leaving queues **no work at all**, rather than queueing work that then does nothing. The conversation is exported afterwards to show it is still there to come back to.

**Checkpoint**: US1 and US2 together are the feature. Everything after this is reach and polish.

---

## Phase 5 (US3, P2): Ask everybody at once

**Goal**: after enrolling a machine, ask all of them and work through the answers as they arrive.

- [X] T048 [US3] Add the bulk ask to both surfaces: N ordinary exchanges, one per agent (FR-006, §5.5). No new operation — **Done**, and it needed `SeatVoice.RequestEachAsync`: **one body, two entry points**, with `RequestEveryoneAsync` delegating to it. The grouped refusal report with names abbreviated after four is 0.25.1's finding, and a second bulk sender with its own reporting would be that lesson applied in one place only. **Each discussion is its own conversation** — a shared one would put nineteen unrelated arguments in one transcript and leave no surface able to find the one it wanted, which is what the existing bulk send does and is therefore the nearest wrong answer.
- [X] T049 [US3] Show the count and *each costs that agent a turn* **before the first Request is sent** (FR-007), and take an answer — **Done.** `Ownership.Costing` names them up to four and counts the rest, so the set is recognisable without the list running away; the safe answer sits under the cursor and answering it sends nothing.
- [X] T050 [US3] **Send every ask before waiting on anything** (0.25.1's rule) — **Done, and the rule reads differently here, which is worth saying rather than ticking.** That rule was written about *starting processes*, where thirty seconds each over nineteen agents is nine and a half minutes. **An ask has no wait at all**: a send is one round trip and answers arrive later as ordinary messages, so the rule is satisfied by there being nothing to defer rather than by deferring it. What would be wrong is waiting for each answer before asking the next, which would turn one keystroke into an afternoon — and that is what T052 asserts against.
- [X] T051 [US3] Name an agent that cannot be asked and proceed with the rest (AC US3.4), grouped by reason with names abbreviated after four — 0.25.1's finding — **Done by reuse rather than by reimplementation**: `RequestEachAsync` shares `SeatVoice`'s own grouped report, which already groups by reason, abbreviates after four, and puts the counts and the cause before the names.
- [X] T052 [P] [US3] Add `tests/Parley.Terminal.Tests/AskingEveryoneTests.cs`, asserting the ordering **by ordering rather than by timing**. **Must fail against** a per-agent wait — **Done**, 6 tests over a real socket. The ordering is asserted as **one queued act for all three agents**, a shape a per-agent wait could not have; the conversations are then read back from the broker, each named for the agent it is about and holding that agent's exchange and nobody else's. The dialog is answered **by pressing keys** (`driving.Row` then `driving.Enter`), on 0.30.4's record of a seam-only test proving nothing.
- [X] T053 [P] [US3] Assert the cost statement is shown before anything is sent, and that answering *no* sends nothing — **Done**, and the *no* case asserts the broker was never asked rather than only that the report said so: nothing is open against anybody afterwards, which survives the wording being changed.

---

## Phase 6 (US4, P3): See the claim against its neighbours

**Goal**: an overlap or a gap is visible at the moment of deciding.

- [X] T054 [US4] Show the lines held by the other agents in that project beside the proposal (FR-017), on both surfaces, from the roster read that already exists — **Done.** `ReviewAsync` takes the registry now rather than a per-agent lookup, which gives it the workspace, the live line and the neighbours from one thing; `Roster.Neighbours` is the shared rule. **A neighbour's *live* line is shown, never a pending one** — a line accepted but not yet carried back is a decision that has reached nobody, so it is not what competes.
- [X] T055 [US4] Let the operator go and ask a named peer without losing the discussion they are in (AC US4.2) — **Done by construction**: the discussion is a conversation the place names and asking a peer is an action in the agents pane, so neither clears the other. 017's rule that arriving at a conversation reads it is what keeps the transcript current when they come back.
- [X] T056 [P] [US4] Add `tests/Parley.Client.Tests/TheNeighboursAreShownTests.cs`: two agents with overlapping lines, both shown, **neither refused** (AC US4.3, FR-030, Principle II). **Must fail against** any arbitration creeping in — **Done**, 6 tests there plus 3 on the terminal's accept screen. The overlap is the first case, and the screen is asserted to contain no *conflict*, *clash*, *overlap* or *already claimed by*: refusing an overlapping line, ranking the claims or marking a winner would each feel like helpfulness and would each be one agent given authority over another where nobody would look for it.

---

## Phase 7: Polish and cross-cutting

- [X] T057 [P] Apply every refusal in `contracts/refusals.md` and add `tests/Parley.Cli.Tests/OwnershipRefusalsTests.cs` covering each row, including the ones that must **never** be said — **Done**, 9 tests. **The contract asked for something the code did not do**: the character refusal named the code point and not the position. That was adequate while a statement was capped at a hundred characters and is not now the bound is gone — *it contains U+2028* is not actionable advice about a paragraph — so it names the position, counted in text elements like the length beside it. The never-say set is read from the six sources carrying this feature's words, **named by hand rather than globbed**, so adding a surface is a decision somebody makes in that list.
- [X] T058 [P] Enrolment no longer asks for an ownership line anywhere, and says the line is obtained by asking. Sweep `src/Parley.Setup/`, `src/Parley.Terminal/`, `src/Parley.Web/`, `README.md` and `plugins/parley/` — **Done, and the sweep found two things that had already shipped stale, which is 019's lesson repeating.** **The README still said the line is capped at 100 characters** — the bound came off at 0.33.0, and the earlier sweep caught the plugin prose and the test sites and missed the README entirely. **`OwnershipStatement`'s own class remark still said the line "is authored by an operator, not by the agent it describes"**, the exact claim 020 amended; corrected in place, keeping what did not move — no agent-facing operation writes the field, and which of three routes produced a line is recorded. The plugin's setup guidance and the web UI's Register page both told a person to write the line and now say it is obtained by asking; the enrolment note reporting an absent line names the remedy rather than leaving somebody hunting a field that was removed on purpose.
- [X] T059 [P] `DeadSurfaceTests` clean: every new label is reached through the action table or is private — **Done**, and it is the terminal suite passing that says so rather than an inspection. Every new door is offered through the action table, so the status line and the `?` menu get it by construction, and the wording constants are read by tests that hold what a person is told.
- [X] T060 [P] `SurfaceTests` clean: the new door appears in exactly one file, which carries it — **Done.** Nothing this feature adds is destructive, so no typed-name door was needed: an ownership line is written, not destroyed, and the acceptance is reversed by accepting a different one. The guard passes with no exemption added.
- [X] T061 Run every suite from the host executables directly — `dotnet test` reports `Zero tests ran` on this machine — and record the counts against the binary that produced them — **Done. 4,223 tests, 0 failed, 1 skipped**, every suite run from its own host executable on the build that contains all of 020: Broker 232, Cli 1044, Client 389, Core 299, Headless 49 (1 skipped), Mcp 198, Server 333, Spec 49, Store 581, Terminal 842, Web 199, WebUi 8. **A count is evidence only about the binary that produced it**, and 018 records a commit whose message called the suite green against a build made before its last edits.
- [ ] T062 Walk `quickstart.md`, keeping the walk script beside it as 019 did — **Deferred to after 021, on the owner's decision of 2026-09-27**, so the walk covers the surface the combined gate will review rather than one that 021 then changes. **Step 10 has already been corrected**: it demanded a migration walked on a store carrying traffic, and there is no migration — the schema is 1. Rewritten rather than deleted, because what it asked for is still worth checking of a fresh store, and it now also records that an existing store cannot be opened by this build at all.
- [ ] T063 Ask **this repository's own agent** what it owns, through the built feature, and record the answer in SPEC.md §17 question 10 — **Blocked on the deployment, and the owner is doing that half.** It needs 0.33.0 installed, and **a 0.33.0 broker refuses a store written by 0.32.0**: the collapse took the schema from 11 to 1, so `SchemaMigrator`'s newer-store guard fires and its advice — *upgrade Parley, or restore the backup* — is the opposite of what happened. The data loss was accepted on 2026-09-26; the misleading sentence is a defect recorded in `REVIEW-LEDGER.md`. The owner resets and installs, and this is asked once they say the deployment is on 0.33.0. It remains the one measurement no walk can make.
- [ ] T064 **Run the review gate** — three consecutive clean runs per lens, each probing something the previous runs did not, logged in `REVIEW-LEDGER.md` **including the clean runs**. Not before the last edit: this file has been caught three separate times reviewing while authoring

  > **THIS IS THE ONE GATE FOR ALL THREE FEATURES, on the owner's decision of 2026-09-26**:
  > *"The review gate will be 019, 20 and 21. The sum of all these actions."* 019's T110 is
  > deferred into this one and must not be run separately.
  >
  > It runs after the last edit of the last of the three, which is what makes it right rather than
  > merely later: three features on one branch would otherwise mean three gates, two of them
  > invalidated by the next feature's commits. The surface is three features wide, so a finding
  > anywhere resets that lens across all of it.

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
