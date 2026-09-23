---

description: "An agent is an identity, not a name"
---

# Tasks: An agent is an identity, not a name

**Input**: Design documents from `/specs/018-agent-identity/`
**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/catalogue.md

**Tests**: included. This repository's rule is that a guard is a test, and two of the four success criteria
(SC-002, SC-003) are claims about shape that are worthless unasserted.

**Organization**: by user story, so P1 alone is a working slice.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel — different files, no dependency on an incomplete task
- **[Story]**: US1 to US4, from spec.md

---

## Phase 1: The identity and the catalogue it keys (blocking)

**Purpose**: the two shapes everything else reads. Nothing in Phase 2 onward compiles without these.

- [X] T001 Write `src/Parley.Configuration/AgentIdentity.cs`: a GUID behind a type, minted, parsed and refused, with no other operation on it
- [X] T002 [P] Write `tests/Parley.Core.Tests/AgentIdentityTests.cs`: minting is unique, parsing round-trips, anything not a GUID is refused, and nothing derives one from a path or a name
- [X] T003 Rekey `src/Parley.Configuration/NodeCatalogue.cs` by identifier: entries become `identity → (command, workspace)`, and `Roles`/`CommandFor`/`WorkspaceFor` stop taking an `AgentName`
- [X] T004 Give `NodeCatalogue` the workspace-side lookup: for each entry, read that workspace's launch configuration and return the `(project, name)` pairs it declares
- [ ] T005 [P] Write `tests/Parley.Cli.Tests/CatalogueByIdentityTests.cs`: one entry per identifier by construction, a second entry for the same identifier is unrepresentable, and an entry carries no project and no name
- [ ] T006 Teach `src/Parley.Setup/LaunchEntries.cs` to read and write `parleyAgentId`, leaving every other member of the file alone
- [ ] T007 [P] Write `tests/Parley.Cli.Tests/LaunchIdentityTests.cs`: the identifier survives a rewrite, a file without one is not damaged, and another tool's members are untouched

**Checkpoint**: the two files have their new shapes and nothing reads them yet.

---

## Phase 2 (US1, P1): a message reaches an agent whatever project it is asked in

**Goal**: the defect, gone. A checkout enrolled in two projects starts from either.

**Independent test**: enrol one checkout in two projects under two names; ask from the project whose
catalogue did not name it; it starts.

- [ ] T008 [US1] Write the failing test first — `tests/Parley.Cli.Tests/StartingAcrossProjectsTests.cs`: a node serving project B, a catalogue whose one entry's workspace declares a name for B, a start for that name; assert the process is launched. It must fail before T009
- [X] T009 [US1] In `src/Parley.Cli/NodeCommand.cs`, announce names per project from the workspace-side lookup rather than from `catalogue.Roles`
- [X] T010 [US1] In `src/Parley.Cli/NodeCommand.cs`, resolve a requested name to an identifier and then to a command and workspace when starting
- [X] T011 [US1] Refuse and say so when a name cannot be resolved, naming the catalogue file — so "started in the wrong place" cannot read as "nobody catalogued it"
- [ ] T012 [P] [US1] Report a catalogued workspace that has gone, and one whose launch configuration cannot be read, each naming the path; neither removes the entry
- [X] T013 [US1] Refuse to announce a project in which two catalogued workspaces claim one name, naming both paths (R4)
- [ ] T014 [P] [US1] Write `tests/Parley.Cli.Tests/AnnouncingNamesTests.cs`: the table in `contracts/catalogue.md` — each row of it, including the two-workspaces-one-name refusal
- [ ] T015 [US1] Run the CLI suite from its own host executable

**Checkpoint**: US1 is a deliverable slice. A checkout answers in every project that names it.

---

## Phase 3 (US2, P1): one checkout is one process

**Goal**: two projects asking at once produce one process.

**Independent test**: two simultaneous starts for one identifier from two projects; count processes.

- [ ] T016 [US2] Write the failing test first — `tests/Parley.Cli.Tests/OneProcessPerIdentityTests.cs`: two nodes sharing a table, two starts for one identifier, assert one launch. It must fail before T017
- [X] T017 [US2] Key the resident table by identifier in `src/Parley.Cli/NodeCommand.cs`, and take it as a parameter rather than creating it
- [X] T018 [US2] Create the table once in `src/Parley.Cli/Program.cs` / `ServeCommand.cs` and hand it to every node the process hosts (R5)
- [X] T019 [US2] Keep the dead-resident sweep working against the new key, so the next wake after a death is a start and not a turn into a corpse
- [ ] T020 [P] [US2] Assert the narrower thing that is still true: a turn goes to the existing process when one project asks for an agent another project started
- [ ] T021 [US2] Run the CLI suite

**Checkpoint**: SC-002 is measured rather than argued.

---

## Phase 4 (US3, P2): identity survives a move or a rename

**Goal**: moving a checkout or renaming it in one project leaves everything else alone.

- [X] T022 [US3] Mint the identifier in `src/Parley.Setup/Enrolment.cs` when the repository has none, and write it to both the catalogue and the repository
- [X] T023 [US3] Make enrolling an already-catalogued checkout into a further project add a name and nothing else — no second entry, and no refusal for one existing
- [X] T024 [US3] Refuse an enrolment whose identifier is already catalogued at another path, naming both paths (R2, the copied-repository case)
- [X] T025 [US3] Delete the machine-wide enrolment gate and the cross-project scan in `src/Parley.Setup/Catalogues.cs`, and 009's deviation with them
- [X] T026 [P] [US3] Write `tests/Parley.Cli.Tests/EnrollingTwiceTests.cs`: second project adds a name only; a move keeps the identifier; the copy is refused naming both paths
- [ ] T027 [US3] Take the catalogue binding out of the manifest: `src/Parley.Configuration/ProjectManifest.cs`, `src/Parley.Core/ProjectSettings.cs`, and every reader listed in plan.md
- [X] T028 [US3] Resolve the machine's one catalogue in `src/Parley.Cli/Program.cs`, and delete `serve`'s refusal of one explicit catalogue offered to several projects
- [ ] T029 [P] [US3] Update the surfaces that name a per-project catalogue: `src/Parley.Terminal/Adding.cs`, `Naming.cs`, `Onboarding.cs`, `Removing.cs`, `Shell.cs`
- [ ] T030 [P] [US3] Update `install.ps1`, `install.sh` and `src/Parley.Cli/AutostartCommand.cs` where they write or read a per-project catalogue path
- [ ] T031 [US3] Run the CLI, Setup-facing and terminal suites

**Checkpoint**: one catalogue per machine, everywhere that names one.

---

## Phase 5 (US4, P2): a person is told when this machine cannot start an agent

**Goal**: the half of the defect that made it invisible.

- [ ] T032 [US4] Give the client a way to know which participants this machine cannot start, read from the catalogue rather than guessed
- [ ] T033 [US4] Mark such a participant in `src/Parley.Terminal/RoomRegion.cs`, in words and never by colour alone
- [ ] T034 [US4] Say it in the composer's preview in `src/Parley.Terminal/LineRegion.cs`: the message will wait until that repository is opened
- [ ] T035 [US4] Keep the send working and say what became of it in the report — a queued Request is collected the moment the repository is opened, so refusing would throw away a message that was going to arrive
- [ ] T036 [P] [US4] Write `tests/Parley.Terminal.Tests/UnstartableIsSaidTests.cs`, reading the laid-out screen rather than the model
- [ ] T037 [US4] Run the terminal suite

**Checkpoint**: every user story delivered.

---

## Phase 6: The specification, and delivery

- [ ] T038 Update `SPEC.md` §12 for the catalogue's key and scope, and §4.1's manifest illustration for the binding that goes — the specification changes in the same commit as the code (Principle I)
- [ ] T039 [P] Update `README.md`, the operator skill and `parley --help` wherever they describe a per-project catalogue
- [ ] T040 Bump `VersionPrefix` in `Directory.Build.props` — a minor, because §12's catalogue is a versioned description of what a node does
- [ ] T041 Hygiene grep for the originating project's names over everything added, tracked and untracked
- [ ] T042 Run every suite from its own host executable and record the counts
- [ ] T043 Walk `quickstart.md` against a throwaway deployment, recording each check
- [ ] T044 The review gate: three lenses, three consecutive clean runs each, after the last edit, logged in `REVIEW-LEDGER.md`

---

## Dependencies

```text
Phase 1 ──> Phase 2 (US1) ──> Phase 3 (US2)
        └─> Phase 4 (US3) ──> Phase 5 (US4)
                                   └──> Phase 6
```

- Phase 1 blocks everything: both file shapes change there.
- US1 and US3 are independent of each other after Phase 1 — US1 is the node reading, US3 is enrolment writing
  and the manifest. US2 depends on US1 only because it edits the same method.
- US4 depends on US3 for "this machine cannot start it", which is a question about the one catalogue.

## Parallel opportunities

- T002, T005, T007 — three test files, three different subjects.
- T012 and T014 alongside T013.
- T029 and T030 — surfaces and scripts, no overlap.
- T036 while T035 is being written; different projects.

## Implementation strategy

**MVP is Phase 1 plus Phase 2.** That is the owner's defect fixed: a checkout answers in every project that
names it. Phase 3 stops it starting twice, which matters the first time two projects ask at once. Phases 4
and 5 make the arrangement honest and the remaining gap visible.

**Two tasks must fail before their implementation** — T008 and T016 — because both assert a shape rather than
a value, and a test written after the code it checks is a second statement of it. Run each against the old
behaviour and record that it failed.
