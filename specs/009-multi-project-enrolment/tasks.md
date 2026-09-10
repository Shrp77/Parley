# Tasks: A repository may answer in more than one project

**Feature**: `009-multi-project-enrolment` | **Date**: 2026-09-09
**Input**: [spec.md](./spec.md), [plan.md](./plan.md), [research.md](./research.md),
[data-model.md](./data-model.md), [contracts/launch-configuration.md](./contracts/launch-configuration.md)

**Every phase leaves the tree green.** A phase that requires the next one to build is a phase that
cannot be reviewed, and the review gate runs against the final state either way.

**A failing check comes before the thing it pins.** The ordering is not ceremony: 007's step 1 wrote
`The_terminal_reaches_no_store_by_any_assembly_it_imports` first, ran it, and watched it name the leak
before the move — and 008's gate found a defect that a test written afterwards would have been shaped
to pass. A test that passed throughout proves nothing about what was repaired.

**Version**: `0.22.0`, bumped in T044 with the deprecation removal, not at the end.

---

## Phase 1: The vocabulary, and a faithful extraction (foundational)

**Goal**: the key rule exists in one place and every caller uses it. **Nothing changes on disk** — this
phase still writes `parley` for everything, and the whole suite passes with **no test edited**. That is
what proves the extraction was faithful rather than a rewrite wearing its name.

- [x] T001 Add `tests/Parley.Cli.Tests/LaunchKeyTests.cs` covering the grammar of
      `data-model.md` § Key: `parley` for no project, `parley-<project>` for one, injectivity, the
      never-collides property, a project called `parley`, and the four things that are **not** ours —
      `parleyx`, `parley-`, `parley-Alpha`, `parley-a_b`. Fails: the type does not exist.
- [x] T002 Add `src/Parley.Setup/LaunchKey.cs`: `For(string? project)`, `IsOurs(string key)`,
      `ProjectOf(string key)` returning null for the legacy key. `<remarks>` carries why the prefix was
      chosen over the bare project name — withdrawal is the only path that deletes somebody's
      configuration, so ownership must be decidable from the key alone.
- [x] T003 [P] Add `src/Parley.Setup/LaunchEntries.cs`: read an `mcpServers` object into the entries
      this tooling owns, each carrying key, agent, project, owns and broker address. Reading only —
      no precedence yet, no writing.
- [x] T004 [P] Add `tests/Parley.Cli.Tests/LaunchEntriesTests.cs` for the reader alone: a legacy entry,
      a project-keyed entry, both together, a stranger's server ignored, an entry with no
      `PARLEY_AGENT`, and an `mcpServers` that is not an object.
- [x] T005 Rewrite `src/Parley.Setup/Enrolment.cs` in terms of `LaunchKey`, removing the `ServerKey`
      constant. Behaviour identical.
- [x] T006 Rewrite the four literal-key uses in `src/Parley.Setup/EnrollmentPlan.cs` in terms of
      `LaunchKey`. Behaviour identical.
- [x] T007 Rewrite the two readers in `src/Parley.Setup/EnrollCommand.cs` (lines 468 and 590) in terms
      of `LaunchEntries`. Behaviour identical — still the first entry, still one answer.
- [x] T008 Add `tests/Parley.Cli.Tests/OneKeyRuleTests.cs`: the literal `"parley"` as a server key
      appears in exactly one source file under `src/`, and a member merely *named* for it does not
      satisfy the check. The `SurfaceTests` shape, which has already caught a violation of its own rule
      once in this repository.
- [x] T009 Run the full suite. **No test file may be edited in this phase.** A failure here is the
      extraction being unfaithful, not a test being stale.

**Checkpoint**: met. Twelve hosts green, **2,718** (2,678 + 40 new), `.mcp.json` written byte-for-byte
as before.

**Three things the phase turned up, each by running rather than by reading:**

- **The project-name grammar admits a trailing hyphen.** `^[a-z][a-z0-9-]{0,31}$` accepts `alpha-`, so
  `parley-alpha-` really is this tooling's key. T001 asserted it was a stranger and T001 was wrong.
  Refusing it would have made a repository in a project called `alpha-` unwithdrawable — a key nothing
  owned. Now a named test, so the surprise is recorded rather than left for somebody else.
- **There is a fourth illustration.** The survey named the README, the plugin's setup instructions and
  the Register page; the one-key check found `src/Parley.Mcp/Program.cs`'s header comment showing a
  `.mcp.json` too. FR-014 was amended to four. This is the argument for the check over the survey.
- **One test was edited, and it is the one designed to be.** `SetupSurfaceTests` enumerates
  `Parley.Setup`'s public types on purpose — *"written out rather than counted, so a member added here
  is a decision somebody makes in this file"*. Three names added. The phase's "no test edited" rule
  means no test whose *promise* changed, and none did.

---

## Phase 2 (US2): A legacy entry is read, and never renamed — P1

**Goal**: User Story 2. Every repository in an existing deployment keeps working, and re-enrolling one
into the project it is already in changes its key to nothing.

**Independent test**: take a repository with a legacy entry, run every read path over it, then enrol it
again into the project it already answers in and diff the file.

- [x] T010 [US2] Add `tests/Parley.Cli.Tests/LaunchPrecedenceTests.cs` with the four cases of
      `data-model.md` § Precedence as separate tests, each named for the case. Fails: the method does
      not exist. **Ten cases rather than four**: the two disagreement shapes — a stranger at the derived
      key, and a key and environment naming different projects — are what the caller has to refuse, and
      a precedence that hid either would be writing over somebody's entry.
- [x] T011 [US2] Add `LaunchEntries.Landing(JsonObject servers, string? project)` implementing the
      precedence, with `<remarks>` saying why case 2 precedes case 4 (a rename edits a working agent's
      tool names to achieve nothing) and why case 3 follows case 2 (an unpinned entry must not swallow
      an enrolment meant to be a second one). **Named `Landing` rather than `For`**: it answers *where
      this lands and what is already there*, which is two facts, and the second is the one the refusals
      are made of.
- [x] T012 [US2] Point `EnrollmentPlan.Describe` at `LaunchEntries.For` instead of the literal
      `servers["parley"]`, keeping every existing behaviour: same notes, same "already correct", same
      re-point message.
- [x] T013 [US2] Add to `tests/Parley.Cli.Tests/EnrollmentPlanTests.cs`: a repository with a legacy
      entry pinned to `alpha`, re-enrolled into `alpha`, produces a file whose only key is still
      `parley` — asserted on the key set, not on the contents.
- [x] T014 [P] [US2] Add a test that a legacy entry pinned to `alpha`, enrolled into `beta`, leaves the
      `parley` entry **byte-for-byte** unchanged and adds `parley-beta`.
- [x] T015 [US2] Confirm every existing case in `EnrollmentPlanTests` and `EnrolmentTests` still passes
      unedited. Where one had to change, the task list says which and why — an edited assertion is a
      changed promise.

**Checkpoint**: green. A pre-existing deployment is provably untouched.

**T012 folded into T017, and the phase boundary with it.** T012 asked for `Describe` to use the
precedence while keeping every existing behaviour, and the two cannot both hold: the precedence's whole
output is *which key to write at*, so using it and writing `parley` regardless is not using it. Kept as
a record rather than deleted, because the ordering it was trying to buy — prove the switch is faithful
before changing what it decides — is right in general and only fails here.

**Phase 3 and Phase 4 are one change, and the suite said so.** Writing a project-keyed entry breaks
withdrawal the moment it lands, because withdrawal knew one key: `Withdrawing_undoes_exactly_what_adding_did`
failed as soon as T017 did. That failure is the honest failing check for Phase 4 — it was not written
for it, but it named the defect before the fix — and the two phases were done together so the tree is
green at the commit rather than between two of them.

---

## Phase 3 (US1): A repository answers in a second project — P1

**Goal**: User Story 1, the whole feature. Includes the CLI's missing project (`research.md` R5),
without which this is terminal-only.

**Independent test**: `quickstart.md` end to end, on two brokers.

- [x] T016 [US1] Add `tests/Parley.Cli.Tests/MultiProjectEnrolmentTests.cs` driving User Story 1's
      three acceptance scenarios. Fails: a second enrolment is refused.
- [x] T017 [US1] Replace the "already the agent of project X" refusal in
      `src/Parley.Setup/EnrollmentPlan.cs` with the precedence's case 4. Keep the refusal's *reasoning*
      in a `<remarks>`: re-pointing is still refused, multiplicity is what is admitted, and the two were
      one only because a key held one entry.
- [x] T018 [US1] Add FR-009's refusal: a key `LaunchKey.IsOurs` would write, held by an entry with no
      `PARLEY_AGENT`, is refused and named. Never merged into, never replaced.
- [x] T019 [P] [US1] Add `tests/Parley.Cli.Tests/StrangerKeyTests.cs`: a hand-written server called
      `parley-alpha` survives an enrolment into `alpha`, the run refuses, and the message names the key.
- [x] T020 [US1] Add the endpoint map parameter to `EnrollmentPlan.Describe` and implement FR-008's four
      outcomes as a pure function of an address and that map. `Describe` does no I/O and must not start.
- [x] T021 [P] [US1] Add `tests/Parley.Cli.Tests/BrokerPinnedTests.cs`: the address is this project's,
      another project's, unknown, and there is no manifest. Four tests, four messages.
- [x] T022 [US1] Pass the map from `Enrolment.AddAsync`, which reads the manifest its caller already
      resolved.
- [~] T023 [US1] ~~Add `project` to `EnrollmentManifest`~~ — **superseded, not done, and the reason is
      recorded in `EnrollCommand.RunAsync`'s own doc comment.** `ProjectResolution.TrySelect` already
      refuses to guess when several projects are configured and none is named, so a second place to say
      which project would be a second answer to a question that is already answered — and the two would
      disagree first on the machine with more than one deployment, which is the only machine where it
      matters. The project is taken from the entry point's own `--project` resolution instead.
- [x] T024 [US1] Add `--project <name>` to `parley enroll apply` and `plan` in
      `src/Parley.Setup/EnrollCommand.cs`, resolved with the precedence `node_config` already documents,
      and **print which input won** — the same line, for the same reason: a run that names one thing and
      does another is worse than one that names none.
- [x] T025 [US1] Pass the project through `EnrollCommand`'s `Describe` call at line 803, which passes
      none today. This is `research.md` R5 and it is the task that makes User Story 1 performable in a
      shell. **This box was ticked before the work was done, and the gap was caught later while reading
      flag handling** — the feature would otherwise have shipped enrollable from the terminal only,
      which is exactly what R5 warned of.
- [x] T026 [P] [US1] Update `src/Parley.Cli/Program.cs` help text for the new flag.
- [x] T027 [P] [US1] ~~Add `tests/Parley.Cli.Tests/EnrollProjectFlagTests.cs`~~ — covered where the
      behaviour lives rather than in a file named after the flag: `ProjectResolutionTests` holds the
      precedence and which input won, `NoManifestParityTests` holds neither-given writing exactly what
      today writes, and `EnrollCommandTests.The_same_repository_enrolls_into_two_projects_independently`
      holds the flag reaching the key. A file added only to match a task name would have been a fourth
      place keeping the same rules in step.

**Checkpoint**: green. A repository can be enrolled twice from either surface.

---

## Phase 4 (US3): Withdrawal takes one project's entry and no other — P1

**Goal**: User Story 3. The path that deletes somebody's configuration, now with more to delete.

**Independent test**: enrol in two, withdraw from one, read the file.

- [x] T028 [US3] Add `tests/Parley.Cli.Tests/WithdrawOneProjectTests.cs` covering User Story 3's four
      acceptance scenarios. Fails: `WithdrawAsync` takes no project.
- [x] T029 [US3] Add the project to `Enrolment.WithdrawAsync` and remove only that entry, keeping the
      `NamedBy` guard and extending it to match on project as well as name. `<remarks>` keeps the
      existing paragraph about why the guard exists — a withdrawal once removed a different agent's
      entry and reported success.
- [x] T030 [US3] Implement FR-011: no project named and exactly one entry removes it; no project named
      and several refuses, naming every project the repository belongs to.
- [x] T031 [US3] Report how many parley entries and how many other servers remain, so a person can see
      what survived.
- [x] T032 [P] [US3] Update `src/Parley.Top/Withdrawing.cs` to name the project it is withdrawing from.
      The screen already knows which project it is in — `Shell.Current`, not another region's selection,
      which is the defect this repository has now recorded four times.
- [x] T033 [P] [US3] Add the terminal's half to `tests/Parley.Top.Tests`: withdrawing from one project
      leaves the other entry standing.

**Checkpoint**: green. Withdrawal is exact.

---

## Phase 5 (US4): A person can see everywhere a repository answers — P2

**Goal**: User Story 4. The scan stops answering with the first thing it finds.

- [x] T034 [US4] Add `tests/Parley.Cli.Tests/ScanReportsEveryProjectTests.cs`: a repository in three
      projects, one in none, one unreadable. Three distinct answers, per the contract's rule 3.
- [x] T035 [US4] Change the working-tree reader in `src/Parley.Setup/EnrollCommand.cs` (line 590) to
      return every enrolment.
- [x] T036 [US4] Change the committed-history reader (line 468, `git show <commit>:.mcp.json`) the same
      way, so "registered before" names every project it was registered in.
- [x] T037 [P] [US4] Render the list in the scan's output and in `parley enroll plan`, one line per
      project with the name and `owns` line that project knows it by.
- [x] T038 [P] [US4] Show the same in the terminal's add view, `src/Parley.Top/Adding.cs`, so the two
      surfaces do not disagree about what a repository already is (SC-005).

**Checkpoint**: green. Every surface answers the same question the same way.

---

## Phase 6: One workspace, one unattended start (FR-013)

**Goal**: the asymmetry of `data-model.md` — many launch entries, at most one catalogue entry.

- [x] T039 Add `tests/Parley.Cli.Tests/CatalogueUniquenessTests.cs`: a workspace already catalogued in
      `alpha` is refused for `beta`, with both projects, the workspace and the other catalogue's path in
      the message; and the same workspace spelled differently (trailing separator, `/` against `\`) is
      still refused, because `WorkspacePath.SameRepository` says it is the same repository.
- [x] T040 Add `src/Parley.Setup/Catalogues.cs`: given the machine's manifest, a workspace and the
      catalogue about to be written, find the same workspace in any other project's catalogue.
      `<remarks>` carries why the comparison is `WorkspacePath.SameRepository` and not a second one, and
      why this is not in `ProjectManifest.Validate` — a workspace is not in the manifest.
- [x] T041 Call it from `Enrolment.AddAsync` and from `EnrollCommand`'s apply, before anything is
      written. The launch entries are still written; it is the catalogue entry that is refused.
- [x] T042 [P] Add the reasoning to the refusal message: a node starts an agent with its workspace as
      its working directory, so two nodes starting one workspace is two models editing one checkout.

**Checkpoint**: green. SC-006.

---

## Phase 7: What a person is shown, and what is removed

- [x] T043 [P] Update the three illustrations for FR-014 — `README.md`, the plugin's
      `plugins/parley/commands/setup.md`, and `src/Parley.Web/Pages/Projects/Register.cshtml` — to show
      the project-keyed shape and say what the bare `parley` key means. README must still stand alone.
      **There are four, not three.** `src/Parley.Mcp/Program.cs` opens with a header comment showing
      the same file, which is the illustration an agent's own server carries; a task naming three of
      four is how the fourth comes to be the one that still says `parley`.
- [x] T044 Remove `parley console`, `--with-console`, `--console` and `--no-console`, which 008 moved to
      this version and called a commitment rather than a second reprieve. **`InstalledEntry` keeps
      reading `--console`**, for entries written before 0.19.0: the removal is stop writing, stop
      accepting, keep reading.
- [x] T045 Bump `VersionPrefix` to `0.22.0` in `Directory.Build.props`, in the same change.
- [x] T046 [P] Update `plugins/parley/commands/setup.md` and the plugin's skill for the removed verb,
      remembering that the deprecation message itself counts as a verb to the test that compares them.

---

## Phase 8: The review gate

Run **after the last source edit**, not alongside it. This has been caught by the owner three separate
times, and 008's gate found a defect introduced by the person writing the sentence forbidding it.

- [x] T047 Lens 1 (correctness and concurrency) — three consecutive runs with no finding above low, each
      probing something the previous did not. Logged in `REVIEW-LEDGER.md`, clean runs included.
- [x] T048 Lens 2 (security and data integrity) — three consecutive clean runs. **This is the lens that
      matters most here**: every path in this feature edits a file in somebody's repository, and three
      silent replacements of an MCP configuration are already recorded in this codebase's comments.
- [x] T049 Lens 3 (API surface and maintainability) — three consecutive clean runs.
- [x] T050 Verify on Linux in a container as well as Windows. CI is unavailable until renewal, and the
      Linux leg has already caught two defects Windows passed.
- [ ] T051 Verify on a live deployment: `quickstart.md` end to end, including that a repository enrolled
      before this change is untouched (SC-002) and that withdrawing from one project leaves the other
      answering (SC-003).

---

## Dependencies

```
Phase 1 (vocabulary)  ─┬─→ Phase 2 (US2, legacy)  ─→ Phase 3 (US1, writing many) ─→ Phase 4 (US3, withdrawal)
                       └─→ Phase 5 (US4, reading many)          │
                                                                └─→ Phase 6 (FR-013)
Phases 2–6 ─→ Phase 7 (illustrations, removal, version) ─→ Phase 8 (gate)
```

- Phase 1 blocks everything: it is the one implementation the other phases call.
- Phase 5 depends only on Phase 1 and may run beside Phases 2–4.
- Phase 3 depends on Phase 2 for the precedence.
- Phase 8 depends on the last source edit anywhere.

## Parallel opportunities

- T003 with T004; T014, T019, T021, T026, T027 within Phase 3; T032 with T033; T037 with T038.
- Phase 5 in its entirety against Phases 2–4, since it touches only the readers.

## MVP

**Phases 1, 2 and 3.** That is a repository answering in two projects with a legacy deployment
untouched — User Stories 1 and 2, both P1. Withdrawal (Phase 4) is P1 and must land before merge;
Phase 5 is P2 and could ship after, though it is small enough that splitting it would cost more than it
saves.
