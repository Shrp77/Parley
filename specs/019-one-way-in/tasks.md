# Tasks: One place, and one way in

**Input**: Design documents from `specs/019-one-way-in/`
**Prerequisites**: plan.md, spec.md, research.md (R1–R6), data-model.md, contracts/layout.md, contracts/commands.md

**Tests**: included, and not optional here. Two of this feature's claims are races — a port claimed twice
(SC-008) and a project served while agents stay attached (SC-010) — and this repository's own history says
a race reasoned about is a race unfixed. Every probe below names what it must fail against before it is
believed.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: different files, no dependency on anything incomplete — safe to run together
- **[Story]**: the user story it serves
- Paths are repository-relative

## The three rules this feature is most likely to break

Read these before starting; each has a test that fails the build rather than a convention to remember.

1. **`PluginTests.The_operator_skill_documents_every_operator_command`** extracts `parley ([a-z][a-z-]*)`
   from the *whole* of `src/Parley.Cli/Program.cs`, comments and strings included, and demands set equality
   with `SKILL.md`. **A removal stub must never write `parley <removed verb>`** — see T070.
2. **`GraphTests`** walks project references transitively. `Parley.Configuration` may reach neither a store
   nor the transport, and every tool references it.
3. **`Parley.Core` has no I/O and no package dependencies.** `ProjectSettings` keeps no path rules (FR-067).

---

## Phase 1: Setup

**Purpose**: the branch and the version, so that nothing later has to remember them.

- [ ] T001 Confirm the working branch is `019-one-way-in` off `main`, and that `.specify/feature.json` names `specs/019-one-way-in`
- [ ] T002 Bump `VersionPrefix` to `0.32.0` in `Directory.Build.props`, in the same commit as the first behaviour change rather than at the end

---

## Phase 2: Foundational — the layout readers

**Purpose**: everything below depends on a machine's projects being a directory listing rather than a file.
**No user story can start until this phase is done.**

- [X] T003 [P] Remove `NodeConfig` from `src/Parley.Core/ProjectSettings.cs` and fix every compile error it produces (FR-068; this is 018's owed T027, absorbed here)
- [X] T004 Add `src/Parley.Configuration/ProjectFolder.cs`: given a root and a name, where that project's `project.json` and store are (FR-003, FR-006). Pure path composition, no I/O beyond existence
- [X] T005 Add `project.json` read/write to `ProjectFolder`: `endpoint` required and an absolute URI, `description` optional, **no `name`, no `store`, no `node_config`** (data-model.md)
- [X] T006 Rewrite `src/Parley.Configuration/ProjectManifest.cs` to derive the project set from the projects directory (FR-005), keeping the type name so callers move in one step
- [X] T007 Implement the five bad-folder outcomes as reports rather than failures: no `project.json`, unparseable JSON, missing/invalid `endpoint`, folder name not a project name, and two projects on one endpoint (FR-026 to FR-029)
- [X] T008 Delete `ProjectManifest.Collisions`' store and endpoint checks; keep name uniqueness as a property of the filesystem (plan.md, Principle V)
  - **Done as store and *name*, not store and endpoint.** This task's wording contradicts FR-028 and its own T012, both of which require two projects on one endpoint to be reported by name. The endpoint is the one collision the layout cannot prevent — nothing about a folder stops two of them naming one port — so it is the one check that had to stay. Name and store went, because neither is expressible: a name is a directory entry and a store is derived from it.
- [X] T009 Change `ProjectManifest.Locate` so `--config`/`PARLEY_CONFIG` names the **root** (R4). **The compatibility half was built and then removed 2026-09-23 with FR-062**: the old spelling is refused by name, naming the directory to point at and `reset.ps1`, rather than silently resolved
- [X] T010 [P] Test in `tests/Parley.Cli.Tests/`: a folder with no `project.json` is silently not a project; an unparseable one is reported naming project and file while the others are read
- [X] T011 [P] Test: a folder whose name is not a valid project name is reported and skipped, and does not stop the rest being read
- [X] T012 [P] Test: two projects claiming one endpoint are both reported by name and neither is preferred (FR-028)
- [X] T013 [P] Test: `--config` given the old `projects.json` path resolves to the directory containing it, and given a directory resolves to itself — including when neither exists yet (R4)

**Checkpoint**: a machine's projects can be read from a directory; nothing yet writes one.

---

## Phase 3: US1 — Name a project and nothing else (P1)

**Goal**: an operator types a name; the tool chooses the store location and the port and reports both.
**Independent test**: on an empty root, add by name alone, start the broker, connect. No path, no port typed.

- [X] T014 [US1] Add `src/Parley.Configuration/PortLedger.cs` with the `ports.json` shape: optional `band`, required `handed_out` (data-model.md)
  - **Built in `Parley.Setup`, not `Parley.Configuration`.** `Parley.Mcp` references Configuration and deliberately carries nothing about setting a machine up; a type that claims ports and binds sockets on an agent's own server is precisely the surface `Parley.Setup` exists to keep out of it, which is CLAUDE.md's own argument for that project existing. Both writers of the record — `project add` and `project remove` — already live there.
- [X] T015 [US1] Implement the exclusive hold: open `ports.json` with `FileShare.None`, read-modify-write, close — modelled on `StoreLock.Acquire`, and **never deleting the file** (R1; the Linux `flock`-belongs-to-an-open-file reason)
- [X] T016 [US1] Read a failed open as *held* only when the file exists afterwards, so a missing root is not reported as contention (R1, carried from `StoreLock`)
- [X] T017 [US1] Make contention ordinary rather than fatal: bounded retry with short backoff, and a refusal naming the contention when the budget runs out (R1, FR-015)
- [X] T018 [US1] Reconcile before choosing (FR-013): inside the hold, list the projects and treat every endpoint found in a `project.json` as spoken for, whether or not `ports.json` lists it
- [X] T019 [US1] Choose from the band **19100–19355** and bind-test the candidate inside the hold before recording it (R1; the bind is what `FreePort` lacked, the hold is what serialises it)
- [X] T020 [US1] Report exhaustion and create nothing (R1, FR-015)
- [X] T021 [US1] Rewrite `project add` in `src/Parley.Setup/ProjectCommand.cs` to take a name alone: claim a port, create the folder, write `project.json`, and report the endpoint and the store location (FR-016, FR-017)
- [X] T022 [US1] Make `project add` create nothing when the claim fails — no folder, no partial `project.json` (FR-015)
- [X] ~~T023 [US1] Keep `--listen` on `project add` for a stated endpoint~~ — **reversed 2026-09-23.** Built as written, then removed with FR-020: `--listen` is gone everywhere and an endpoint that must not be loopback is edited in `project.json`
- [X] ~~T024 [US1] Check a stated endpoint's availability on the address it names~~ — **reversed with T023.** Nothing states an endpoint, so `PortLedger.TryReserve` and `CanBindAt` went with it
- [X] T025 [US1] Keep `--description`, and keep `project set` able to change it (FR-019)
- [X] T026 [US1] Release a project's port on `project remove` (FR-014)
- [X] T027 [US1] Make `project add` work with no broker running (FR-018)
- [X] T028 [P] [US1] **Race probe**: N concurrent `project add` calls against one root must produce N distinct endpoints, repeated enough times that a one-in-many failure is seen (SC-008). Must fail against a version that chooses without the hold
- [X] T029 [P] [US1] Test: deleting `ports.json` costs a rebuild and nothing else — every project keeps its endpoint and the next add gets an unused port (SC-009)
- [X] T030 [P] [US1] Test each `ports.json` state from data-model.md: missing, empty, lost entries, naming a dead project, naming a port a project does not have
- [X] T031 [P] [US1] Test: an unreadable `ports.json` fails the claim and **affects nothing that serves** (FR-011)

**Checkpoint**: US1 is independently demonstrable — `project add orders`, then serve, then connect.

---

## Phase 4: US2 — Everything Parley keeps is inside a project (P1)

**Goal**: there is no way to name a store outside a project, and the test rig still isolates itself.
**Independent test**: look for every documented way to name a store outside a project and find none.

**This phase is load-bearing work, not cleanup.** R3 inventories 26 files.

- [ ] T032 [US2] Remove `--db`/`-d` and `PARLEY_DB` from `src/Parley.Cli/Program.cs` and from `serve` (FR-007, FR-031)
- [X] T033 [US2] Remove `--db` from `project add` — the only way a store path is set today (R3's first scope question)
- [ ] T034 [US2] Remove `--node-config` from `project add` and `project set`, which wrote a field nothing reads (FR-052)
- [X] T035 [US2] Keep `InstalledEntry.Parse`'s `case "--db":` read path (R3; 0.22.0's *stop writing, stop
  accepting, keep reading*)
  - **Kept after all, and the reasoning is worth the two lines it costs.** FR-063 was withdrawn — nothing
    is released, so no entry needs to go on *working*. But `autostart status` still has to *describe* what
    is installed, and a developer who has not yet run `reset.ps1` has an old entry right now. A reader that
    could not parse it would show blanks for a scheduled task that plainly exists, which is the shape this
    repository keeps recording: a green-looking answer to a question nobody asked. Reading costs nothing
    and writing is gone, which is exactly the line 0.22.0 drew for `--console`.
- [ ] T036 [US2] Convert `tests/Parley.Cli.Tests/BrokerProcess.cs`: `Start` takes a root and a project name, writes `<root>/projects/<name>/project.json` **directly** with a `FreePort()` endpoint, and keeps reading the real address off stdout (R3 — the rig must not allocate, because the band is per machine and the root is per test)
- [ ] T037 [US2] Remove `PARLEY_DB` from `ToolProcess.cs`'s variable clear-list, keeping the neutral working directory
- [ ] T038 [US2] Delete `OneStoreIsForTestingTests.cs`, `ForeignDatabaseTests.cs`, `NoManifestParityTests.cs` — ~24 tests whose subject is `--db` itself
- [ ] T039 [US2] Split `ServeSelectionTests.cs`: delete the two `--db` tests, move the catalogue-selection test elsewhere
- [ ] T040 [P] [US2] Convert the eleven mechanical files listed in R3 (`PruneCommandTests`, `StartupBannerTests`, `WebUiContainmentTests`, `ChildLifetimeTests`, `SecondServeTests`, `InFlightWarningTests`, `SecondSessionTests`, `ProjectDestroyTests`, `DeploymentVariables`, `OwnershipRoutingTests`, `AutostartManifestPlanTests`)
- [ ] T041 [US2] Convert `ProjectCommandTests` (17 tests) — the largest careful file, and the one the original surface inventory missed
- [ ] T042 [US2] Convert `ProjectResolutionTests`, `EnrolmentNextStepsTests`, `AutostartPlanTests`, `InstallScriptManifestTests` — the store-naming mechanism is what they assert
- [ ] T043 [US2] Convert `ProbeSevenTests` and `ProbeThirtyThreeTests`: keep the read-compatibility halves, drop the write halves (R3)
- [ ] T044 [US2] **Re-decide** `tests/Parley.Terminal.Tests/NamingTests`: it drives adopting a broker somebody started by hand with an explicit store, which FR-007 makes impossible. The flow needs a decision, not a repointing (R3)
- [ ] T045 [P] [US2] Test: a test rig naming a different root gets a wholly separate set of projects and needs no path beyond that root (US2 scenario 3)

**Checkpoint**: `SC-003` is demonstrable — no command or variable opens a store outside a project.

---

## Phase 5: US3 — Enrol a folder of repositories in one act (P1)

**Goal**: one verb finds the repositories, configures them, starts them, and reports which answered.

- [ ] T046 [US3] Collapse `enroll scan|plan|apply` into one verb in `src/Parley.Setup/EnrollCommand.cs`: `enroll <dir> [--all|--none] [--describe]` (FR-040). No file to author, no plan step
- [ ] T047 [US3] Offer the git repositories found and allow choosing among them, with `--all`/`--none` for scripts (FR-041)
- [ ] T048 [US3] Implement `--describe`: report what would be configured, change nothing (FR-042)
- [ ] T049 [US3] Catalogue every repository enrolled, so a peer's request can start it — **enrolling is the consent** (FR-043, reversing the per-repository opt-in)
- [ ] T050 [US3] State before configuring that enrolling makes each repository startable by a peer, and name how many are about to be started (FR-044)
- [ ] T051 [US3] Start each repository after configuring so each **registers itself**; write no registration on a repository's behalf (FR-045, FR-066)
- [ ] T052 [US3] Report per repository whether it registered, and report one that did not register in time as **not known**, never as failed (FR-046; 008's rule)
- [ ] T053 [US3] Enrolling a repository already enrolled elsewhere adds this project's name and leaves its identity and other projects alone (FR-047)
- [ ] T054 [US3] Carry forward an existing statement of what a repository owns; never ask a person to compose one (FR-048 — 020 owns the asking)
- [ ] T055 [P] [US3] Test: a git worktree is reported as sharing its checkout's launch configuration rather than silently written (edge case)
- [ ] T056 [P] [US3] Test: enrolment with no broker running does what it can and says what it could not (edge case)
- [ ] T057 [P] [US3] Test: a repository already enrolled in this project is reported as such, not duplicated, identity unchanged

---

## Phase 6: US4 — Parley serves everything, or it is not running (P2)

**Goal**: starting Parley serves every project; no option serves fewer.

- [X] T058 [US4] Change `ServeCommand`'s `reread` to list the projects directory and parse each `project.json` — **and change nothing else about the watcher**, which already polls every two seconds (R2)
  - **Done early, because it was not optional once the layout landed.** The stamp was one file's write time; that file no longer exists, so `File.Exists` was false, the stamp never moved, and the watcher silently stopped noticing anything at all — a machine going on serving whatever it started with while `project add` reported success. Caught by `HostedNodeTests`, not by reading. The stamp is now every project's configuration and when each was last written, which is also what keeps a broker from being restarted for a file somebody dropped beside one. R2 held: nothing else about the watcher changed.
- [ ] T059 [US4] Remove `--listen`, `--db`, `--with-web-ui`, `--web-ui`, `--no-web-ui` from `serve` (FR-031, FR-039)
- [ ] T060 [US4] Serve every project and report by name any whose store cannot be opened, without preventing the others (FR-030, FR-032)
- [ ] T061 [US4] Stay up and report why when no project can be opened or none exists, so a project named next is served without a restart (FR-033)
- [ ] T062 [US4] Remove the four `autostart install` options; the entry serves everything and starts the surfaces (FR-039)
- [ ] T063 [P] [US4] Test: three projects, all reachable after one start; and no option exists that would have served fewer (SC-004)
- [ ] T064 [P] [US4] Test: one project with an unopenable store is named and the other two are served (FR-032)

---

## Phase 7: US5 — The command line stops offering what it cannot do (P2)

**Goal**: sixteen verbs become stubs naming the surface that does the thing.

- [ ] T065 [US5] Replace the sixteen verbs in `src/Parley.Cli/Program.cs` with stubs: `retire`, `reinstate`, `purge`, `export`, `force-conclude`, `prune`, `instruct`, `start`, `archive`, `unarchive`, `destroy`, `refusals`, `clarifications`, `results`, `flags`, `instructions`
- [ ] T066 [US5] Each stub exits **64** (`EX_USAGE`) so a script detects it, and names the surface and how to reach it (FR-051)
- [ ] T067 [US5] **Write no stub text containing `parley <removed verb>`** — say "That moved to the terminal", never "`parley export` moved" (contracts/commands.md; this breaks `PluginTests` otherwise)
- [ ] T068 [US5] Remove the sixteen from `plugins/parley/skills/parley-operator/SKILL.md` in the same change, including the listing at L122–178
- [ ] T069 [US5] Verify `The_agent_facing_command_reaches_for_no_operator_command` still holds for `write-peers.md` and `status.md`
- [ ] T070 [US5] Run `PluginTests` and confirm the verb sets are equal in both directions **before** moving on — this is the build constraint, not a check to defer
- [ ] T071 [US5] Rewrite `project list` output: endpoint, store and reachability in one place; **two facts never merged into one word** — whether it answered, and whether anything off this machine can address it (FR-022, FR-023, FR-025)
- [ ] T072 [US5] Put the remedy beside the limitation: a loopback endpoint's report says what to run to make it reachable (FR-024)
- [ ] T073 [US5] Drop the `node …-catalogue.json` line from `project list`, which names what 018 made meaningless
- [ ] T074 [P] [US5] Test: every removed verb exits 64 and names a surface; and none of their output contains `parley ` followed by the removed verb
- [ ] T075 [P] [US5] Test: `project list` never describes a loopback endpoint with a word that reads as network reachability (FR-023)

---

## Phases 8 and 9 — withdrawn 2026-09-23, the owner's call

**US6 (an existing deployment moves) and US7 (a machine that has not been reinstalled still starts) are
both cut. Nothing has been released, so there is nothing to move and nothing to strand.**

Twenty-two tasks go with them — T076 to T093 — along with FR-046 to FR-064's compatibility half and
research R6. They were careful work: take each store's lock first as the check that nothing is serving,
copy then verify then rewrite then delete so an interruption leaves the configuration naming whichever
copy is authoritative, never overwrite a destination, leave `.lock` and `.owner` behind because they
describe a process that is not running and carrying them forward carries a lie.

**All of it rested on a premise nobody examined: that deployments exist to migrate.** The only machine
carrying the old layout is the one this was written on.

**What replaces them is `reset.ps1`**, at the repository root:

- [X] T076 Add `reset.ps1`: stop the broker, remove the autostart entry, read the old manifest **before**
  deleting it so every store it names goes with its `-wal`, `-shm`, `.lock`, `.owner`, `.files` and
  `.files-incoming` companions
- [X] T077 Keep `node.json` — the machine's agent catalogue is keyed by identity since 018 and unchanged
  by 019, and deleting it would mean re-enrolling every repository by hand
- [X] T078 Give it `-FilesOnly`, which skips every machine effect. **Its first run removed a live logon
  entry**: `-Root` sandboxed the files while `parley autostart remove` went on acting on the real machine.
  A parameter that sandboxes the files and not the machine is not a sandbox
- [X] T079 Take the store list from the old manifest rather than guessing, because once it is deleted
  nothing knows where those stores were
- [ ] T080 **A `reset.sh` counterpart — owed, not forgotten.** Windows only for now, on the owner's call
  2026-09-23, because that is the machine carrying the old layout. **CI runs on Windows, Linux and macOS
  and `install.sh` covers all three**, so a developer setting Parley up on Linux or macOS today has no way
  to clear a pre-019 layout but by hand. The PowerShell script is the specification for it: stop the
  broker, read the old manifest *before* deleting it, take every store with its `-wal`, `-shm`, `.lock`,
  `.owner`, `.files` and `.files-incoming` companions, keep `node.json`, and carry `--files-only` — which
  exists because the PowerShell one removed a live logon entry on its first run

**One rule from the withdrawn work is kept and is load-bearing**: the broker stops first, because
`StoreLock` holds a store with no sharing and a running broker cannot have its files deleted underneath
it.

---

## Phase 10: US8 — A project named while Parley runs is served within seconds (P3)

**Goal**: existing behaviour this change could silently break.

- [ ] T094 [US8] Confirm the `failing` set still holds a project that will not start and retries only when its entry changes (R2)
- [ ] T095 [US8] Confirm an unreadable `project.json` stops nothing, generalising the loop's existing `catch` to one bad project among many (R2, FR-029)
- [ ] T096 [P] [US8] Test: a project added while Parley runs is reachable within ten seconds without a restart (FR-034, SC-010)
- [ ] T097 [P] [US8] Test: adding or changing one project does not disconnect agents attached to another (FR-035, SC-010) — the half that matters, and the half a restart would silently break
- [ ] T098 [P] [US8] Test: a project folder removed by hand stops being served and the rest go on (FR-036)

---

## Phase 11: Polish, documents, and the gate

- [ ] T099 [P] Update `SPEC.md` §4.1 — its illustration of a project manifest becomes the layout (Constitution Principle I; this is a document change first)
- [ ] T100 [P] Sweep `SPEC.md` §10.2 for command-line spellings; **the operations do not change**, only how they are spelled at a prompt
- [ ] T101 [P] Sweep `README.md` (~90 mentions), and make it direct a person who wants to take part to the terminal or the web UI — naming a way in that works with no terminal (FR-054)
- [ ] T102 [P] Update `plugins/parley/README.md`
- [ ] T103 [P] Update `plugins/parley/commands/setup.md`: `project add --db --listen` becomes `project add <name>`
- [ ] T104 [P] Update `plugins/parley/commands/setup-many.md`: `enroll scan|plan|apply` becomes the one verb
- [ ] T105 Confirm `plugins/parley/commands/files-*.md` are untouched — those five verbs stay, and a slash command has no terminal to be pointed at
- [ ] T106 Confirm `plugins/parley/hooks/parley_waiting.py` still works: `parley waiting <agent> [--with-files]` with `--broker` or `--project` runs on nearly every turn of every session, and swallows errors by design (R5 — breaking it fails silently everywhere)
- [ ] T107 Confirm `install.ps1` and `install.sh` still work end to end: `project list` including the readiness poll, `open`, `autostart install`, `autostart remove`, `<tool> --version` (FR-050)
- [ ] T108 Update `CLAUDE.md`'s current-plan entry to 019, naming 0.32.0 and what building it found
- [ ] T109 Walk `quickstart.md` against tools packed into a scratch path, with the live deployment left alone, and record every check in `REVIEW-LEDGER.md`
- [ ] T110 Run the review gate: three consecutive clean runs per lens, each probing something the previous runs did not, logged in `REVIEW-LEDGER.md` — **including the clean runs**
- [ ] T111 Hygiene: scan the **added lines of the branch's own diff** for the originating project's names, not the files, and include untracked files, which `git grep` cannot see

---

## Dependencies

```text
Phase 1  Setup
  └─> Phase 2  Foundational (layout readers)        ← blocks everything
        ├─> Phase 3  US1  name a project            ← MVP
        ├─> Phase 4  US2  nothing outside a project (needs Phase 3's folder writer)
        ├─> Phase 5  US3  enrol in one act
        ├─> Phase 6  US4  serve everything          (needs Phase 2's reread)
        ├─> Phase 7  US5  command removals          (independent of 3–6)
        ├─> Phase 8  US6  migrate                   (needs Phase 3's layout)
        ├─> Phase 9  US7  old entry still starts    (needs T009)
        └─> Phase 10 US8  served while running      (needs Phase 6)
              └─> Phase 11 Polish and the gate
```

**US5 (Phase 7) is independent of the layout work** and can be done by a second pair of hands at any point
after Phase 1 — it touches `Program.cs`, `SKILL.md` and the documents, and nothing in `Parley.Configuration`.

**US2 (Phase 4) is the long pole**: 26 files, of which nine need thought and one (T044) needs a decision
rather than an edit.

## Suggested MVP

**Phase 1 + Phase 2 + Phase 3.** That delivers SC-001 — an operator brings up a deployment by typing one
name — and is independently demonstrable without any of the removals, the migration or the enrolment work.

## Parallel opportunities

- T010–T013 (Phase 2 probes) once T004–T009 are in
- T028–T031 (US1 probes) once T014–T027 are in
- T040 (eleven mechanical conversions) across several hands
- T099–T104 (documents) at any time after Phase 7
- Phase 7 entirely, alongside Phases 3–6

## Settled: the terminal's "Name the running deployment" is deleted

**Decided 2026-09-23, and by the flags going rather than on its own merits.**

Neither `spec.md` nor `plan.md` mentioned this flow. It was found while converting the terminal suite:
`Naming.OpenNameRunningAsync` adopted a broker *already serving*, reading its store and port out of an
autostart entry — "every answer is already in the entry, so the one thing typed is the name."

`CanNameRunning` required an entry that serves one explicit store, which is an entry written with `--db`.
**Nothing can write one now**, so no machine will have one again; and the owner's own entry served the
manifest, so the screen was already unreachable there.

**The trap it left behind is why this needed deciding rather than editing.** Deriving a fresh store and
keeping the stated endpoint compiles, passes, and points the newly named project at an *empty* database
while the running broker still holds the real one — every conversation in that deployment appearing to
have vanished, with nothing objecting.

Gone with it: `Naming.NameRunningTitle`, `CanNameRunning`, `OpenNameRunningAsync`, `Shell.NameRunning`,
`Naming.ProposedStore`, `StoreProblem`, `EndpointProblem`, and the `Store`/`StoreWhy` fields of
`Proposed`. The create form is two fields: a name, and an optional description.

## What must be true at the end

| | |
|---|---|
| SC-001 | one name, no path, no port |
| SC-002 | one command, no file authored |
| SC-003 | no way to open a store outside a project |
| SC-004 | every project reachable; no option serves fewer |
| SC-005 | every surviving verb is called by a program or brings a machine up |
| SC-006 | one command moves a deployment; everything readable before is readable after |
| SC-007 | an un-reinstalled machine still serves |
| SC-008 | simultaneous adds never share an endpoint, measured over many attempts |
| SC-009 | deleting `ports.json` costs a rebuild and nothing else |
| SC-010 | a project added while running is reachable in ten seconds, and nothing else disconnects |
