# Tasks: A terminal surface a person takes part from

**Input**: Design documents from `specs/004-terminal-surface/`
**Prerequisites**: [plan.md](./plan.md), [spec.md](./spec.md), [research.md](./research.md),
[data-model.md](./data-model.md), [contracts/operations.md](./contracts/operations.md)

**Tests**: Included, and not optional here. This repository's review gate is partly mechanical
(`dotnet test` is the whole lint gate) and partly adversarial — CLAUDE.md records that its
highest-value findings came from *writing the malicious case and watching it be admitted*, never
from inspection. Every guard task below therefore has a probe beside it.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel — different files, no dependency on an incomplete task
- **[Story]**: the user story this serves (US1–US4)

## What was verified before these tasks were written

The plan asserted the move was safe. Rather than trust that, the file set was checked:

| Claim | Check | Result |
|---|---|---|
| Only `SeatCookie` touches HTTP | grep all 15 candidates for `HttpRequest`/`HttpResponse`/`HttpContext`/`CookieOptions` | **Confirmed** — 14 clean, `SeatCookie` alone matches |
| An implicit `global using` might hide a dependency | `Microsoft.NET.Sdk.Web` supplies them, so explicit `using` lines prove nothing — grepped type names, not usings | **Confirmed** — no hidden ASP.NET use |
| `Parley.Web.Tests` splits cleanly | classify each of 23 files by `WebApplicationFactory`/`HttpClient`/`ConsoleHost` use | **9 pure, 14 host-bound** |
| No graph grows | `Parley.Web.csproj` references exactly Configuration + Transport | **Confirmed** |
| FR-018 needs new code | `ConsolePicker.Available` is already `!IsInputRedirected && !IsOutputRedirected` | **Reuse, do not reinvent** |

One correction to the plan's prose: it says *"of the twelve types the web console holds outside
its Razor pages, exactly one touches HTTP."* There are **fifteen**, not twelve. The table beneath
it is complete and correct, and the conclusion is unaffected — but the count is wrong and tasks
below name all fifteen.

---

## Phase 1: Setup

**Purpose**: the new library exists, is wired into the build, and is empty. No behaviour moves yet.

- [ ] T001 Create `src/Parley.Client/Parley.Client.csproj` — `Microsoft.NET.Sdk` (not `.Web`),
      `RootNamespace`/`AssemblyName` `Parley.Client`, `IsPackable` false, project references to
      `..\Parley.Configuration\Parley.Configuration.csproj` and
      `..\Parley.Transport\Parley.Transport.csproj` **and nothing else**. Carry a header comment
      in the style of `Parley.Web.csproj`: it exists because two programs read it, and it holds
      the same two references the console already had so no graph grows.
- [ ] T002 Create `tests/Parley.Client.Tests/Parley.Client.Tests.csproj` — copy the shape of
      `tests/Parley.Web.Tests/Parley.Web.Tests.csproj`: `<OutputType>Exe</OutputType>`,
      `IsTestProject`, `NoWarn CS1591`, the linked `..\ManifestIsolation.cs`, `xunit.v3`, and the
      `BannedApiAnalyzers` block with `..\..\BannedSymbols.txt`. **Omit
      `Microsoft.AspNetCore.Mvc.Testing`** — its presence would be a route back to a web host.
- [ ] T003 Register both projects in `Parley.slnx` under the existing `/src/` and `/tests/`
      folders (note the recorded trap: a solution folder needs a real name, never `/`).
- [ ] T004 Verify `dotnet build` succeeds with 0 warnings before anything moves, so a later
      failure is attributable to the move rather than to the scaffolding.

---

## Phase 2: Foundational — the move

**Purpose**: relocate the client half of `Parley.Web` with **zero behaviour change**.

**This phase blocks every user story.** It is also the phase most likely to be declared finished
early: plan.md is explicit that *the web console's tests pass unchanged against relocated code, or
the move is wrong*, and "unchanged" is the load-bearing word. A test edited to accommodate the
move has stopped testing the move.

- [ ] T005 Record the pre-move baseline: run each `tests/Parley.*.Tests/bin/*/net10.0/*.exe`
      directly and write the per-assembly totals into this file under **Baseline** below.
      Do not use `dotnet test` for the count — CLAUDE.md records it reporting `Zero tests ran`
      on this machine, and a suite that discovers nothing looks exactly like a suite that passes.
- [ ] T006 Move the three top-level types to `src/Parley.Client/`: `BrokerDirectory.cs`,
      `BrokerClient.cs`, `ProjectHealth.cs`. Use `git mv` so history follows.
- [ ] T007 Move `src/Parley.Web/Rendering/` entire — `Transcript.cs`, `Visibility.cs`,
      `Roster.cs`, `RosterView.cs`, `Payload.cs`, `Palette.cs` — to
      `src/Parley.Client/Rendering/`.
- [ ] T008 Move `src/Parley.Web/Seating/` **less `SeatCookie.cs`** — `Seat.cs`, `SeatBook.cs`,
      `SeatHold.cs`, `SeatVoice.cs`, `Delivered.cs` — to `src/Parley.Client/Seating/`.
      `SeatCookie.cs` stays: it is the one type that knows what HTTP is, and it is the reason the
      seam is where it is.
- [ ] T009 Change the moved files' namespaces `Parley.Web[.X]` → `Parley.Client[.X]`, and add the
      `using Parley.Client...;` lines the Razor pages, `ConsoleHost.cs`, `Program.cs` and
      `SeatCookie.cs` now need. **Use Edit/Write, not a script** — memory records regex surgery
      mangling C# in this repository repeatedly; scripted edits are for markdown only.
- [ ] T010 Add `<ProjectReference Include="..\Parley.Client\Parley.Client.csproj" />` to
      `src/Parley.Web/Parley.Web.csproj`, and update its header comment: it no longer holds the
      client half, and the comment currently claims it does.
- [ ] T011 `git mv` the nine pure test files to `tests/Parley.Client.Tests/` —
      `BrokerDirectoryTests.cs`, `ConcurrencyTests.cs`, `RegistrationGuidanceTests.cs`,
      `RosterConsistencyTests.cs`, `RosterTests.cs`, `SeatTests.cs`, `UnnamedMachineTests.cs`,
      `VersionTests.cs`, `VisibilityTests.cs` — changing **namespaces and usings only**. If a
      file needs more than that to compile, stop: it was misclassified and the seam is wrong.
- [ ] T012 Confirm the fourteen host-bound files stay in `tests/Parley.Web.Tests/` and still
      build: `ActingTests`, `CurrencyTests`, `DeadBrokerConsole`, `ExportTests`, `ForgeryTests`,
      `FrameTests`, `InboxTests`, `JoiningTests`, `LaunchTests`, `ManifestCurrencyTests`,
      `NamingAPageAwayTests`, `RenderingTests`, `ScopeTests`, `StaleSeatTests`.
- [ ] T013 **The move's acceptance test.** Re-run every host executable and diff the per-assembly
      totals against T005's baseline. `Web + Client` must equal the old `Web` count exactly, and
      every other assembly must be untouched. A drop of even one test is a test that stopped
      running, which is the failure mode this whole phase is arranged to catch.
- [ ] T014 [P] Add `tests/Parley.Client.Tests/GraphTests.cs`: assert `Parley.Client.csproj`
      references exactly Configuration and Transport, and that no assembly reachable from
      `Parley.Client` is `Microsoft.Data.Sqlite`. Mirror the existing check in
      `Parley.Web.Tests` that the console cannot open a store — the constraint moved, so its
      guard must move with it and hold on both.
- [ ] T015 [P] Add `tests/Parley.Client.Tests/DoubleSeatTests.cs` for research R2's named
      consequence: one person seated **twice** under one name is two attachments, which is
      exactly the case `connected = 1` got wrong until #77. Probe it — seat twice, release one,
      and assert the person is still present and no in-progress work was released.
- [ ] T016 Bump `VersionPrefix` in `Directory.Build.props` **in this commit**. The move changes
      shipped assemblies, and CLAUDE.md is explicit that a change landing without a bump cannot be
      delivered by the documented upgrade path — `dotnet tool update` silently installs nothing.

**Checkpoint**: the console behaves identically, its tests are unedited, and a second reader is
now possible.

---

## Phase 3: User Story 1 — Read the deployment without a browser (P1) 🎯 MVP

**Goal**: a person launches `parley top`, moves between projects, and reads a conversation —
without typing an identifier (SC-001).

**Independent test**: against a broker holding a project, agents and one conversation, confirm
each is reachable and that the transcript matches what the web console renders for the same
conversation.

- [ ] T017 Add `Spectre.Console` to `Directory.Packages.props` under a new
      `<ItemGroup Label="Terminal">`, pinned exactly, and reference it from
      `src/Parley.Cli/Parley.Cli.csproj` with **no `Version=` attribute** — CPM makes that a
      restore error. Confirm `NuGetAudit` stays clean; this is the first non-Microsoft,
      non-gRPC dependency on the tool.
- [ ] T018 Add `<ProjectReference ...Parley.Client...>` to `src/Parley.Cli/Parley.Cli.csproj`.
      Leave the `Parley.Web` reference alone — `parley console` still starts Kestrel, which
      plan.md records as deliberate.
- [ ] T019 [US1] Add the `top` verb to `src/Parley.Cli/Program.cs`, following the existing
      `if (verb == "console")` shape at line ~325, and add its line to the `Help` string.
- [ ] T020 [US1] Refuse a non-interactive terminal (FR-018) in `src/Parley.Cli/Top/TopCommand.cs`
      by reusing `ConsolePicker.Available` — it is already
      `!IsInputRedirected && !IsOutputRedirected` and carries the written reasoning. The refusal
      must name what to run instead, not merely decline.
- [ ] T021 [P] [US1] `src/Parley.Cli/Top/ProjectList.cs` — list the manifest's projects via
      `BrokerDirectory` (FR-001), offering the fallback broker under a name that says it was not
      configured when the manifest names none.
- [ ] T022 [US1] Re-read the manifest while running (FR-002) in `src/Parley.Cli/Top/TopCommand.cs`
      and `src/Parley.Cli/Top/ProjectList.cs` by reading `BrokerDirectory.Current` on each draw —
      it already re-stamps on access. Do **not** snapshot it into a field at startup; that is
      precisely the bug the console shipped and #73 fixed.
- [ ] T023 [P] [US1] `src/Parley.Cli/Top/AgentsView.cs` — `DescribeRegistry` on the **Operators**
      client, showing name, presence, tier and owned workspace (FR-003). A registered agent that
      is not running must read as *startable*, not *absent*; quickstart.md §2 names these as
      different facts.
- [ ] T024 [P] [US1] `src/Parley.Cli/Top/ConversationList.cs` — `ListConversations` (FR-004).
- [ ] T025 [US1] `src/Parley.Cli/Top/TranscriptView.cs` — `Export`, rendered through the moved
      `Parley.Client.Rendering.Transcript` and `Visibility` (FR-004). Deriving it here instead is
      how two surfaces come to disagree, which SC-003 forbids.
- [ ] T026 [US1] Contain an unreachable project to itself (FR-017, SC-004) in
      `src/Parley.Cli/Top/ProjectList.cs`, using the moved `ProjectHealth` and reporting within
      five seconds.
- [ ] T027 [P] [US1] `tests/Parley.Cli.Tests/Top/NavigationTests.cs` — every step reachable by
      selection, no identifier typed (SC-001).
- [ ] T028 [P] [US1] `tests/Parley.Cli.Tests/Top/ColourTests.cs` — **probe SC-007/FR-005**: strip
      all styling from rendered output and assert every fact still reads. Do not inspect the code
      for colour use; render it and read it back with colour removed.
- [ ] T029 [P] [US1] `tests/Parley.Cli.Tests/Top/AgreementTests.cs` — **probe SC-003**: the same
      exported conversation, rendered by both surfaces, must agree on participants and per-message
      visibility. This is the test that justifies Phase 2 existing at all.
- [ ] T030 [P] [US1] `tests/Parley.Cli.Tests/Top/DeadBrokerTests.cs` — one dead project, others
      still usable, reported inside five seconds (SC-004).
- [ ] T031 [US1] `tests/Parley.Cli.Tests/Top/NonInteractiveTests.cs` — redirected output refuses
      and names the alternative (FR-018).

**Checkpoint**: US1 ships alone. An operator can read a deployment from a terminal with nothing
below this line built.

---

## Phase 4: User Story 2 — Take part in a conversation (P2)

**Goal**: a person seats themselves, answers a question they owe, and opens a new one.

**Independent test**: with a peer agent attached, ask the seated person a question from that peer;
confirm it arrives, that answering closes the exchange, and that the peer receives the answer.

**Depends on**: US1 for navigation.

- [ ] T032 [US2] `src/Parley.Cli/Top/Seating.cs` — hold a seat through the moved `SeatBook`,
      reached via `AgentsFor` (FR-006). Present `Seat.Adapter` = `parley-console`, unchanged:
      research R2 settles that a second marker would invent a restriction SPEC.md §14 does not
      have. **Never reach `ClientFor` from here** — contracts/operations.md makes the separation
      the guarantee and the naming explicitly not.
- [ ] T033 [US2] Release the seat on exit **including on interrupt** (FR-011, SC-006) in
      `src/Parley.Cli/Top/Seating.cs` — a `PosixSignalRegistration`/`CancelKeyPress` path that
      ends the `Attach` stream. A seat outliving the person is the failure #75 already shipped
      once.
- [ ] T034 [US2] Show seated-ness in `src/Parley.Cli/Top/Seating.cs` from the held stream only,
      never from a remembered flag (FR-012). The console's bug was a cookie outliving its seat; a
      bool would be the same bug in a new colour.
- [ ] T035 [P] [US2] `src/Parley.Cli/Top/Waiting.cs` — `Waiting`, `DrainInbox`,
      `GetSubQuestionIds`, `ResumeOwedBy` (FR-008), debts first per §7.3. **Say that draining is
      a state change** before doing it: quickstart.md §5 requires the person be told rather than
      discover it.
- [ ] T036 [US2] `src/Parley.Cli/Top/Answering.cs` — `Answer`, one resolution per sub-question
      (§5.3), answerable without leaving the transcript.
- [ ] T037 [US2] `src/Parley.Cli/Top/Composer.cs` — `Ask`/`AskRole` with intent, sub-questions and
      consequences collected before anything is sent (FR-009), and `Clarify`/`Notify` into an
      open conversation (FR-008).
- [ ] T038 [US2] Refuse before the wire in `src/Parley.Cli/Top/Composer.cs` (FR-010): rule 8.5's
      cap **stated as a limit**, rule 8.1's consequences minimum, and an empty sub-question set. When the broker refuses anyway,
      show its own words including the remedy — the shape #71 settled, where a refusal stripped of
      its detail told an agent not to do the very thing the broker had asked for.
- [ ] T039 [P] [US2] `tests/Parley.Cli.Tests/Top/SeatGuardTests.cs` — **probe FR-006/FR-007**:
      try to seat under a name the registry holds for a real agent (must refuse), and seat while
      the registry is unreadable (must refuse **outright**). A guard that passes hardest when the
      deployment is least known is worth nothing.
- [ ] T040 [P] [US2] `tests/Parley.Cli.Tests/Top/ReleaseTests.cs` — quitting and interrupting both
      release the seat within a minute (SC-006, FR-011).
- [ ] T041 [P] [US2] `tests/Parley.Cli.Tests/Top/RestartTests.cs` — the broker restarts underneath
      a running session; the surface must report the person as **no longer seated** rather than
      keep showing them present.
- [ ] T042 [P] [US2] `tests/Parley.Cli.Tests/Top/RefusalTests.cs` — an oversize message is refused
      here with the limit stated and never reaches the store; a broker refusal reaches the person
      with its remedy intact.
- [ ] T043 [P] [US2] `tests/Parley.Cli.Tests/Top/ConcludedTests.cs` — sending into a conversation
      concluded while it was open on screen is refused **with the reason**, not generically.
- [ ] T044 [US2] `tests/Parley.Cli.Tests/Top/RoundTripTests.cs` — the story's own test: a peer
      asks, the person answers from the terminal, the peer receives it (SC-002).

**Checkpoint**: the feature's stated purpose is met — a person takes part without an editor.

---

## Phase 5: User Story 3 — Watch the broker it is talking to (P3)

**Goal**: a Logs view that follows the broker's log, and an action that opens the web console.

**Independent test**: with a broker writing its log, new lines appear without restarting the view,
and the launch action opens the console's address.

- [ ] T045 [US3] `src/Parley.Cli/Top/LogTail.cs` — open the log with
      `FileShare.ReadWrite | FileShare.Delete` and **never the default** (FR-013). Research R4
      measured this in this repository: a reader holding a file the default way does not risk a
      stale read, it **stops the writer** — 287 of 300 renames denied. A continuously-open tail is
      the worst case of that.
- [ ] T046 [US3] A visible filter over the tail in `src/Parley.Cli/Top/LogTail.cs` (FR-013) — the
      log carries framework noise beside Parley's own lines, and the filter must be on screen, not
      remembered.
- [ ] T047 [US3] `src/Parley.Cli/Top/Launch.cs` — open the console address in the default browser
      and keep running (FR-014). Cross-platform: `explorer`/`open`/`xdg-open`, on the three
      platforms CI runs.
- [ ] T048 [P] [US3] `tests/Parley.Cli.Tests/Top/LogTailTests.cs` — **probe R4's finding
      directly**: hold the tail open and rename the log underneath it. The rename must succeed.
      Then assert appended lines appear without reopening the view.
- [ ] T049 [P] [US3] `tests/Parley.Cli.Tests/Top/LaunchTests.cs` — the launch action resolves the
      console address and does not terminate the application.

---

## Phase 6: User Story 4 — Do the operator jobs without leaving (P4)

**Goal**: name a project and onboard an agent from inside the application.

**Independent test**: name a project here, and confirm `parley project list` reports it
identically to one added from the command line.

- [ ] T050 [US4] `src/Parley.Cli/Top/Naming.cs` — call `ProjectManifest.TryUpdate` (the
      lock-holding path #78 added), **not** a re-implementation. The manifest has one writer and
      this is that same program; a second code path writing it would reintroduce exactly the lost
      update that was just fixed.
- [ ] T051 [US4] `src/Parley.Cli/Top/Onboarding.cs` — run the enrolment code in-process, and tell
      the person what remains to be done outside the application.
- [ ] T052 [P] [US4] `tests/Parley.Cli.Tests/Top/NamingTests.cs` — a project named here and one
      named by the command are indistinguishable in the manifest; a refusal names the rule broken.
- [ ] T053 [P] [US4] `tests/Parley.Cli.Tests/Top/ConcurrentNamingTests.cs` — naming from the
      surface while a `parley project add` runs must lose nothing. The lock is the mechanism;
      this proves the surface actually took it.

---

## Phase 7: Polish

- [ ] T054 [P] Reflow on resize in `src/Parley.Cli/Top/TranscriptView.cs`, never truncating
      anything that carries meaning (Edge Cases), with a check in
      `tests/Parley.Cli.Tests/Top/ReflowTests.cs`.
- [ ] T055 [P] Empty states across `src/Parley.Cli/Top/ProjectList.cs`,
      `src/Parley.Cli/Top/AgentsView.cs` and `src/Parley.Cli/Top/ConversationList.cs` — no
      projects, no agents, no conversations, each saying what to do next.
- [ ] T056 Confirm the terminal binds **no** destructive operator operation. `Retire`,
      `Reinstate`, `Purge`, `ForceConclude`, `Instruct` and `Prune` are excluded by
      contracts/operations.md on the argument that a destructive action behind a keystroke is a
      different risk from one behind a page that describes it first. Add a surface test that
      fails if one appears, so the exclusion is enforced rather than remembered.
- [ ] T057 Update `CLAUDE.md`: a `src/Parley.Client` entry in Layout saying why it exists, an
      amendment to the `Parley.Web` entry (it no longer holds the client half), and `parley top`
      in the surface list.
- [ ] T058 Update `SPEC.md` §17 question 15 with what this closes — the question records the gap,
      so it is where the answer belongs.
- [ ] T059 Run the hygiene grep for the originating project's names across **tracked and
      untracked** files. `git grep` sees tracked files only, and a brand-new file leaks the name
      while the check still reports clean — that has happened here.
- [ ] T060 Run the three review lenses **to convergence**, not to a fixed count: correctness and
      concurrency, security and data integrity, API surface and maintainability. Re-run a lens
      after fixing what it found. Stop when a full rotation finds nothing above low severity, and
      state how many rounds it took.

---

## Baseline

*Filled by T005 before anything moves, and compared by T013 after.*

| Assembly | Tests | After move |
|---|---|---|
| Parley.Broker.Tests | | |
| Parley.Cli.Tests | | |
| Parley.Core.Tests | | |
| Parley.Headless.Tests | | |
| Parley.Mcp.Tests | | |
| Parley.Server.Tests | | |
| Parley.Spec.Tests | | |
| Parley.Store.Tests | | |
| Parley.Web.Tests | | |
| Parley.Client.Tests | — | |

---

## Dependencies

```text
Phase 1 (Setup, T001–T004)
        │
Phase 2 (The move, T005–T016)  ← blocks everything; T013 is its acceptance test
        │
        ├─► Phase 3  US1  Read           (T017–T031)   MVP, ships alone
        │        │
        │        └─► Phase 4  US2  Take part      (T032–T044)   needs US1 to navigate
        │
        ├─► Phase 5  US3  Logs + launch  (T045–T049)   independent of US1/US2
        └─► Phase 6  US4  Operator jobs  (T050–T053)   independent; touches no broker
                 │
Phase 7 (Polish, T054–T060)
```

US3 and US4 depend on Phase 2 only, so either can be built beside US1 — US4 does not touch a
broker at all.

## Parallel opportunities

- **Phase 1**: T001 and T002 are separate files.
- **Phase 2**: T006, T007 and T008 are disjoint sets; T014 and T015 are separate new test files.
- **US1**: T021, T023, T024 are separate views; T027–T031 are five separate test files.
- **US2**: T039–T043 are five separate probes.
- **Across stories**: once Phase 2 lands, US1, US3 and US4 can proceed at once.

## Implementation strategy

**MVP is Phase 1 + Phase 2 + US1.** An operator who can read a deployment from a terminal has
something useful before a single message is sent, and every later story navigates through it.

**Phase 2 is the risk, and it is a different kind of risk from the rest.** It is the only phase
whose success criterion is *nothing changed*. Everything after it is additive and fails loudly;
the move fails quietly, by a test that stopped running while the summary still says green. That
is why T005 and T013 bracket it with counted evidence rather than a build that passes — and why
they insist on the host executables, since `dotnet test` reports `Zero tests ran` on this machine
and an empty suite is indistinguishable from a passing one in that output.

**Do not review while authoring.** CLAUDE.md records the user catching that three separate times:
lenses run alongside edits report passes that never covered the final state. T060 runs after the
last edit.

## Format validation

All 60 tasks carry a checkbox, a sequential ID, a file path, `[P]` only where files are disjoint,
and a `[US#]` label on story-phase tasks and on no others.
