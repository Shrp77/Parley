---

description: "Tasks for 006 — three drill-down views on both surfaces, and the four operator operations they need"
---

# Tasks: Three-view operator surfaces, and conversations that can be archived and destroyed

**Input**: Design documents from `/specs/006-three-view-operator/`
**Prerequisites**: `plan.md`, `spec.md`, `research.md`, `data-model.md`, `contracts/`

**Tests are not optional here.** The constitution requires a green build as the whole lint gate,
every schema fragment in `SPEC.md` executed by `tests/Parley.Spec.Tests`, and invariants carried in
storage with a test that reconstructs the defect. *Probe; do not reason.*

**Organization**: by user story, so each is independently testable. Phase 2 is genuinely blocking:
the surfaces are clients of a protocol that does not yet have these operations.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel — different files, no incomplete dependency
- **[Story]**: which user story, on story-phase tasks only

---

## Phase 1: Setup

**Purpose**: the things that must be true before the first real edit.

- [X] T001 Bump `VersionPrefix` to `0.18.0` in `Directory.Build.props`. The minor moves because §10.2 is a versioned contract — "a broker either implements all of it or is not a V1 broker" — so adding four operations changes what a V1 broker must implement.
- [X] T002 [P] Open a `## 006-three-view-operator` section in `REVIEW-LEDGER.md` with the three-lens counter table at zero. Untracked; it spans sessions and machines, which is why it is written down rather than remembered.
- [X] T003 [P] Create `src/Parley.Store/Migrations/004_archive.sql` as an empty file and confirm `SchemaMigrator` discovers it and the suite still passes. A migration that is not discovered fails silently on the machine that most needs it.

---

## Phase 2: Foundational — the protocol, before anything reads it

**Purpose**: `SPEC.md` and the schema it documents. **Nothing in `src` changes in this phase**
except the migration file, and `tests/Parley.Spec.Tests` proves the document is true before any
code depends on it.

**Blocks**: every user story.

- [X] T004 Add `archive(conversation)`, `unarchive(conversation)`, `destroy(conversation)` and `start(agent)` to the §10.2 operations table in `SPEC.md`, each with the description `contracts/operations.md` gives and the reason it is §10.2 rather than §10.1.
- [X] T005 Write the `destroy` consequence into `SPEC.md` §14 beside the purge consequence, in the same terms: what the participants lose, what a waiting asker sees, and that it cannot be undone.
- [X] T006 Document in `SPEC.md` §12.3 that a broker MAY be asked to start a catalogued role, and that the agent registers itself on attaching — closing by name the gap §12.3's last paragraph already identifies. This is the sentence `research.md` R1 rests on being explicit rather than inferred.
- [X] T007 Add `conversations.archived_utc` to the §9 schema in `SPEC.md`, with the argument from `data-model.md` for why it is a column and not a fourth `state` value.
- [X] T008 Add the `destroyed_conversations` tombstone table to the §9 schema in `SPEC.md`, and state that it holds no message content and why (§9.4).
- [X] T009 State in `SPEC.md` §9 that destroying a conversation removes every message and exchange of it, carried by the foreign keys rather than by application code — §9.3's own argument. **Corrected while doing it: the plan and `data-model.md` said `ON DELETE CASCADE`, and the repository's own `PurgeAsync` already does something better.** The references deliberately do *not* cascade; the deletes run in one transaction with `PRAGMA defer_foreign_keys = ON`, so a table nobody remembered fails the whole destroy at commit instead of being swept silently. A cascade reports nothing, which is the opposite of what §9.3 asks a constraint to do. No table rebuild was needed and the existing FK declarations are untouched.
- [X] T010 Extend §10.2's `conversations()` description with the archived flag and the include-archived option. Not a new operation.
- [X] T011 Write `src/Parley.Store/Migrations/004_archive.sql` to match the fragments now in `SPEC.md` exactly, including the cascade. Read `003_message_cap.sql`'s recorded ordering hazards first.
- [X] T012 Run `tests/Parley.Spec.Tests` and confirm the new fragments execute in the order written, that section references resolve, and that no operation name spans the §10.1/§10.2 boundary. The floor check is what makes T004–T010 more than prose.
- [X] T013 [P] Add a case to `tests/Parley.Spec.Tests` asserting the four new names appear in §10.2 and in neither §10.1 nor the agent-facing proto service, so a later edit cannot quietly move one across.

**Checkpoint**: `SPEC.md` describes a protocol this repository does not yet implement, and the
document's own tests prove it is coherent. That is the intended state.

---

## Phase 3 (US1, P1): See the deployment without reading four things at once

**Goal**: one list of projects, a summary that follows the highlight, and a way in and back.

**Independent test**: with two projects named, the surface opens on the projects list; moving the
highlight changes the summary beside it; choosing opens the project view; back returns with the
same project highlighted.

- [ ] T014 [US1] Add `src/Parley.Client/Views/ProjectsView.cs` deriving what the projects view shows — the projects, and for one of them its agent and conversation summary — from broker reads, with no toolkit type in it.
- [ ] T015 [P] [US1] Add `src/Parley.Client/Views/ViewPlace.cs` holding where a person is: which of the three views, and what it is about. Derived once so both surfaces cannot disagree about what "back" means.
- [ ] T016 [P] [US1] Add `tests/Parley.Client.Tests/Views/ProjectsViewTests.cs`: the summary is for the highlighted project only; an unreachable broker yields "not answering" rather than an empty project; a machine naming no projects yields the empty-state sentence.
- [ ] T017 [US1] Rewrite `src/Parley.Cli/Top/Shell.cs` to host one view at a time over `ViewPlace`, replacing the four-region layout. Keep `OnFrameChanged`, the work queue under its lock, the idle sweep and `Safe` exactly as they are.
- [ ] T018 [US1] Rewrite `src/Parley.Cli/Top/ProjectsRegion.cs` as the projects view: the list plus the summary beside it.
- [ ] T019 [US1] Add `src/Parley.Web/Pages/Index.cshtml` (and its model) as the same projects view, reading the same derivation.
- [ ] T020 [P] [US1] Add `tests/Parley.Cli.Tests/Top/ProjectsViewTests.cs` driving the terminal: open, move, choose, back, and the same project still highlighted.
- [ ] T021 [P] [US1] Add `tests/Parley.Web.Tests/ProjectsViewTests.cs` making the same four assertions against the page.
- [ ] T022 [US1] Move 004's project actions — create, name the running deployment, remove with the name typed back, follow the log — onto this view unchanged, and move their tests with them **unedited**. A guard whose test needed changing to keep passing is the signal to stop.
- [ ] T023 [P] [US1] Extend `tests/Parley.Cli.Tests/Top/StackOnResizeTests.cs` to cover all three views at the narrowest supported width, asserting nothing is cut short.

**Checkpoint**: the owner's complaint is answered. This is the MVP.

---

## Phase 4 (US2, P1): Conversations that can be put away and destroyed

**Goal**: the three conversation operations, end to end, tested without either surface.

**Independent test**: archive through one client and a second client and a fresh process both agree;
restart the broker and it holds; destroy and no client can find it and the store holds no message of
it.

- [X] T024 [US2] Add archive, unarchive and destroy to `src/Parley.Store/ConversationRepository.cs`, and the tombstone write. Parameterised SQL; CA2100 is an error and the one existing suppression is not a precedent.
- [X] T025 [US2] Add the archived flag and the include-archived option to the conversation summary read in `src/Parley.Store/ConversationRepository.cs`.
- [X] T026 [P] [US2] Add `tests/Parley.Store.Tests/ArchiveTests.cs`: archived survives a reopen of the database; an archived conversation may be open or closed; unarchiving clears the timestamp.
- [X] T027 [US2] Add `tests/Parley.Store.Tests/DestroyTests.cs` **reconstructing the defect twice**: a message of the destroyed conversation surviving, and a message of a *different* conversation not surviving. The second half is what catches a cascade written too wide.
- [X] T028 [P] [US2] Add a case to `tests/Parley.Store.Tests` asserting the tombstone holds no message text, no payload and no intent — read the row back and assert its columns are exactly the four in `data-model.md`.
- [X] T029 [US2] Add the three operations to `src/Parley.Broker/OperatorConsole.cs`. **`Broker.cs` must not change**; `SurfaceBoundaryTests` fails if its public surface grows.
- [X] T030 [US2] Add `Archive`, `Unarchive`, `Destroy` rpcs and the `ConversationSummary.archived` / `ListConversations.include_archived` fields to `proto/parley.proto`, on `Operators` only.
- [X] T031 [US2] Implement the three rpcs in `src/Parley.Server/OperatorsService.cs` and map them in `src/Parley.Server/Wire.cs`.
- [X] T032 [US2] Add the three client methods to `src/Parley.Client/BrokerClient.cs`.
- [ ] T033 [P] [US2] Add `tests/Parley.Server.Tests/ConversationLifecycleTests.cs` over a real loopback socket: archive, read from a second client, restart, read again; destroy and confirm export can no longer find it.
- [ ] T034 [P] [US2] Add an adversarial probe to `tests/Parley.Server.Tests`: an `Agents` client attempting each of the three, asserting there is no such method to call. Written as the malicious case, not as a reading of the proto.
- [ ] T035 [US2] Add the destroy tombstone read, so an operator asking what happened to a conversation is answered — the FR-006 obligation.
- [ ] T036 [P] [US2] Add `tests/Parley.Broker.Tests` cases confirming an archived conversation still delivers messages and stays archived, which is `research.md` R4 made a property of the code rather than a decision in a document.

**Checkpoint**: the protocol is implemented and provable with no surface built.

---

## Phase 5 (US3, P2): Manage the agents of one project

**Goal**: the agents half of the project view, and an added agent that is startable at once.

**Independent test**: add a repository to a project with an empty catalogue; within thirty seconds it
is listed and can be started, with nobody having opened it. Change its startup behaviour and the
catalogue and the running node both reflect it.

- [ ] T037 [US3] Add the `start(agent)` rpc to `proto/parley.proto` on `Operators`, implement it in `src/Parley.Server/OperatorsService.cs`, and route it through `src/Parley.Server/BrokerRuntime.cs` to the node whose catalogue names the role.
- [ ] T038 [US3] Add `StartAsync` to `src/Parley.Client/BrokerClient.cs` and the operation to `src/Parley.Broker/OperatorConsole.cs`.
- [ ] T039 [US3] Add `tests/Parley.Server.Tests/StartOnDemandTests.cs`: a node joins declaring a role nothing has ever registered, `start` is called, and the node receives the Start command. **This is `research.md` R1's claim about `NodesService.RequestStart` turned into a test** — that the path has no registry lookup — rather than a reading of it.
- [ ] T040 [P] [US3] Add a probe asserting `start` on a role no node's catalogue names is refused and starts nothing.
- [ ] T041 [US3] Add `src/Parley.Client/Views/AgentStanding.cs` deriving one phrase per agent from presence and catalogue membership, per `data-model.md`. **Leave the wording as named constants and settle the words with the owner** — `plan.md` records that choosing them without them is the reason they are being replaced.
- [ ] T042 [P] [US3] Add `tests/Parley.Client.Tests/Views/AgentStandingTests.cs` covering all four combinations of never-run × startable-here, and asserting the phrases contain neither "catalogued" nor "not in this catalogue".
- [ ] T043 [US3] Add the agents half to the project view in `src/Parley.Cli/Top/AgentsRegion.cs`, showing name, standing and startup behaviour.
- [ ] T044 [US3] Add the same to `src/Parley.Web/Pages/Projects/Agents.cshtml`.
- [ ] T045 [US3] Move retire, reinstate and purge onto the project view unchanged, with their tests unedited, and confirm `SurfaceTests`' one-file rule still holds.
- [ ] T046 [US3] Add *Change startup behaviour*, writing the project's catalogue, and confirm the running node honours it without a restart — the catalogue-comparison loop 005 added is what makes this true.
- [ ] T047 [US3] Extend the add-an-agent description in `src/Parley.Cli/Top/Adding.cs` to name the process it is about to start, beside the two files it will write.
- [ ] T048 [US3] Call `start` from enrolment after the two files are written, in both surfaces, so adding leaves a startable agent.
- [ ] T049 [P] [US3] Add `tests/Parley.Cli.Tests/Top/AddedIsStartableTests.cs` walking the whole path: add, then a question addressed to the new agent starts it, with nothing having opened the repository.
- [ ] T050 [P] [US3] Confirm `WithdrawalReachTests` still passes and extend it: a workspace shared by two agents, where starting one must not start the other. The 005 defect that a test named for the property did not check it is the reason this is written out.

**Checkpoint**: `plan.md` Q1 is discharged or is proven wrong, in code, not in prose.

---

## Phase 6 (US4, P2): Find a conversation and decide what to do with it

**Goal**: the conversations half of the project view.

**Independent test**: six conversations, sorted each way with the highlight preserved; two archived
and the list holds four; *show archived* on and it holds six with two marked; choosing opens the
conversation view.

- [ ] T051 [US4] Add `src/Parley.Client/Views/ConversationsView.cs` deriving the list — name, last activity — and the detail panel — participants, message count, open, archived — from `conversations()`, which already returns all of it (`research.md` R6).
- [ ] T052 [US4] Add sorting by name and by date to that derivation, with the highlight expressed as the conversation's identifier so a sort cannot move it.
- [ ] T053 [P] [US4] Add `tests/Parley.Client.Tests/Views/ConversationsViewTests.cs`: both sorts, the highlight preserved across each, archived excluded by default, archived included and marked, and the archived count present in both.
- [ ] T054 [US4] Add the conversations half to the project view in `src/Parley.Cli/Top/ConversationsRegion.cs`, with the detail panel beside the list.
- [ ] T055 [US4] Add the same to `src/Parley.Web/Pages/Conversations/Index.cshtml`.
- [ ] T056 [US4] Add archive and unarchive actions to both surfaces. No typed name: nothing is destroyed and one keystroke reverses it.
- [ ] T057 [US4] Add the destroy action to both surfaces behind the full door — participants and count named, an open exchange's waiting asker named and accepted first, the identifier typed back — and add `DestroyAsync` to `SurfaceTests`' one-file rule.
- [ ] T058 [US4] Add *Start a new conversation* and the *show archived* toggle, the toggle as session view state and not stored on the broker.
- [ ] T059 [P] [US4] Add `tests/Parley.Cli.Tests/Top/DestroyDoorTests.cs` probing the door: the wrong identifier destroys nothing; declining the asker's outcome stops before the name is asked; the description names every participant.
- [ ] T060 [P] [US4] Add the equivalent page test in `tests/Parley.Web.Tests/DestroyDoorTests.cs`.
- [ ] T061 [P] [US4] Add a probe that an archived conversation which then receives a message is still archived and is marked as having had activity — R4 as a test.

---

## Phase 7 (US5, P2): Read a transcript that reads like a transcript

**Goal**: the conversation view, readable.

**Independent test**: a conversation of three agents and twenty messages, one carrying JSON and one
carrying paragraph breaks; colours consistent, headers distinguishable with colour stripped, times
local, paragraphs preserved, JSON laid out.

- [ ] T062 [US5] Add `src/Parley.Client/Rendering/LocalTime.cs`, one way to write a local time, shared by both surfaces so they cannot differ.
- [ ] T063 [US5] Add message-body layout to `src/Parley.Client/Rendering/Transcript.cs`: the author's line breaks and paragraphs preserved, JSON payloads indented over lines.
- [ ] T064 [US5] Make the header visually distinct from the body **by a means that survives colour being stripped** — the existing rule that a test stripping all colour must lose no information.
- [ ] T065 [P] [US5] Add `tests/Parley.Client.Tests/Rendering/ReadabilityTests.cs`: paragraphs survive; a five-key JSON payload occupies at least five lines; headers are identifiable in the colour-stripped text.
- [ ] T066 [P] [US5] Add a test asserting `parley export` output contains **no local time** and still says UTC. `research.md` R8: the export's reader is by definition somebody else, somewhere else.
- [ ] T067 [US5] Build the conversation view in `src/Parley.Cli/Top/TranscriptRegion.cs` over those derivations, keeping `Palette` unchanged (`research.md` R7) and the 003 visibility annotation intact.
- [ ] T068 [US5] Build the same page in `src/Parley.Web/Pages/Conversations/Detail.cshtml`.
- [ ] T069 [US5] Move taking part — answer, ask, tell, take delivery, seat required — onto this view unchanged, tests unedited.
- [ ] T070 [P] [US5] Add an injection probe: a payload containing newlines, escape sequences and control characters must not open a paragraph the surface treats as its own, nor repaint the screen. `Safe` still holds and the new line-breaking is not a route around it (FR-021).

---

## Phase 8 (US6, P3): Hand the current thing to a browser

- [ ] T071 [US6] Add `src/Parley.Client/Rendering/Deep.cs` building console addresses from `ConsoleHost.LoopbackAddress` and the console's own routes, in one place.
- [ ] T072 [US6] Bind `F8` on all three terminal views to open the address for the thing on screen, and list it among what each view can do.
- [ ] T073 [P] [US6] Add `tests/Parley.Client.Tests/DeepTests.cs` asserting each address matches a route the console actually serves — reflected from the Razor pages, not written out a second time. The `5045` defect was a hand-copied address; a hand-copied route is the same defect one layer up.
- [ ] T074 [P] [US6] Add a terminal test that `F8` on each view produces the right address and that a failed launch is reported without stopping the surface.

---

## Phase 9: Polish, parity and the review gate

- [ ] T075 Extend `tests/Parley.Cli.Tests/Top/DriftTests.cs` for the four new operations against both surface trees and `contracts/operations.md`, and confirm the four one-sided survey operations are still recorded as deliberate.
- [ ] T076 [P] Confirm `GraphTests` still finds no route to a store from `Parley.Client` or `Parley.Web`, walked transitively.
- [ ] T077 [P] Update `README.md` for the three views, the new operations and `F8`, and update `ReadmeTests` to match — including putting `Shell.Threshold` back to 80 and the README's wording with it, unless the owner has said otherwise.
- [ ] T078 [P] Update `CLAUDE.md`'s `Parley.Web` and `Parley.Cli` entries for the three-view shape and the four operations.
- [ ] T079 Run the full suite three times on Windows and three times under podman on Linux, and record the counts in a `Parity` table here. `dotnet test` reports "Zero tests ran" on this machine — run the host executables directly.
- [ ] T080 Run the review gate: three consecutive clean runs per lens, each probing something the previous runs did not, **after the last edit**, logged in `REVIEW-LEDGER.md` including the clean runs.
- [ ] T081 Walk `quickstart.md` end to end on the real deployment, both surfaces, and record what it found.

---

## Dependencies

```text
Phase 1 ──> Phase 2 (protocol) ──┬──> Phase 3 (US1) ──> Phase 6 (US4) ──> Phase 7 (US5)
                                 ├──> Phase 4 (US2) ────────^
                                 └──> Phase 5 (US3)
                                                    Phase 8 (US6) ──> Phase 9
```

- **Phase 2 blocks everything.** The surfaces are clients of operations that do not exist yet.
- **US4 depends on US2** for archive and destroy, and on US1 for the view it lives in.
- **US5 depends on US1** only for navigation; its rendering work is independent of US2 and US3.
- **US3 is independent of US2 and US4** — the agents half touches no conversation operation — so it
  can run entirely beside them once Phase 2 is done.
- **US6 depends on all three views existing** to have something to deep-link to.

## Parallel opportunities

- **Phase 2**: T013 runs beside T011–T012 once the fragments are written.
- **Phase 3**: T015/T016 are separate files from T014; T020/T021/T023 are three disjoint test files.
- **Phase 4**: T026/T028 run beside T027; T033/T034/T036 are three disjoint test files.
- **Phase 4 and Phase 5 run entirely beside each other** once Phase 2 is done, and this is the
  largest parallel opportunity in the feature — one is conversations, one is agents, and they share
  no file.
- **Phase 6**: T059 and T060 are the same probe against two surfaces.
- **Phase 7**: T065, T066 and T070 are disjoint test files.
- **Phase 9**: T076, T077 and T078 touch different files.

## Implementation strategy

**MVP is Phase 1 + Phase 2 + Phase 3 (US1).** A person who opens the surface and sees one list of
projects with a summary that follows the highlight has what the owner asked for. Nothing else in the
feature is needed to answer *"confusing and too busy"*.

**Phase 2 ships nothing and is the most important phase.** It changes `SPEC.md` and the schema and
leaves `src` alone, which will feel like no progress. It is what stops the surfaces being built
twice, and `tests/Parley.Spec.Tests` executing the new DDL is what makes the document true rather
than aspirational.

**T039 is the task that decides whether `plan.md` Q1 was right.** The claim is that
`NodesService.RequestStart` guards only on the node's catalogue and performs no registry lookup —
read from the code, and therefore evidence about this build rather than about the protocol. T039
turns it into a test. **If it fails, stop**: the reasoning that reversed 005's amendment is wrong,
and the honest outcome is to say so and put the amendment back, not to add a registration path.

**The riskiest edits are the moves, not the additions.** T022, T045 and T069 move 005's guards onto
new views. Their tests move **unedited**, and a test that needs editing to keep passing means the
guard changed while being carried. That is the stop rule for those three tasks.

**Do not review while authoring.** T080 runs after the last edit. `CLAUDE.md` records the owner
catching the alternative three separate times, and the ledger records that a rotation to convergence
was measured to be weaker than the counter.

**Two things are the owner's, not this list's.** T041's wording is chosen with them. T077's threshold
goes back to 80 unless they say otherwise — it is 200 today as a diagnostic and is marked as one in
its own remark.

## Format validation

All 81 tasks carry a checkbox, a sequential ID, a file path or a named document, `[P]` only where
files are disjoint and no incomplete task is depended on, and a `[US#]` label on story-phase tasks
and on no others.
