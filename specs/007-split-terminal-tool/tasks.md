# Tasks: The surfaces are their own tools

**Feature**: `007-split-terminal-tool` | **Plan**: [plan.md](./plan.md) | **Spec**: [spec.md](./spec.md)

Organised by the four shippable steps in [research.md](./research.md) R7, not by layer. Each step is
independently mergeable and independently verifiable, and each bumps `VersionPrefix` in the same
change.

| Step | Phase | Delivers | Version |
|---|---|---|---|
| 1 | 2 — Foundational | The store leak, fixed alone | patch |
| 2 | 3 — US1 | `parley-top` | minor |
| 3 | 4 — US1/US3 | `parley-web-ui`, the rename | minor |
| 4 | 5 — US2 | The open-exchange warning | patch |

**`[P]`** marks tasks that can run at the same time as their neighbours: different files, no
dependency on an incomplete task in the same phase.

---

## Phase 1: Setup

- [x] T001 Read `specs/007-split-terminal-tool/plan.md`, `research.md` and `contracts/commands.md` before writing code; the seven decisions are settled and are not to be re-litigated during implementation
- [x] T002 Open a section for this branch in `REVIEW-LEDGER.md` with a counter table per lens, per the constitution's review gate
- [x] T003 Confirm the Linux container path works before relying on it: `podman run --rm -v "<repo>:/src:ro" mcr.microsoft.com/dotnet/sdk:10.0` builds and runs the suite, since CI is unavailable and Linux has already caught two defects Windows passed

---

## Phase 2: Foundational — Step 1, the store leak

**Ships alone and first.** This is a defect in a shipped build: the terminal reaches SQLite today.
It does not depend on the packaging change, and fixing it turns Step 2 into a file move rather than
a file move plus an untangling.

**Independent test**: the extended `GraphTests` walk fails before the move and passes after, and no
other behaviour changes.

### Probe first — the constitution requires the failure to be seen

- [x] T004 Extend `tests/Parley.Client.Tests/.../GraphTests` to walk transitively from the terminal's sources (`src/Parley.Cli/Top`) and fail when a store is reachable — **run it and watch it fail** on `NodeCatalogue` → `Parley.Headless` → `Parley.Store`, and record the failure text in `REVIEW-LEDGER.md` before changing any source
- [x] T005 Record in the same ledger entry why the existing walk could not have caught this: it starts from `Parley.Client` and `Parley.Web`, and the terminal sits inside `Parley.Cli`, which owns the store legitimately

### The move

- [x] T006 Move `src/Parley.Headless/NodeCatalogue.cs` to `src/Parley.Configuration/NodeCatalogue.cs`, changing only the namespace; it uses `System.Text.Json` and `Parley.Core` and references nothing else in `Parley.Headless`
- [x] T007 Update the `using` in every caller: `src/Parley.Cli/EnrollCommand.cs`, `FleetCommand.cs`, `NodeCommand.cs`, `Program.cs`, `ProjectResolution.cs`, `Top/AgentsRegion.cs`, `Top/Shell.cs`, `src/Parley.Headless/AgentFleet.cs`
- [x] T008 Confirm `src/Parley.Headless/Parley.Headless.csproj` keeps its `Parley.Store` reference — `AgentFleet` needs it — and add a `<remarks>` on `AgentFleet` saying so, since the assembly's one remaining reason to hold the store should be findable
- [x] T009 [P] Add a `<remarks>` to the moved `NodeCatalogue` recording why it lives in `Parley.Configuration`: a per-machine file two programs read, and a reader the terminal must reach without a route to a store
- [x] T010 [P] Reword the remark at `src/Parley.Client/Views/AgentStanding.cs:107`, which cites a constraint that will no longer be true. Keep the `IReadOnlySet<string>` boundary and say why it is still right — `Standing.Join` needs role names, not a file reader

### Make it checkable

- [x] T011 Add a `GraphTests` walk from `Parley.Configuration` asserting it reaches neither a store nor the transport; it is about to be referenced by every tool, so anything acquired there is acquired by all four
- [x] T012 Run the extended walk and confirm it now passes, and that the non-vacuity assertions in `GraphTests` still hold — a walk that resolves nothing passes for the wrong reason
- [x] T013 Bump `VersionPrefix` in `Directory.Build.props` (patch) in this same change
- [x] T014 Record the fix in `CLAUDE.md` under the active plan: what the leak was, why nothing caught it, and where the reader now lives
- [x] T015 Full suite on Windows and in the Linux container; both green before merge

### Review gate for Step 1

- [x] T016 Lens 1 (correctness and concurrency) × 3 consecutive clean, each probing something new, logged in `REVIEW-LEDGER.md`
- [x] T017 Lens 2 (security and data integrity) × 3 consecutive clean, logged
- [x] T018 Lens 3 (API surface and maintainability) × 3 consecutive clean, logged

**Checkpoint**: the terminal cannot reach a store, proved by a check that failed before the fix
(SC-004). Nothing about packaging has changed yet.

---

## Phase 3: User Story 1 — Step 2, `parley-top` (Priority: P1) 🎯 MVP

**Goal**: a terminal change installs without stopping the broker.

**Independent test**: leave an exchange open and delivered, install a terminal-only change, and
confirm the broker was not restarted, no agent was told to quit, and no exchange changed state.

### The project

- [x] T019 [US1] Create `src/Parley.Top/Parley.Top.csproj` referencing `Parley.Client`, `Parley.Configuration` and `Parley.Transport` only, packed as a tool with command `parley-top`
- [x] T020 [US1] Move `src/Parley.Cli/Top/*` to `src/Parley.Top/`, changing namespaces from `Parley.Cli.Top` to `Parley.Top`
- [x] T021 [US1] Add the entry point in `src/Parley.Top/Program.cs`, taking the arguments `parley top` takes today and keeping the refusal when input is redirected
- [x] T022 [US1] Remove `Top/` from `src/Parley.Cli` and delete any reference it leaves behind

### The tests move with it

- [x] T023 [US1] Create `tests/Parley.Top.Tests` and move `tests/Parley.Cli.Tests/Top/*` into it, including `Driving`, `Walking` and `Drawn`
- [x] T024 [US1] Leave `Loopback` and `ToolProcess` in `tests/Parley.Cli.Tests`, referenced by the new suite; they are shared with the CLI's own tests
- [x] T025 [US1] Assert the count is unchanged across the move: record the terminal test count before and after and confirm they match, so a test lost in the move fails rather than disappears

### The wrapper

- [x] T026 [US1] Add the forwarding wrapper in `src/Parley.Cli/Program.cs`: `parley top` resolves `parley-top`, starts it with the arguments given, and returns its exit code, parsing none of them
- [x] T027 [US1] When `parley-top` is not installed, say which tool provides the command and how to install it, rather than "unknown command", which would be untrue
- [x] T028 [P] [US1] Test the wrapper both ways: forwards when present, explains when absent

### Install

- [x] T029 [US1] Extend the `$tools` table in `install.ps1` with a `Surface` column and a `parley-top` row
- [x] T030 [US1] Add `-Surfaces terminal | web-ui | both | none` to `install.ps1`, defaulting to `both` on Windows and `web-ui` on Linux, and document the default in the script's help
- [x] T031 [US1] Add per-tool reporting — `installed`, `unchanged`, `skipped` — so "the terminal was replaced and the broker was not" is distinguishable from "nothing happened"
- [x] T032 [US1] Generalise the holder check to any process named after a tool being replaced, so a running terminal is caught as adapters are; the message names what will be closed rather than counting it
- [x] T033 [US1] Update `-Uninstall` to remove every tool in the table, leaving no scheduled task pointing at a missing path

### Verify the point of the feature

- [x] T034 [US1] With a broker running and an exchange open and delivered, install a terminal-only change and confirm zero broker restarts, zero agents ended, zero exchanges changed (SC-001)
- [x] T035 [US1] Confirm the terminal shows the same deployment afterwards, including the open exchange
- [x] T036 [US1] Bump `VersionPrefix` (minor — the tool set a machine installs is a breaking change to how it is installed)
- [x] T037 [US1] Update `README.md` and `CLAUDE.md` for the new tool and the `-Surfaces` argument
- [x] T038 [US1] Full suite on Windows and Linux

### Review gate for Step 2

- [x] T039 [US1] Lens 1 × 3 consecutive clean, logged
- [x] T040 [US1] Lens 2 × 3 consecutive clean, logged
- [x] T041 [US1] Lens 3 × 3 consecutive clean, logged

**Checkpoint**: SC-001 met for the terminal. This is the MVP — the feature delivers value here even
if Steps 3 and 4 never ship.

---

## Phase 4: User Stories 1 and 3 — Step 3, `parley-web-ui` and the rename

**Goal**: the same for the web UI, and a machine chooses which surfaces it carries.

**Independent test**: a broker-only machine runs agents and carries no ASP.NET Core; a machine with
the web UI reaches it after a logout and logon with nobody starting it by hand.

### The address constant, first — it unblocks the reference removal

- [ ] T042 [US1] Move the loopback address constant from `src/Parley.Web/ConsoleHost.cs` to `src/Parley.Configuration`, keeping the `<remarks>` about §16 with it
- [ ] T043 [US1] Point `src/Parley.Web/ConsoleHost.cs`, `src/Parley.Cli/Program.cs` and `src/Parley.Top/Shell.cs` at the moved constant
- [ ] T044 [US1] Extend `ConsoleAddressTests` to assert one source across the packages; it exists for the defect where a hand-copied `5045` met a real `5280` and the browser opened a dead port

### The project

- [ ] T045 [US3] Create `src/Parley.WebUi/Parley.WebUi.csproj` — a thin host over `Parley.Web` — packed as a tool with command `parley-web-ui`
- [ ] T046 [US3] Add its entry point, defaulting to the loopback address from `Parley.Configuration`
- [ ] T047 [US3] Remove the `ProjectReference` to `Parley.Web` from `src/Parley.Cli/Parley.Cli.csproj`
- [ ] T048 [US3] Add a `GraphTests` assertion that `Parley.Cli` cannot reach `Parley.Web`, and watch it fail before T047 lands
- [ ] T049 [US3] Add the packing check for SC-005: pack the `parley` tool and assert no ASP.NET Core assembly is in the output — reading the project file proves only the direct reference is gone

### The child-process launch

- [ ] T050 [US1] Change `serve --with-web-ui` in `src/Parley.Cli/Program.cs` to start `parley-web-ui` as a child, passing the address the broker **actually** bound, not the one it was asked for
- [ ] T051 [US1] Do not wait on the child and do not restart it; end it when the broker ends, leaving no orphan
- [ ] T052 [US1] When the child cannot start — not installed, port held, missing runtime — report it and keep serving; the broker's tool must learn only the command name, never the assembly (FR-015)
- [ ] T053 [US1] **Probe it**: hold the web UI's port, start the broker, and confirm agents are unaffected and the broker reports the failure (SC-008)

### The rename

- [ ] T054 [US3] Rename the command and flag to `parley web-ui` and `--with-web-ui` (`--web-ui <url>`) in `src/Parley.Cli/Program.cs`
- [ ] T055 [US3] Keep `parley console` and `--with-console` working, warning once and naming the new spelling; the owner's machine has the old flag in a scheduled task right now
- [ ] T056 [US3] Record the deprecation in `CLAUDE.md` with the version it is removed at, so it is a decision with a date rather than a kindness that becomes permanent
- [ ] T057 [P] [US3] Add a test asserting `OperatorConsole` and `ConsolePicker` are **not** renamed; "console" means three things here and only one is a browser surface
- [ ] T058 [US3] Update `autostart install` in `src/Parley.Setup/AutostartPlan.cs` to register `--with-web-ui`
- [ ] T059 [US3] Confirm the web UI comes up at logon where it is installed (SC-009, FR-018)

### Every document that names it

- [ ] T060 [P] [US3] Update `README.md`
- [ ] T061 [P] [US3] Update the broker's startup block and help text in `src/Parley.Cli/Program.cs`
- [ ] T062 [P] [US3] Update the plugin's prose under `plugins/parley`
- [ ] T063 [US3] Add the mechanical check for SC-006 and SC-010: everything naming a surface names the same command, and no user-facing text calls the web UI a console

### Close out

- [ ] T064 [US1] Verify SC-001 for the web UI: a web-UI-only change installs with the broker untouched
- [ ] T065 [US3] Verify a broker-only machine runs agents and carries no web stack (SC-005)
- [ ] T066 [US3] Verify the version-mismatch report names both versions (SC-007, FR-011), extending the wording `ProjectsRegion.Describe` already uses
- [ ] T067 [US3] Bump `VersionPrefix` (minor)
- [ ] T068 [US3] Full suite on Windows and Linux

### Review gate for Step 3

- [ ] T069 [US3] Lens 1 × 3 consecutive clean, logged
- [ ] T070 [US3] Lens 2 × 3 consecutive clean, logged
- [ ] T071 [US3] Lens 3 × 3 consecutive clean, logged

**Checkpoint**: all four tools exist, a machine chooses its surfaces, and neither surface can reach
a store.

---

## Phase 5: User Story 2 — Step 4, the open-exchange warning (Priority: P2)

**Goal**: an install that must stop the broker says so first, and says what is in flight.

**Independent test**: with an exchange open and delivered, run an install that touches the broker
and confirm the warning names the consequence and the exchange before anything stops.

**Separable**: this is the only step that could be dropped without making the rest incoherent, which
is why it is last.

- [ ] T072 [US2] Add a step to `install.ps1` that asks the broker what is open before stopping it, using the operator surface the terminal's conversation list already uses, filtered to open exchanges
- [ ] T073 [US2] Name the open exchanges and state the consequence — a delivered question's agent will not be there to answer it — before stopping anything
- [ ] T074 [US2] When the broker cannot be reached, say it **could not ask** rather than implying nothing is in flight; a guard that fails open must say so
- [ ] T075 [US2] Warn rather than refuse: `-Force` already means the decision belongs to the person making it, and blocking an upgrade is not this script's job
- [ ] T076 [US2] Do not show the warning when only a surface changed — there is nothing to stop
- [ ] T077 [US2] Confirm the store is never opened by the script; asking the broker is the boundary, and reading the store directly for a progress message would break it
- [ ] T078 [US2] Verify SC-003: warned first, in every case where an exchange is open
- [ ] T079 [US2] Bump `VersionPrefix` (patch)
- [ ] T080 [US2] Full suite on Windows and Linux

### Review gate for Step 4

- [ ] T081 [US2] Lens 1 × 3 consecutive clean, logged
- [ ] T082 [US2] Lens 2 × 3 consecutive clean, logged
- [ ] T083 [US2] Lens 3 × 3 consecutive clean, logged

---

## Phase 6: Polish & Cross-Cutting

- [ ] T084 [P] Every new public member documented, `<remarks>` carrying why rather than restating the signature; the build enforces the presence, not the quality
- [ ] T085 [P] Case-insensitive grep for the originating project's names across new and changed files, including untracked ones — `git grep` sees tracked files only
- [ ] T086 [P] Update `specs/007-split-terminal-tool/quickstart.md` if any command differs from what shipped
- [ ] T087 Update `CLAUDE.md`'s layout section for `Parley.Top` and `Parley.WebUi`, saying what each may reference and why
- [ ] T088 Confirm `parley --version` reports the same version for all four tools, since they come from one solution
- [ ] T089 Final full suite on Windows and Linux against the merged result

---

## Dependencies & Execution Order

```
Phase 1 (Setup)
   └─> Phase 2 (Step 1 — the leak)          ships alone, patch
          └─> Phase 3 (Step 2 — parley-top)  ships, minor          🎯 MVP
                 └─> Phase 4 (Step 3 — web UI + rename)  ships, minor
                        └─> Phase 5 (Step 4 — the warning)  ships, patch
                               └─> Phase 6 (Polish)
```

**Why the order is a chain rather than a fan.** Step 2 moves the terminal, which is far easier once
Step 1 has removed its store route. Step 3 removes `Parley.Cli`'s reference to `Parley.Web`, which
needs the address constant moved first (T042 before T047). Step 4 touches only `install.ps1`, which
Steps 2 and 3 both change.

**Within phases**, `[P]` tasks are independent: T009/T010, T028, T057, T060/T061/T062, T084/T085/T086.

**T042–T044 must precede T047.** Removing the project reference before moving the constant breaks
the build.

**T004 must precede T006.** The failing check comes first; that is the constitution's rule, not a
preference.

---

## Implementation Strategy

**MVP is Phase 2 + Phase 3.** Step 1 alone fixes a real defect and is worth shipping on its own
merits. Step 2 delivers the feature's whole point for the surface that actually churns — 29 of 44
changed source files last cycle. Stopping there would leave the web UI as it is today and still
collect most of the value.

**Each step merges separately**, with its own version bump and its own review gate. The gate is not
run once at the end: a step that ships is a step that was reviewed.

**Verify on Linux every time.** CI cannot run — GitHub reports a billing failure — and the container
has already caught two defects that Windows passed clean, one of them a real one. Windows-only
verification is not verification.

**Do not batch the version bumps.** `dotnet tool update` silently no-ops when the version has not
moved, so a step that ships without a bump cannot be installed by the documented path, and the
failure is silent on the machine already running the old build.
