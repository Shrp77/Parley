# Tasks: A tabbed terminal, and agents added or removed from it

**Input**: Design documents from `specs/005-tabbed-terminal-agents/`
**Prerequisites**: [plan.md](./plan.md), [spec.md](./spec.md), [research.md](./research.md),
[data-model.md](./data-model.md), [contracts/operations.md](./contracts/operations.md),
[contracts/surface.md](./contracts/surface.md), [quickstart.md](./quickstart.md)

**Tests**: Included, and not optional here, for the reason 004's task list gave: `dotnet test` is
this repository's whole lint gate, and its highest-value findings came from *writing the
malicious case and watching it be admitted*, never from inspection. Every guard below has a probe
beside it, and every door — a typed name, a second confirmation, a refusal — has the case that
tries to walk through it.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel — different files, no dependency on an incomplete task
- **[Story]**: the user story this serves (US1–US4)

## What was verified before these tasks were written

The plan asserted where things are and what already exists. Each was checked in the tree at
`c51e776` rather than trusted:

| Claim | Check | Result |
|---|---|---|
| `Spectre.Console` is used only under `src/Parley.Cli/Top` | grep `Spectre` across `src/**/*.cs` | **Confirmed** — all 16 files under `Top`, none outside; `ConsolePicker` draws with `System.Console` |
| The rewrite is "~16 view files and ~20 tests" | count | **16 sources, 23 test files** under `Top` (tests: 21 classes plus `Driving.cs`, `Eventually.cs`, `LogFile.cs` helpers) |
| `Parley.Mcp` already reads `PARLEY_PROJECT` and the enroll command does not write it | grep both | **Confirmed** — `ServerOptions.ProjectVariable` reads it; zero hits in `EnrollmentPlan.cs` and `EnrollCommand.cs` |
| The three operations are already on `BrokerClient` | grep | **Confirmed** — `RetireAsync`, `ReinstateAsync`, `PurgeAsync`, plus `DescribeRegistryAsync` and `WaitingAsync` for the description |
| A seat is distinguishable by adapter | `Seat.Adapter` | **Confirmed** — `parley-console`, in `src/Parley.Client/Seating/Seat.cs` |
| The idle rule is one constant both surfaces share | `SeatBook.IdleAfter` | **Confirmed** — twenty minutes, in `src/Parley.Client/Seating/SeatBook.cs` |
| FR-018 needs no new code | `ConsolePicker.Available` | **Reuse** — already `!IsInputRedirected && !IsOutputRedirected`, and `TopCommand.Interactive` already delegates to it |
| The serve watcher's interval is reachable from `NodeCommand` | `ServeCommand.WatchInterval` | **Confirmed** — `internal static`, two seconds |
| `Roster.State` is where §14's presence words come from | `src/Parley.Client/Rendering/Roster.cs` | **Confirmed**; `AgentsView.State` in `Top` only maps them to colours |
| `NodeCatalogue` is in `Parley.Cli` | grep `class NodeCatalogue` | **Corrected** — it lives in `src/Parley.Headless/NodeCatalogue.cs`, which `Parley.Cli` references. The watch in T014 reads through `NodeCatalogue.ReadFrom` and nothing moves |
| `Onboarding` calls the enroll command rather than reimplementing it | grep | **Confirmed** — `EnrollCommand.RunAsync` from `Onboarding.cs`; the dialog port in T034 keeps that call |

One thing the plan leaves open and this list does not close: **which component factory runs
Terminal.Gui without a console** (research R2). That is T005, and it is the reason Phase 2 has two
halves that do not wait on each other.

---

## Phase 1: Setup

**Purpose**: the toolkit is on the graph and the version moves. Nothing is drawn yet.

- [X] T001 Add `Terminal.Gui` `2.4.17` to `Directory.Packages.props` in the `Terminal` group
      beside `Spectre.Console` (which stays until T044), pinned exactly. Rewrite the comment above
      the group: the Spectre comment says the package "brings nothing with it", and that stops
      being true — say the graph is twenty-four packages, that no transitive pin binds today
      because none of the `Microsoft.Extensions.*` arrivals has a `PackageVersion` entry, and that
      `NuGetAudit` prices every one of them on each build (research R1). No CLI flag in the
      comment — a double hyphen inside an XML comment breaks the file.
- [X] T002 Reference `Terminal.Gui` from `src/Parley.Cli/Parley.Cli.csproj` beside the existing
      `Spectre.Console` reference.
- [X] T003 Bump `VersionPrefix` in `Directory.Build.props` from `0.16.9` to `0.17.0`. The minor
      moves: `parley top` changes wholesale, a shipped package is removed, and `parley node`
      behaves differently when its catalogue changes. In this commit, not at the end.
- [X] T004 Verify `dotnet build` succeeds with zero warnings and
      `dotnet list package --vulnerable --include-transitive` reports nothing, so a later failure
      is attributable to the rewrite rather than to the dependency.

---

## Phase 2: Foundational

Two halves. **2A gates every task under `Top`** and nothing there starts until it passes. **2B
does not touch the toolkit at all** and runs beside 2A; it gates US3 and US4 and is the half most
worth probing adversarially, because it ships behaviour outside the surface.

### 2A — The spike, and the harness it becomes

**Purpose**: prove the surface can be driven by a test on both platforms before a line of it is
written. Research R2 found that 2.4.17 ships `IApplication`, `ApplicationImpl(IComponentFactory,
ITimeProvider)` documented *for testing*, `GetInputInjector()`, `RaiseKeyDownEvent`,
`LayoutAndDraw` and `ToString()` *as rendered by the driver* — but no named headless driver
(`DriverRegistry.Names` is `WINDOWS`, `DOTNET`, `ANSI`). Whether a factory the library ships can
run with no console attached, or the tests must write one, is the open question.

- [X] T005 Spike, in `tests/Parley.Cli.Tests/Top/Driving.cs` (rewritten from the Spectre
      `TestConsole` helper): build an `IApplication` with an `IComponentFactory` that has no
      console, add a `Window` with a `Label` and a `TextField`, inject the key `a` through
      `GetInputInjector()` (or `RaiseKeyDownEvent` if injection needs a run loop), call
      `LayoutAndDraw`, and assert the rendered `ToString()` contains the label's text and the
      field now holds `a`. **Record the answer in `research.md` R2** — which factory, and whether
      it is the library's or ours. Never touch the static `Application` class or
      `DefaultKeyBindings`: xUnit runs classes in parallel and both are process-global.
- [X] T006 Run T005 on Windows, then under podman (see memory: `verify-linux-locally-with-podman`
      — CI billing was lapsed on 2026-09-03; verify it is back before relying on it). Drivers are
      where platforms differ. **If no factory can run without a console on either platform**,
      stop, record the fallback research R2 names (`Init(DOTNET)` under a pseudo-terminal) as a
      low finding in `REVIEW-LEDGER.md`, and decide with the owner before T007.
- [X] T007 Grow `tests/Parley.Cli.Tests/Top/Driving.cs` into the harness every later test uses:
      `Driving.Open(...)` builds the application over an injectable `ITimeProvider`
      (`VirtualTimeProvider` when the test needs to advance twenty minutes); `Driving.Find(app,
      title)` returns a view by its `Title`; `Driving.Row(view, text)` selects a row in a list by
      its text; `Driving.Tab(app)`, `Driving.Enter(app)`, `Driving.Escape(app)`, `Driving.Type(app,
      text)`; `Driving.Click(app, view)` raises a mouse event at the view's frame centre; and
      `Driving.Screen(app)` returns `ToString()` with every attribute stripped. **Never by
      coordinates** (contracts/surface.md): a region that moves when the layout stacks must not
      move a test. Keep `Eventually.cs` and `LogFile.cs` as they are.
> **T008 moved, 2026-09-04.** Rewriting the entry point before a shell exists to run would leave
> the tool broken and every one of 004's twenty-one test classes uncompilable, since they drive
> `DriveAsync(IAnsiConsole, ...)`. It is done with T023, which is the first moment there is
> something for it to run. T009 and T010 are unaffected and stay here.

- [X] T008 Rewrite `src/Parley.Cli/Top/TopCommand.cs`: `RunAsync` keeps the interactivity refusal
      (`Interactive`, unchanged, FR-018) and the Ctrl+C reasoning already written there; it creates
      an `IApplication` through `Application.Create(TimeProvider.System)` and hands it to a new
      `DriveAsync(IApplication app, ...)` overload that a test calls with its own. The static
      `Application` class is never referenced; `AnsiConsole` goes.
- [X] T009 Rewrite `src/Parley.Cli/Top/Safe.cs` for the toolkit: the toolkit's hot-key specifier
      and any control character that could move the cursor or reshape a line are neutralised, on
      the same reasoning the Spectre version records (a log line or an agent's `owns` is text
      somebody else wrote). Re-drive `tests/Parley.Cli.Tests/Top/LogInjectionTests.cs` against it
      once T035 lands; until then a unit test on `Safe` alone.
- [X] T010 Write `tests/Parley.Cli.Tests/Top/NonInteractiveTests.cs` against the new
      `TopCommand`: a redirected console is refused with the sentence naming what to run instead,
      before any toolkit type is constructed.

**Checkpoint**: a test can open the surface, press a key, and read the screen, on both platforms.

### 2B — Behaviour outside the surface (independent of the toolkit)

**Purpose**: the three findings the plan made by reading the code. Each has its own tests and
lands without a view existing.

- [X] T011 [P] Create `src/Parley.Cli/Enrolment.cs`: an `internal static class Enrolment` with
      `Add(RepositoryPlan plan, string catalogueFile, ...)` — the per-entry body of
      `EnrollCommand.ApplyAsync` (writing `.mcp.json` from `EnrollmentPlan.Describe`'s result,
      preserving a byte-order mark the file had, and adding `agents.<name> = { command, workspace
      }` to the catalogue) — and `Withdraw(string workspace, AgentName agent, string
      catalogueFile)`, its inverse: remove `mcpServers.parley` leaving every other server and the
      file itself, remove the catalogue entry, **never touch the instructions file** (research
      R5). Both return what changed as a list of paths so a caller can print it. `<remarks>` says
      why it was extracted: one writer, FR-008.
- [X] T012 Make `EnrollCommand.ApplyAsync` call `Enrolment.Add` per entry in
      `src/Parley.Cli/EnrollCommand.cs`, passing the project it resolved when it has one. Its
      output must not change: `tests/Parley.Cli.Tests/EnrollCommandTests.cs` runs unchanged, and
      a test edited to accommodate the extraction has stopped testing it.
- [X] T013 [P] `EnrollmentPlan.Describe` in `src/Parley.Cli/EnrollmentPlan.cs` gains an optional
      `string? project`. When given, `PARLEY_PROJECT=<project>` is written into the `parley`
      server's `env`. When the existing entry carries a different `PARLEY_PROJECT`, or a
      `PARLEY_BROKER` naming another endpoint, the plan is refused naming that project (FR-009,
      research R4). An entry naming this project is "already correct"; one naming neither is
      re-pointed and said.
- [X] T014 `src/Parley.Cli/NodeCommand.cs` watches its catalogue: poll the file's stamp at
      `ServeCommand.WatchInterval`; on change, end the `Join` stream and join again with the new
      `StartableRoles`, keeping its resident agents (research R3). An unreadable or invalid
      catalogue mid-run is reported and the last good roles are kept — a node that dropped every
      role because somebody was half-way through saving the file is worse than one that lagged a
      poll.
- [X] T015 [P] Write `tests/Parley.Cli.Tests/EnrolmentTests.cs`: `Add` then `Withdraw` leaves
      `.mcp.json` and the catalogue byte-identical to before (other servers, the byte-order mark,
      indentation); `Withdraw` on a repository with no `parley` entry reports "already absent"
      and writes nothing; `Withdraw` never rewrites `CLAUDE.md`; two `Add`s of the same workspace
      under different names are refused by the catalogue check. **Probes:** a `.mcp.json` whose
      `mcpServers` is a string (refused as unmergeable, unchanged on disk); a catalogue entry
      whose workspace points at a *different* directory than the one being withdrawn (the entry
      is left alone and the report says whose it is).
- [X] T016 [P] Add to `tests/Parley.Cli.Tests/EnrollmentPlanTests.cs`: `Describe` with a project
      writes `PARLEY_PROJECT`; against an entry carrying another project's name, refuses and names
      it; against `PARLEY_BROKER` pointing elsewhere, refuses; against an entry with neither,
      re-points and says so; without a project, writes exactly what it wrote before (the existing
      cases stay green).
- [X] T017 [P] Write `tests/Parley.Cli.Tests/NodeCatalogueWatchTests.cs`, driven through the
      loopback broker the way `HostedNodeTests.cs` is: a role appended to the catalogue file is
      announced within two watch intervals and the registry's `startable` roles reflect it,
      with no restart; the gap between joins reads as `stranded` for under a second and the
      resident agents survive it. **Probes:** the file replaced by invalid JSON keeps the previous
      roles and logs the refusal; a role's workspace edited to another agent's directory is
      announced as the file says — the node announces, it does not adjudicate — and the test
      records that this is by design (§17 question 13 keeps the catalogue local).

**Checkpoint**: `parley enroll apply` writes `PARLEY_PROJECT` on a manifest-mode machine; a node
picks up a catalogue edit; one code path writes and withdraws the two files.

---

## Phase 3: User Story 1 — See the deployment at once and move through it (P1) 🎯 MVP

**Goal**: four regions on one screen, Tab between them, arrows within, Enter opens, Escape backs
out, the mouse does the same, and every state is a phrase.

**Independent Test**: against a manifest naming two projects, one answering and one not, reach a
named conversation's transcript with Tab, arrows and Enter only, then again with the mouse only;
both end on the same transcript showing author, recipient and visibility in words (SC-001).

- [X] T018 [US1] Create `src/Parley.Cli/Top/Shell.cs`: the top-level window holding the four
      regions and a status line. `Shell.Threshold = 80` columns; at or above, side by side
      (Projects left, Agents over Conversations centre, Transcript right); below, a `TabView`
      whose pages are the same four view objects in the same order (research R10). The status
      line always reads `Focus: <region title>` and lists the focused region's actions with their
      letters. Every phrase is a `const string` on the view that draws it (contracts/surface.md).
      Tab and Shift+Tab move focus in region order and round again; Ctrl+Q quits.
- [X] T019 [P] [US1] Create `src/Parley.Cli/Top/ProjectsRegion.cs` from `ProjectList.cs`: a list
      titled `Projects` of name, whether the broker answers (`not answering` within five seconds,
      the probe already in `ProjectHealth`), and description. The first project is selected on
      open with no key pressed. Selection survives a refresh. Actions come in US2; this task is
      the list.
- [X] T020 [P] [US1] Create `src/Parley.Cli/Top/AgentsRegion.cs` from `AgentsView.cs`: a table
      titled `Agents` of the union of `DescribeRegistry` and the selected project's catalogue,
      keyed by name, with the three states of data-model.md as phrases — §14's presence through
      `Roster.State` for a row in both; `catalogued, not yet registered` for catalogue-only; the
      presence plus `not in this catalogue` for registry-only — and `seat` for a row whose
      adapter is `Seat.Adapter`. The catalogue is read fresh on every draw and a project with no
      catalogue draws the registry alone. Colour may repeat the phrase; nothing is said by colour
      alone.
- [X] T021 [P] [US1] Create `src/Parley.Cli/Top/ConversationsRegion.cs` from
      `ConversationList.cs`: a list titled `Conversations`, most recent first. Enter opens the
      selected conversation in the transcript region and the list does not move.
- [X] T022 [P] [US1] Create `src/Parley.Cli/Top/TranscriptRegion.cs` from `TranscriptView.cs`:
      a view titled `Transcript` rendering `Parley.Client`'s transcript with author, recipient
      and per-message visibility in words, empty on open, and saying `not answering` when the
      project's broker is. A column that does not fit is dropped with its heading, never cut
      mid-word (004's narrow-terminal rule).
- [X] T023 [US1] Wire the four into `Shell` in `src/Parley.Cli/Top/Shell.cs` and `TopCommand.
      DriveAsync`: selecting a project redraws the other three for it; a project whose broker is
      not answering leaves every other project selectable; delete `ProjectList.cs`,
      `AgentsView.cs`, `ConversationList.cs`, `TranscriptView.cs`.
- [X] T024 [P] [US1] Rewrite `tests/Parley.Cli.Tests/Top/NavigationTests.cs` through `Driving`:
      the keyboard journey of SC-001 (Tab ×2, arrows, Enter) ends on the named transcript; the
      same journey by `Driving.Click` alone ends on the same screen text; Shift+Tab goes back;
      Tab from Transcript returns to Projects; opening a second conversation leaves the list
      selection one arrow from the first (scenario 3).
- [X] T025 [P] [US1] Write `tests/Parley.Cli.Tests/Top/LayoutTests.cs`: at 79 columns the four
      regions are tab pages and Tab still visits all four; at 80 they are side by side; at 40 no
      region's screen text contains an ellipsis and every title is present (scenario 5); the same
      `Driving.Find` calls succeed in both layouts.
- [X] T026 [P] [US1] Rewrite `tests/Parley.Cli.Tests/Top/ColourTests.cs`: with every attribute
      stripped, the screen still contains `Focus: Projects`, each presence phrase, `not
      answering` and `seat` wherever the coloured render showed them (SC-006).
- [X] T027 [P] [US1] Rewrite `tests/Parley.Cli.Tests/Top/DeadBrokerTests.cs`: a project whose
      endpoint nothing listens on says `not answering` in all three of its regions within five
      seconds of selection, and Tab then reaches the other project's conversations (SC-008,
      scenario 4).
- [X] T028 [P] [US1] Write `tests/Parley.Cli.Tests/Top/CatalogueJoinTests.cs`: the three rows of
      data-model.md drawn from a registry and a catalogue built to produce each; a seat row shows
      `seat`; a project without a catalogue draws the registry alone. **Probe:** a catalogue
      entry whose name is not a well-formed `AgentName` is shown as a row with the reason rather
      than crashing the draw or being silently dropped.

**Checkpoint**: MVP. A person can read a deployment from one screen. Scenario 6 (the idle seat)
is verified in US2 because a seat has to be taken first, and taking one is scenario 1 there.

---

## Phase 4: User Story 2 — Take part from the same screen (P2)

**Goal**: everything 004 did, from the region that shows the thing, with the same guards and
words. This is the parity contract (SC-002): every 004 journey ends in the same registry and
store state, verified through a broker.

**Independent Test**: each 004 acceptance scenario replayed through `Driving` with its
broker-side assertions unchanged.

- [X] T029 [US2] Port `Seating.cs` into `src/Parley.Cli/Top/AgentsRegion.cs`: *Take a seat* opens
      a dialog for the name; `SeatBook`'s guards refuse a name the registry holds for a real
      agent, with the reason; *Leave your seat* releases it; the region shows the seat within
      five seconds. The frame touches the seat on every draw so the idle rule runs on the
      application's `TimeProvider`. Delete `Seating.cs`.
- [X] T030 [US2] Port `Waiting.cs`, `Answering.cs` and `Composer.cs` into
      `src/Parley.Cli/Top/TranscriptRegion.cs`: *What is waiting for you*, *Answer*, *Ask*,
      *Tell*, each a dialog on the transcript region, sending through `SeatVoice` exactly as 004
      does, with the message cap refusal and the concluded-underfoot handling unchanged. Delete
      the three files.
- [X] T031 [US2] Port `Naming.cs` into `src/Parley.Cli/Top/ProjectsRegion.cs` as two dialogs:
      *Name this deployment* and *Create a project*, pre-filled and validated as typed with the
      `NameProblem`/`StoreProblem`/`EndpointProblem` checks unchanged, Enter accepting every
      proposal (scenario 3). Delete `Naming.cs`.
- [X] T032 [US2] Redraw `src/Parley.Cli/Top/Removing.cs` as a dialog on the projects region:
      keep first and Enter's answer, forget deletes nothing, destroy takes the project's name
      typed back — the rule unchanged, the word `destroy` still confined to this one file
      (scenario 4). `Removing.Choices` stays.
- [X] T033 [P] [US2] Redraw `src/Parley.Cli/Top/LogTail.cs` into a view opened from the projects
      region (*Follow the broker log*): the follow logic and the file choice (the manifest-serving
      entry's log, `LogLocationTests`) unchanged; lines appear on the application's timer tick
      without a key; Escape closes it. The `Console.ReadLine` stop recorded as a low finding in
      `REVIEW-LEDGER.md` on 2026-09-03 goes away with the redraw, and `LogTailTests` can now
      drive past the first line.
- [X] T034 [P] [US2] Redraw `src/Parley.Cli/Top/Onboarding.cs` as a dialog (scan / plan / apply
      of a manifest) that still calls `EnrollCommand.RunAsync`; `src/Parley.Cli/Top/Launch.cs`
      (*Open the web console in a browser*) as an action on the projects region.
- [X] T035 [US2] Port `ProjectMenu.cs`'s remaining entries onto the status line and the regions
      that own them, keeping its string constants as the action labels; delete `ProjectMenu.cs`.
      At this point every file under `src/Parley.Cli/Top` is on the new toolkit.
- [X] T036 [P] [US2] Rewrite `tests/Parley.Cli.Tests/Top/ParticipationTests.cs`,
      `JoinGuardTests.cs`, `SeatLifetimeTests.cs`, `AgreementTests.cs`, `MessageCapTests.cs`,
      `ConcludedUnderfootTests.cs` and `BrokerRestartTests.cs` through `Driving`, **with every
      assertion that reads the registry or the store unchanged** (SC-002). `SeatLifetimeTests`
      advances the `VirtualTimeProvider` twenty minutes and asserts the seat is released and the
      screen says `Your seat was released: nothing touched it for 20 minutes.` (US1 scenario 6).
- [X] T037 [P] [US2] Rewrite `tests/Parley.Cli.Tests/Top/NamingTests.cs` and
      `RemovingTests.cs` through `Driving`: the pre-filled, validated create form; keep as
      default; forget deletes nothing; destroy only after the typed name; a wrong name leaves the
      store on disk.
- [X] T038 [P] [US2] Rewrite `tests/Parley.Cli.Tests/Top/LogTailTests.cs`, `LogRotationTests.cs`,
      `LogLocationTests.cs` and `LogInjectionTests.cs` through `Driving`: a line written by the
      broker appears without a key (scenario 5); rotation is followed; the manifest-serving
      entry's log is the one announced; control characters in a line cannot move the cursor.
- [X] T039 [P] [US2] Rewrite `tests/Parley.Cli.Tests/Top/LaunchTests.cs` and
      `NarrowTerminalTests.cs` through `Driving`; fold the narrow case into `LayoutTests` if it
      would otherwise be a duplicate, and say so in the deleted file's replacement.
- [X] T040 [US2] Replay check: list every `[Fact]` and `[Theory]` name in
      `tests/Parley.Cli.Tests/Top` at `c85a271` against the same directory now, and record in
      this file under **Parity** below every 004 test that has no counterpart, with the reason.
      An empty list is the goal; a non-empty one is a decision to write down, not a gap to hide.
- [X] T041 [US2] Every action on the status line is also a button or menu entry: drive one
      action per region by `Driving.Click` alone in `tests/Parley.Cli.Tests/Top/MouseTests.cs`
      (take a seat, open a transcript, open the log, ask) and assert the same end state as the
      keyboard path (FR-003).
- [X] T042 [US2] Run the whole `Parley.Cli.Tests` host executable directly
      (`tests/Parley.Cli.Tests/bin/*/net10.0/Parley.Cli.Tests.exe`), not `dotnet test`, and
      record the count under **Parity**. `dotnet test` reports `Zero tests ran` on this machine
      and an empty suite is indistinguishable from a passing one there.
- [X] T043 [US2] Remove the `Spectre.Console.Testing` reference from
      `tests/Parley.Cli.Tests/Parley.Cli.Tests.csproj` and delete any helper that only existed
      for `TestConsole`.
- [X] T044 [US2] Remove `Spectre.Console` from `src/Parley.Cli/Parley.Cli.csproj` and both
      Spectre entries from `Directory.Packages.props`; `grep -r Spectre src tests` returns
      nothing. Build with zero warnings. Two toolkits in one process was tolerable on a branch
      for the length of the rewrite and is not a state that ships.

**Checkpoint**: parity. Nothing 004 delivered is gone, the old toolkit is gone, and the tree is
what `main` would carry if the feature stopped here.

---

## Phase 5: User Story 3 — Make a repository an agent of this project (P3)

**Goal**: from the agents region, name a directory; the surface shows the two files it will
write, writes them through `Enrolment.Add` on confirmation, and the agent appears as
`catalogued, not yet registered` within ten seconds.

**Independent Test**: with an empty catalogue, add a directory; its `.mcp.json` names the project
and the role, the catalogue names the directory, the row appears within ten seconds; after its
server attaches once the registry lists it `startable`; adding it again is refused and changes
nothing (SC-003 as amended).

- [X] T045 [US3] Create `src/Parley.Cli/Top/Adding.cs`: a dialog opened by *Add an agent* on the
      agents region. A directory field validated as typed against data-model.md's list — exists,
      holds `.git` (a worktree's file is said, not refused), `.mcp.json` mergeable, `parley` entry
      names no other project or broker, name well formed and not in the catalogue under another
      workspace — each refusal a reason at the prompt and `Nothing was written.`; a name field
      pre-filled from `EnrollCommand.SuggestName`; an optional `owns` field capped at
      `EnrollmentPlan.OwnsLimit`. Then the two paths it will write, and a confirm. On confirm,
      re-read the catalogue (edge case: edited by hand), call `Enrolment.Add`, report
      `Added '<name>'.` and the two paths, and refresh the region.
- [X] T046 [US3] Catalogue creation (FR-010, research R8) in `src/Parley.Cli/Top/Adding.cs`: when
      the project's entry names no `node_config`, propose `<store name>-node.json` beside the
      store, create it with an empty `agents` object, and record it on the entry through the same
      code `parley project set --node-config` runs in `src/Parley.Cli/ProjectCommand.cs`
      (extract `SetAsync`'s write into an internal method if it is not already callable without a
      console). Say `Created <path> and recorded it on '<project>'. Its broker restarts to host
      the node from it.`
- [X] T047 [US3] Write `tests/Parley.Cli.Tests/Top/AddingTests.cs`: the happy path through
      `Driving` against a repository fixture built by `RepositoryFile.cs`, asserting the
      `.mcp.json` contents (`PARLEY_AGENT`, `PARLEY_PROJECT`, `PARLEY_WORKSPACE`) and the
      catalogue entry on disk; each FR-009 refusal writes nothing (compare directory hashes before
      and after); a second add of the same directory is refused; the row reads `catalogued, not
      yet registered` within ten seconds. **Probes:** a directory that is a symlink to a
      repository already enrolled elsewhere; a `.mcp.json` with a `parley` entry whose `env` is
      an array; a name that is well formed but is the person's own seat name (refused: the
      catalogue check, since the seat holds the name in the registry, and the reason says so).
- [X] T048 [US3] Write `tests/Parley.Cli.Tests/Top/CatalogueCreationTests.cs`: a project with no
      `node_config` gains one at the proposed path, the manifest entry records it, the phrase is
      shown, and the serve watcher (`ManifestWatchTests`-style loopback with `serve --with-node`)
      restarts the project within two intervals and the node announces the new role.
- [X] T049 [US3] Write `tests/Parley.Cli.Tests/Top/StartableAfterAttachTests.cs` for SC-003's
      second half: after T047's add, attach a `Parley.Mcp` server once with the written
      `.mcp.json` environment, and assert the row moves from `catalogued, not yet registered` to
      `startable` with no node restarted — which is T014 doing its job from the surface's side.

**Checkpoint**: a repository becomes an agent of a project without leaving the terminal, and the
node can start it.

---

## Phase 6: User Story 4 — Withdraw an agent, and be able to take it back (P4)

**Goal**: keep, retire or purge from the agents region, each described first, retire and purge
each behind the agent's typed name, a purge that orphans an exchange behind a second acceptance,
the two files withdrawn only after the broker changed something, and reinstate from the same
region.

**Independent Test**: add through US3, retire (registry retired, files gone, absent from every
peer's roster), reinstate (active, files still gone), purge (gone from the registry); a wrong
name at each step changes nothing (SC-004).

- [X] T050 [US4] Create `src/Parley.Cli/Top/Withdrawing.cs` — **the one file under `Top` that
      may call `RetireAsync`, `ReinstateAsync` or `PurgeAsync`**. *Remove* on the agents region
      opens it. Order fixed by contracts/operations.md: describe (role, workspace, adapter,
      status, presence from `DescribeRegistry`; every `Waiting` edge naming the agent `WaitedOn`)
      → choose with `Keep` first and Enter's answer → for `Purge` with at least one edge, the
      consequence in §14's own sentence, the asker named, what they will see (the exchange gone,
      not timed out), and an explicit acceptance → the name typed back, ordinal, anything else
      `That is not this agent's name. Nothing changed.` → the operation → **only if the broker
      changed something**, `Enrolment.Withdraw` → `Retired '<name>'.` or `Purged '<name>'.` with
      the broker's toll per table, then each file named as removed or already absent.
- [X] T051 [US4] *Reinstate* in `src/Parley.Cli/Top/Withdrawing.cs`, offered only on a row whose
      status is retired: describe, name typed back, `ReinstateAsync`, then `Reinstated '<name>'.
      No file was recreated: add it again for it to be startable.` (FR-015).
- [X] T052 [US4] A row whose adapter is `Seat.Adapter` — this person's or anybody's — is never
      offered *Remove* or *Reinstate* (research R7, FR-016): the status line in
      `src/Parley.Cli/Top/AgentsRegion.cs` omits the action for such a row, and
      `src/Parley.Cli/Top/Withdrawing.cs` refuses if reached by any other route.
- [X] T053 [US4] Reshape `tests/Parley.Cli.Tests/Top/SurfaceTests.cs`: `RetireAsync`,
      `ReinstateAsync` and `PurgeAsync` may appear in exactly one file under `src/Parley.Cli/Top`,
      that file is `Withdrawing.cs`, and it contains the typed-name prompt constant;
      `ForceConcludeAsync`, `InstructAsync` and `PruneAsync` remain forbidden everywhere under
      `Top`; `destroy` stays confined to `Removing.cs`. The failure message still says admitting
      one is an argument to make in `contracts/operations.md` first (FR-017, SC-007).
- [X] T054 [US4] Write `tests/Parley.Cli.Tests/Top/WithdrawingTests.cs` through `Driving`
      against a loopback broker: the description shows the four facts and the owed exchange;
      `Keep` is Enter; retire with the right name → registry retired, both files gone, the report
      lists them; reinstate → active, files still gone, the phrase shown; purge → gone, toll
      shown. **Probes — each must change nothing on disk and in the registry:** the name with
      one character different; the name with trailing whitespace; an empty name; Escape at the
      name prompt; a purge whose broker call fails (stop the broker between the description and
      the name) — files must survive because step 4 follows step 3; a retire refused by the broker
      as "not registered" for an agent that is catalogue-only — files untouched and the refusal
      shown, which is the right answer since there is nothing to retire and `Withdraw` alone is a
      different action the surface does not offer.
- [X] T055 [P] [US4] Write `tests/Parley.Cli.Tests/Top/OrphaningTests.cs`: an agent that owes an
      answer, purged — the second confirmation names the asker and appears **before** the name
      prompt; declining it keeps; accepting then the right name purges and the asker's next
      `parley_inbox` shows the outcome the operator command produces today. Retiring the same
      agent shows no second confirmation and the exchange runs out on schedule.
- [X] T056 [P] [US4] Write `tests/Parley.Cli.Tests/Top/SeatNotOfferedTests.cs`: with a person
      seated from this terminal and another seated from a second `SeatBook` (a browser, in
      effect), neither row offers *Remove*; a real agent beside them does.

**Checkpoint**: all four stories. The surface can change who is in a project on the terms the
surface rule asks for.

---

## Phase 7: Polish

- [X] T057 [P] Empty states in `src/Parley.Cli/Top/AgentsRegion.cs` (no agents: say how to add
      one), `ConversationsRegion.cs` and `ProjectsRegion.cs`, each naming what to do next, as 004's
      T055 did.
- [X] T058 [P] Update `CLAUDE.md`: the `Parley.Web` entry's paragraph on `parley top` says it is
      built on Terminal.Gui rather than Spectre.Console; the surface rule's amendment now covers
      retire, reinstate and purge on the same terms as destroy, with `Withdrawing.cs` named the
      way `Removing.cs` is; the Active plan entry moves 005 to Previous with its merge commit and
      version once known.
- [X] T059 [P] Update `SPEC.md` §17 question 13 with what this changes: the surface now says
      `not in this catalogue` out loud, and a node rejoins on a catalogue change, so the
      misleading `startable` the question describes is narrower than when it was written. The
      question stays open; only its evidence grows.
- [X] T060 [P] Update `plugins/parley/skills/parley-operator/SKILL.md` and `README.md` where they
      describe `parley top`: agents can now be added, retired, reinstated and purged from it,
      each behind a typed name, and the enroll command writes `PARLEY_PROJECT` on a manifest-mode
      machine. Documentation does not move the version.
- [X] T061 Run the hygiene grep for the originating project's names across tracked **and
      untracked** files (memory: `hygiene-grep-misses-new-files`). Every new file in this
      feature — `Shell.cs`, the four regions, `Adding.cs`, `Withdrawing.cs`, `Enrolment.cs` and
      the new tests — is exactly the case `git grep` cannot see.
- [X] T062 Run the full suite by host executable three times on Windows and three times under
      podman (memory: `verify-linux-locally-with-podman`, keep the whole output). Record the
      counts under **Parity**. A Linux-only failure is the platform difference the spike was
      looking for and is a finding, not noise (memory:
      `same-sha-different-result-proves-a-flake`).
- [X] T063 Run the review gate as CLAUDE.md states it, **after the last edit**: three
      consecutive clean runs per lens, each probing what the previous did not, logged in
      `REVIEW-LEDGER.md` under a section for this branch, clean runs included. A finding above
      low resets that lens. The highest-value probes for this branch are named in T015, T017,
      T047 and T054; a lens that only re-reads them has not run.
- [ ] T064 **Open, and left open deliberately.** Walk `quickstart.md` end to end on the real deployment (the one this repository is
      registered with; memory: `parley-repo-registers-as-its-own-agent`) with the built tool,
      and note in this file anything the walk found that the tests did not.


### Parity — the counts T062 asks for

Both platforms at `HEAD`, by host executable, three passes each. Every pass green.

| Platform | Passes | Tests per pass | Failures |
|---|---|---|---|
| Windows 11, .NET 10.0.11 | 3 | 2,055 | 0 |
| Linux, `mcr.microsoft.com/dotnet/sdk:10.0` under podman | 3 | 2,055 | 0 |

The two differ only in what they skip: Linux skips one case in `Parley.Cli.Tests` and twenty-one in
`Parley.Mcp.Tests`, all of them platform-conditional and all of them skipped on purpose. No test
passed on one platform and failed on the other, which is the finding this task was watching for.

`dotnet build` is clean in Debug and Release, which is the whole lint gate.

### What T064 still needs, and why it is not ticked

The redirect half is done and passes: the built tool, run with its output piped, refuses with the
sentence step 1 names and points at `project list`, `registry`, `export` and `console`.

**The rest needs a real terminal and the owner's live deployment, and neither is something to take
without asking.** Walking steps 2 to 7 means `dotnet tool update` over the `parley` on `PATH` — the
one an eighteen-repository deployment and a scheduled task are running right now — and then driving
a full-screen application by hand. That is the owner's call to make and their machine to make it on,
so the task stays open rather than being ticked on a partial walk.

---

## Where this stands

**Phases 1, 2A, 2B and 3 are complete and green, and T029 of Phase 4 with them.** That is the MVP
the strategy below names, plus the whole of the half that ships behaviour outside the surface.

| Phase | Tasks | State |
|---|---|---|
| 1 Setup | T001–T004 | done |
| 2A Spike and harness | T005–T007, T009, T010 | done except T009/T010, which belong with the entry point |
| 2B Outside the toolkit | T011–T017 | done |
| 3 US1 See and move | T018–T028 | done |
| 4 US2 Take part | T029–T044 | T029 done; T030–T044 remain |
| 5 US3 Add | T045–T049 | not started |
| 6 US4 Withdraw | T050–T056 | not started |
| 7 Polish | T057–T064 | not started |

**What the next session picks up.** T030 is the transcript region's actions (what is waiting,
answer, ask, tell), and the foundation for every remaining dialog is already in
`src/Parley.Cli/Top/Prompting.cs` and the `RegionAction` list on `Shell` — a region says what it
can do and the status line and the key handler read the same list, so adding an action is one
entry rather than three edits.

**`TopCommand.RunAsync` still drives 004's Spectre surface**, which is deliberate and is the
"parity before addition" rule: the new shell is reachable only from tests until T040 has counted
004's journeys against their replacements. T035 is where `RunAsync` flips and `ProjectMenu` goes.

---

## Parity

*Filled in by T040, T042 and T062.*

| Measure | Value |
|---|---|
| 004 tests under `Top` at `c85a271` | 63 |
| Tests under `Top` now | 104 |
| 004 tests with no counterpart | **one, deliberately** |
| `Parley.Cli.Tests` host total, Windows | 509 |

**The one that has no counterpart is `ColourTests.The_chooser_really_does_interpret_markup_in_a_choice`.**
It existed to prove the hazard was real — that Spectre's chooser rendered its entries as markup, so
escaping them defended against something rather than nothing. There is no markup interpreter now,
and the test cannot be ported because the thing it asserted about no longer exists. What replaced
it is `ColourTests.The_check_is_looking_at_something`, which does the same job for the hazard this
toolkit actually has: it asserts that `Safe.Text` changes its input, so a version that returned the
string untouched would fail rather than pass every injection assertion vacuously.

Seven more changed name and are listed here so the count can be checked rather than trusted:

| 004 | now |
|---|---|
| `ColourTests.A_message_cannot_style_the_terminal_it_is_read_in` | `…cannot_move_the_cursor_of_the_terminal_it_is_read_in` |
| `ColourTests.A_project_named_with_markup_cannot_style_the_chooser` | `…named_with_control_characters_cannot_repaint_the_list` |
| `NamingTests.A_name_a_store_or_a_port_already_taken_is_refused_at_the_prompt` | `…is_refused_in_the_field` |
| `NamingTests.The_running_deployment_is_named_with_Enter_alone` | `…is_named_from_a_form_that_is_already_filled_in` |
| `NavigationTests.A_conversation_is_reached_without_typing_anything` | `RegionTests.` same name |
| `NavigationTests.The_agents_are_reached_the_same_way` | `RegionTests.The_first_project_is_selected_with_its_agents_beside_it…` |
| `NavigationTests.Leaving_the_first_screen_ends_it_without_reaching_a_broker` | `RegionTests.The_surface_opens_and_closes_without_reaching_a_broker` |

---

## Dependencies

```text
Phase 1 (Setup, T001–T004)
        │
        ├─► Phase 2A  The spike and harness (T005–T010)  ← gates every task under Top
        │        │
        │        └─► Phase 3  US1  See and move        (T018–T028)   MVP
        │                 │
        │                 └─► Phase 4  US2  Take part  (T029–T044)   parity; Spectre leaves at T044
        │                          │
        └─► Phase 2B  Outside the toolkit (T011–T017)  │  ← independent of 2A; gates US3/US4
                 │                                     │
                 └─────────────────┬───────────────────┘
                                   │
                          Phase 5  US3  Add            (T045–T049)   needs T011, T013, T014, T046
                                   │
                          Phase 6  US4  Withdraw       (T050–T056)   needs US3 (it removes what US3 added)
                                   │
                          Phase 7  Polish              (T057–T064)
```

Three orderings are not negotiable, from the plan:

1. **T005 and T006 before any other task under `Top`.** A rewrite built before the test factory
   is known is a rewrite that might not be testable.
2. **Phase 4 complete, T040 recorded, before T045.** Parity before addition: the rewrite must not
   cost a thing 004 delivered, and the only way to know is to replay 004's tests before the
   surface grows.
3. **T044 last in Phase 4.** Spectre leaves only when nothing under `Top` needs it, and it does
   leave before US3 starts.

US4 depends on US3 rather than only on Phase 2, because the thing it withdraws is what US3 adds
and `WithdrawingTests` builds its fixture through `Adding`.

## Parallel opportunities

- **Phase 2**: 2B (T011, T013, T015, T016, T017) runs entirely beside 2A; T011 and T013 are
  separate files, and T015–T017 are three separate test files once their subjects exist.
- **US1**: T019–T022 are four separate region files; T024–T028 are five separate test files.
- **US2**: T033 and T034 are separate files from T029–T032; T036–T039 are disjoint test sets.
- **US4**: T055 and T056 are separate probes from T054.
- **Polish**: T057–T060 touch different files.

## Implementation strategy

**MVP is Phase 1 + 2A + US1.** A person who can see a deployment on one screen and move through
it with Tab has what the owner asked for on 2026-09-03, before any action is taken from it.

**2A is a different kind of risk from the rest, and it is why the spike is first.** Every other
phase fails loudly. If no component factory runs without a console, the failure is that nothing
under `Top` can be tested on CI — and it would be discovered after sixteen files were rewritten.
T006's stop rule is what keeps that discovery cheap.

**Phase 4 is measured, not declared.** Its success criterion is *nothing changed*, the criterion
004's move had and the one most easily satisfied by a test that stopped running while the summary
still said green. T040 lists names against `c85a271`, T042 counts through the host executable,
and the `Parity` table above is where both go.

**2B is the half most worth probing adversarially**, because it is the half that ships behaviour
outside the surface: a catalogue edited to point one role at another workspace, a `.mcp.json`
whose `parley` entry names a project that no longer exists, a file half-saved when the node
polls. Those probes are in T015 and T017 and are not optional.

**Do not review while authoring.** T063 runs after the last edit. CLAUDE.md records the owner
catching the alternative three separate times.

## Format validation

All 64 tasks carry a checkbox, a sequential ID, a file path, `[P]` only where files are disjoint
and no incomplete task is depended on, and a `[US#]` label on story-phase tasks and on no others.
