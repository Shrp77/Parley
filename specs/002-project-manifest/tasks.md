# Tasks: Machine-level project manifest

**Feature**: 002-project-manifest | **Branch**: `002-project-manifest`
**Input**: [spec.md](./spec.md), [plan.md](./plan.md), [research.md](./research.md),
[data-model.md](./data-model.md), [contracts/](./contracts/), [quickstart.md](./quickstart.md)

## Format: `[ID] [P?] [Story] Description`

- **[P]** — parallelisable: different files, no dependency on an incomplete task.
- **[US#]** — the user story this task serves. Setup, Foundational and Polish carry none.

## Path Conventions

Repository root is `C:/DevHell/Github/Parley`. Sources under `src/`, tests under `tests/`,
matching the existing layout.

**Tests are not optional here.** The template treats them as opt-in; this repository's
constitution does not — *"Hand-verifying a constraint and moving on is not enough — the case is
added so the next edit is checked too"*, and *"probe, do not reason"*. Every refusal in
[contracts/manifest.md](./contracts/manifest.md) gets a test that reconstructs the defect it
exists for.

## Two notes on story mapping

- **Multi-project `serve` (FR-019–021) and agent-side resolution (FR-022–024) have no user story
  of their own.** Both are attached to US1, as the story whose value they complete. Worth knowing
  when reading the spec: they are requirements without journeys, and if either grows further it
  should get its own story rather than more weight on US1.
- **US2 is defined by what does not happen**, so its phase is almost entirely tests. That is
  correct, not a gap.

---

## Phase 1: Setup (Shared Infrastructure)

**No scaffolding.** No new project, no new package — [research.md](./research.md) R2 rejected a
YAML parser rather than adding one. The only Phase 1 work is the specification, because
constitution principle I makes a protocol document change *precede* the code that implements it.

- [X] T001 Amend `SPEC.md` §4.1: replace the `parley.yaml` illustration with `projects.json`, and state that the file is per-machine, per-user, and lists named projects each with a store, an endpoint and a node catalogue. Keep §4.1's existing argument — the protocol has no concept of a project, names collide freely across them, one process may host many brokers — unchanged.
- [X] T002 Run `tests/Parley.Spec.Tests` and confirm green after T001. Section references must still resolve, tables must still be well formed, and every schema fragment must still execute in the order written.
- [X] T003 [P] Bump `VersionPrefix` in `Directory.Build.props`. This changes shipped behaviour in `src`, so the version moves in the same change, not at release time.

**Checkpoint**: the document describes the file the code is about to produce.

---

## Phase 2: Foundational (Blocking Prerequisites)

Every user story needs a project to be a validated thing and paths to resolve. Nothing below is
user-visible on its own.

- [X] T004 [P] Create `src/Parley.Core/ProjectName.cs`: a validated project name, same grammar as an agent name (lowercase letters, digits, hyphens, leading letter, at most 32 characters). **No I/O and no package dependency** — `Parley.Core` is constrained and a `using` that pulls I/O in is a review finding.
- [X] T005 [P] Create `tests/Parley.Core.Tests/ProjectNameTests.cs`: valid names, each malformed shape refused, and the boundary at exactly 32 characters — an off-by-one there rejects a legal name and gives the operator no way to tell that from the tool being wrong about its own rule.
- [X] T006 Create `src/Parley.Core/ProjectSettings.cs`: one project's values as a pure record — name, store, endpoint, node catalogue, description — with validation that does not touch a filesystem (absolute-path shape, endpoint parses). Per [plan.md](./plan.md), shape and rules are domain so the CLI and the adapter cannot disagree about what a valid project is.
- [X] T007 Create `src/Parley.Cli/ProjectManifest.cs`: locate (`ApplicationData` + `Parley/projects.json`, overridden by `--config` then `PARLEY_CONFIG`), read, validate whole-file, and write. Unknown keys round-trip. Byte-order mark and line endings of an existing file are preserved.
- [X] T008 Create `tests/Parley.Cli.Tests/ProjectManifestTests.cs` covering **every** refusal in [contracts/manifest.md](./contracts/manifest.md): absent is not an error; present-but-unparseable is refused and never treated as empty; `projects` present but not a list is refused; a `null` entry is named by index; duplicate name, duplicate store, duplicate endpoint each refused naming both; relative path refused; unparseable endpoint refused on read. **The unparseable and wrong-type cases are the ones that matter** — that exact failure has been found twice in this repository, once as an unreadable node catalogue treated as empty and once as a wrong-typed node cast away and overwritten.
- [X] T009 [P] Add to `tests/Parley.Cli.Tests/ProjectManifestTests.cs`: the resolved default location is absolute and under the user's profile. This is the assertion that closes [research.md](./research.md) R1, which was verified on Windows only — **CI runs all three platforms and that is where the Linux and macOS mapping is actually confirmed.**
- [X] T010 Create `src/Parley.Cli/ProjectResolution.cs`: a **pure function** of four inputs — explicit argument, environment variable, named project, previous default — returning each resolved value with the source it came from. Separated from `ProjectManifest` for the same reason `AutostartPlan` is separate from `AutostartCommand`: precedence is where the defects will be and must be testable without a filesystem.
- [X] T011 Create `tests/Parley.Cli.Tests/ProjectResolutionTests.cs`: precedence per value independently; a project supplying the store while `--broker` overrides the endpoint; **no manifest and no project named resolves exactly as today, value for value**; one project and none named uses it; several and none named refuses listing names; a named project absent from the manifest refuses and never falls back.

**Checkpoint**: a manifest can be found, read, refused and resolved. No command uses it yet.

---

## Phase 3: User Story 1 — Name a deployment once (Priority: P1) 🎯 MVP

**Goal**: an operator names a deployment and every command derives its paths from it.

**Independent test**: configure two projects with different stores and endpoints, run the same
command against each by name, confirm each touches only its own files.

- [X] T012 [US1] Add `--project <name>` and `--config <path>` to `src/Parley.Cli/Program.cs`, taken centrally beside `--db` and `--broker`. **They must be taken before the stray-hyphen guard**, which refuses any unconsumed hyphenated argument — a flag consumed downstream never reaches its command, as `--all` did not reach `enroll` until it was taken centrally.
- [X] T013 [US1] Wire resolution into dispatch in `src/Parley.Cli/Program.cs` so every command that needs a store, an endpoint or a catalogue receives resolved values rather than raw flags.
- [X] T014 [P] [US1] Report the resolved value and its source for any command about to write, in the form `node catalogue: <path>   (project "orders")`. This line is the point of the feature: it is what was missing when enrolling would have written consent into a catalogue nothing reads.
- [X] T015 [US1] Refusals in `src/Parley.Cli/Program.cs`: several projects configured and none named refuses and lists them; a named project absent from the manifest refuses, names it, and lists what is configured. **Neither may fall back** — choosing silently is the whole defect.
- [X] T016 [US1] Change `src/Parley.Cli/ServeCommand.cs` to start one `ParleyServer` per configured project when none is named, and only the named one otherwise. [research.md](./research.md) R3 confirmed `StartAsync` is self-contained, so no restructuring is required.
- [X] T017 [US1] Contain a partial start in `src/Parley.Cli/ServeCommand.cs`: a project that fails is named with its cause, the others keep serving, and **the process does not exit 0 while running a subset**. Starting none when projects were configured is distinct from an empty machine, which is success.
- [X] T018 [US1] Own the interrupt once, above all hosts, in `src/Parley.Cli/Program.cs` — each `WebApplication` installs its own console lifetime that sets `Cancel = true`, so *N* projects mean *N* handlers. Shutdown must stop every broker started and leave no port or store handle held.
- [X] T019 [US1] Create `tests/Parley.Cli.Tests/MultiProjectServeTests.cs`: two projects start and both serve; one failing project does not stop the other and the exit code says a subset is running; **one interrupt stops both**. That last case is the regression guard for the defect fixed immediately before this feature, reappearing at process level.
- [X] T020 [P] [US1] Resolve `PARLEY_PROJECT` to an endpoint in the MCP adapter startup, `src/Parley.Mcp`. Read-only: the adapter gains no ability to write a manifest.
- [X] T021 [US1] Refuse to start in `src/Parley.Mcp` when `PARLEY_PROJECT` names a project the machine does not describe **and no explicit endpoint is present** — naming the project and the manifest consulted. **Never fall back to a default endpoint**: connecting an agent to the wrong deployment is worse than one that does not start, and is invisible from both sides of a message.
- [X] T022 [P] [US1] Create `tests/Parley.Mcp.Tests/ProjectReferenceTests.cs`: endpoint only behaves as today; project only resolves; both agreeing connects; both disagreeing lets the endpoint win and reports it; **unknown project with no endpoint refuses and never connects elsewhere**. Also assert `PARLEY_AGENT` and `PARLEY_OWNS` are untouched by any of it — identity comes from the launch environment and a project supplies only where to connect.

**Checkpoint**: US1 delivers on its own. A machine with several projects is usable without repeating a path.

---

## Phase 4: User Story 2 — An existing deployment keeps working (Priority: P1)

**Goal**: nothing changes for anyone who has no manifest.

**Independent test**: with no manifest present, every previously working command behaves
identically.

- [X] T023 [P] [US2] Create `tests/Parley.Cli.Tests/NoManifestParityTests.cs`: with no manifest, `serve`, `up`, `enroll` and the operator commands resolve exactly as before, and **nothing reports the absence**. A tool that mentions a missing optional file on every run has made it mandatory in practice.
- [X] T024 [P] [US2] Add to the same file: with a manifest present, an explicit path or endpoint still wins and the command says which source it used.
- [X] T025 [US2] Add to the same file: a value the manifest does not supply falls back to the previous resolution rather than failing.

**Checkpoint**: the feature is additive, and it is proved rather than asserted.

---

## Phase 5: User Story 3 — Consent lands where it is read (Priority: P2)

**Goal**: the defect that prompted the feature cannot recur.

**Independent test**: enroll against a project whose catalogue is not in the working directory;
confirm entries land there and no second catalogue is created.

- [X] T026 [US3] Change `src/Parley.Cli/EnrollCommand.cs` to take the node catalogue from the resolved project, keeping the manifest's own `node_config` field as an explicit override above it.
- [X] T027 [US3] Ensure `enroll plan` names the exact catalogue file it would write **before** writing. The preview already prints a path; it must now print the resolved one and its source.
- [X] T028 [US3] Add to `tests/Parley.Cli.Tests/EnrollCommandTests.cs`: enrolling against a project whose catalogue is elsewhere writes there, and **no catalogue is created in the working directory**. This is the reconstruction of the original defect — ten repositories written to a file nothing reads, reported as success.
- [X] T029 [P] [US3] Add to the same file: the same repository name enrolled in two projects records independently in each catalogue, neither disturbing the other.

**Checkpoint**: the reported defect is closed and guarded.

---

## Phase 6: User Story 5 — Add a project without looking up the manifest (Priority: P2)

**Goal**: the tool owns its own artifact.

**Independent test**: on a machine with no manifest, add two projects by command; confirm the file
exists where expected and a third with a duplicate endpoint is refused.

- [X] T030 [US5] Create `src/Parley.Cli/ProjectCommand.cs` with `add`, `remove` and `list`, dispatched in `src/Parley.Cli/Program.cs` **beside `serve` and `autostart` rather than below the broker guard** — managing configuration must not require a running broker, or the command that fixes "no broker" needs one.
- [X] T031 [US5] `add` validates the whole manifest before writing any of it, creating the file and its directory when absent, and refuses a duplicate name, store or endpoint naming the conflict. Manifest unchanged on refusal.
- [X] T032 [US5] `remove` takes out only the entry. It **deletes no store, no catalogue and no launch configuration**, and states what it left behind and where — the distinction `retire` and `purge` already draw. Report when a broker appears reachable for the project being removed.
- [X] T033 [US5] Create `tests/Parley.Cli.Tests/ProjectCommandTests.cs`: add creates the manifest and directory; duplicate endpoint and duplicate store are each refused with the file unchanged; **remove deletes nothing on disk** and says so; removing an unknown project refuses and lists what is configured.
- [X] T034 [P] [US5] Add to the same file: `add` refuses rather than starting a fresh manifest over one it could not read. Overwriting an unreadable configuration is how a deployment silently loses everything it described.

**Checkpoint**: a manifest can be created and maintained without knowing where it lives.

---

## Phase 7: User Story 4 — See what this machine runs (Priority: P3)

**Goal**: an operator can answer "what is configured here" in one command.

**Independent test**: configure projects, run the listing, confirm each is named with its paths and
its reachability.

- [X] T035 [US4] Implement `project list` in `src/Parley.Cli/ProjectCommand.cs`: each project with store, endpoint, catalogue and reachability, plus the manifest path so the operator learns where it lives.
- [X] T036 [US4] Probe reachability with a **bounded** per-project connection attempt, so one dead project cannot stall the report. Unreachable projects are marked, never omitted and never reported as an error.
- [X] T037 [P] [US4] Add to `tests/Parley.Cli.Tests/ProjectCommandTests.cs`: a listing with no manifest says none is configured and where one would live, exiting 0; an unreadable manifest exits 78; an unreachable project is listed as such rather than dropped.

**Checkpoint**: all five stories complete.

---

## Phase 8: Polish & Cross-Cutting Concerns

- [X] T038 [P] Update `plugins/parley/commands/setup.md` and `setup-many.md` for `--project`, and `plugins/parley/skills/parley-operator/SKILL.md` for `project add|remove|list`. **`Parley.Mcp.Tests` asserts the operator skill documents every verb the tool has** — a new verb the skill has not heard of fails that test, by design.
- [X] T039 [P] Update `plugins/parley/README.md` and the repository `README.md`, which must stand alone.
- [X] T040 Verify [quickstart.md](./quickstart.md) command-for-command against the built binary. It tells somebody what to type, so it is held to the same standard as the code.
- [X] T041 Run the full suite on the host executables directly. **`dotnet test` reports `Zero tests ran` on this machine** — a toolchain that discovers nothing and a suite that contains nothing produce the same summary, and the second reading is the one that gets acted on.
- [X] T042 Case-insensitive search for the originating project's names across **tracked and untracked files**. `git grep` searches tracked files only, so it passes silently on a new feature's files — which is how two real names nearly shipped in the preceding change.
- [X] T043 Review to convergence: correctness and concurrency; security and data integrity; API surface and maintainability. After the last edit, not alongside it; re-run a lens after fixing what it found; stop when a full rotation finds nothing above low severity, and say how many rounds it took.


### What the review found

Five rounds, twelve findings, all fixed and each one guarded by a case that reconstructs it.

**Round 1**

1. *Correctness.* `ServeCommand`'s start loop sat outside the `try/finally` that disposes what it
   started, so an interrupt arriving between one broker coming up and the next left the first
   holding its port and its store handle — FR-021, in a window no single-project machine enters.
   Confirmed by breaking the disposal and watching the new guard fail, then restoring it.
2. *Correctness.* `ProjectManifest.Write` truncated in place. The file names every store on the
   machine and nothing holds a second copy, so a process stopped mid-write left one every reader
   afterwards refuses. Now written to a sibling and moved over.
3. *Integrity.* `description` was unbounded free text rendered straight into `project list`, so a
   newline in one let a project's entry produce lines that read as another's. Control characters
   are now refused in every string field, and the description is capped.
4. *Integrity.* The duplicate-store check compared path text, so `C:\srv\o\..\o\p.db` and
   `C:/srv/o/p.db` both got in — two brokers on one SQLite file, which neither reports. Paths are
   now normalised before comparison; symlinks remain out of reach and are documented as such.
5. *Maintainability.* The relative-path rule was written twice, at the read end and the write end,
   with wording that had already begun to differ. Extracted to `ProjectManifest.PathProblems`.
6. *Maintainability.* `CLAUDE.md`'s layout table did not mention the new project.

**Round 2** found one gap rather than a defect: nothing proved the adapter only *reads* the
manifest, which is what T020 claims. Asserted behaviourally — the bytes and the timestamp are
unchanged after a launch resolves a project — because the linker keeps `Write` in that binary
whether anything calls it or not.

**Round 3** found nothing above low severity, including a pass over the real binary that exercised
`--config`, the relative-path refusal and the aliased-store refusal end to end.

**Round 4**, run fresh over the final state including the documentation written after round 3,
found three more — one of them the most important in the review.

7. *Correctness, and the feature's own headline.* `enroll` announced the catalogue it was about to
   write **only when a project had been selected**. The run that produced the defect this feature
   exists for had no manifest at all and fell through to `node.json` in whatever directory the
   command was typed in — so the machine that most needed the line was the one machine that never
   saw it. A report that appears once somebody has already adopted the fix is not a report. Now
   printed always, with its source, and narrowed to `plan` and `apply`: `scan` never opens the
   catalogue, and naming a file it will not touch is the same species of wrong.
8. *Correctness.* `autostart` took its listen address from the resolution clients use, which
   consults `PARLEY_BROKER`. On a machine whose agents reach a broker elsewhere that holds
   somebody else's host, so `autostart install` wrote a supervisor entry telling a local broker to
   bind a remote address — failing at the next logon, inside a scheduled task, where nobody is
   watching. Where a broker listens now comes from `--listen` or the project and from nothing else.
9. *Integrity.* `Collisions` refused a shared name, store and endpoint, but not a shared node
   catalogue — while FR-017 requires consent to run unattended to stay per project *because a
   catalogue belongs to one*. A node reads one file and agent names collide across projects by
   design, so two projects sharing a catalogue means `parley up` for either starts entries the
   other consented to, under a name meaning a different repository in each. It cannot be enforced
   in storage the way §9.3 prefers, because the catalogue is not in the store.

Findings 7 and 8 were each confirmed by reintroducing the defect and watching the new guard fail
before restoring the fix.

**Round 5** was not a lens but a platform, and it is the round that justified the whole exercise.
GitHub Actions could not run — a billing block on the account, not a failing build — so the suite
was run against the .NET 10 SDK image in a container. Three failures appeared that Windows cannot
produce:

10. *Correctness.* `ProjectManifest.DefaultPath` combined the result of
    `Environment.GetFolderPath(ApplicationData)` straight into a path. On Linux that call returned
    **the empty string**, giving the *relative* `Parley/projects.json` — a manifest resolved
    against whatever directory the command was typed in, so two invocations from two directories
    each find and write a different file and each one looks like the manifest. The ambiguity this
    feature removes, reintroduced one level above everything it fixed. Now falls back to the home
    directory, and refuses when there is no home rather than inventing a writable path.
11. *Test defect, pre-existing.* `AutostartPlanTests.A_label_with_markup_in_it_cannot_break_the_plist`
    asserted the absence of `awkward/parley.db` — a substring that survives XML escaping intact and
    is supposed to. It passed on Windows only because the separator had been rewritten to a
    backslash by the time it ran: vacuous on one platform, wrong on the other two, and green
    everywhere anybody had looked. It now asserts the absence of the unescaped ampersand, which is
    the thing launchd actually chokes on. Not introduced here; repaired here because it stood
    between this branch and a green Linux run.
12. *Test defect.* `Two_casings_of_one_store_follow_what_the_filesystem_does` used a helper whose
    default gives both projects the same node catalogue, so finding 9's new check made it fail for
    a reason unrelated to the casing question it asks.

macOS remains unverified: no macOS machine was available and none can be simulated. The code takes
its path from the runtime rather than hardcoding one, so a different mapping there is correct
rather than broken — but the assertion has not been run, and `research.md` R1 says so rather than
claiming otherwise.

Three things were considered and deliberately left. Two endpoints that differ as text but bind one
port (`localhost` against `127.0.0.1`) fail visibly at `serve`, contained, with the exit code
saying so. `EnrollCommand`'s own catalogue write is not atomic — the same class as finding 2, but
pre-existing. And `enroll` is dispatched without a cancellation token, which is also pre-existing
and harmless while nothing it calls installs a console lifetime.

---

## Dependencies & Execution Order

### Phase Dependencies

```text
Phase 1 (Setup, spec first)
   └─> Phase 2 (Foundational)   ← blocks every story
          ├─> Phase 3  US1 (P1)  🎯 MVP
          ├─> Phase 4  US2 (P1)  independent of US1
          ├─> Phase 5  US3 (P2)  needs resolution only
          ├─> Phase 6  US5 (P2)  independent of US1
          └─> Phase 7  US4 (P3)  needs Phase 6's list plumbing
                 └─> Phase 8 (Polish)
```

### User Story Dependencies

- **US1** — Foundational only. Delivers alone.
- **US2** — Foundational only. Genuinely independent: it tests the absence of behaviour.
- **US3** — Foundational only.
- **US5** — Foundational only.
- **US4** — US5, for `ProjectCommand.cs`. The only story-to-story dependency.

### Within Each Story

Domain types → manifest/resolution → command wiring → tests. Tests that reconstruct a known
defect (T008, T019, T028) are written to fail first, since the point is watching the check catch
the case rather than trusting that it would.

### Parallel Opportunities

- **Phase 2**: T004/T005 with T006 — Core types are independent of the CLI.
- **Phase 3**: T020/T022 (adapter) run alongside T012–T019 (CLI); different assemblies.
- **Across stories**: US2, US3 and US5 can proceed simultaneously once Phase 2 lands — different files, no shared state.
- **Phase 8**: T038 and T039 are different files.

---

## Implementation Strategy

**MVP is Phase 1 + Phase 2 + Phase 3 (US1)** — 22 tasks. At that point a machine with several
projects is usable and the ambiguity that caused the defect is gone.

**Then Phase 4 (US2)** before anything else, despite being tests only. It is the proof that
existing deployments are unharmed, and it is cheapest to establish before more behaviour depends
on resolution.

**Then Phase 5 (US3)**, which closes the reported defect, followed by Phase 6 and Phase 7 as
ergonomics.

**Do not skip T001.** The specification change comes first, not because of process but because
principle I makes a document that describes a file the code will not produce a defect in itself.

---

## Summary

| | |
|---|---|
| Total tasks | 43 |
| Setup | 3 |
| Foundational | 8 |
| US1 (P1) | 11 |
| US2 (P1) | 3 |
| US3 (P2) | 4 |
| US5 (P2) | 5 |
| US4 (P3) | 3 |
| Polish | 6 |
| Parallelisable | 14 marked `[P]` |
