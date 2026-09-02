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
| `Parley.Web.Tests` splits cleanly | classify each of 23 files by `WebApplicationFactory`/`HttpClient`/`ConsoleHost` use | **8 pure, 15 host-bound** — the first pass said 9/14 and was wrong; see below |
| No graph grows | `Parley.Web.csproj` references exactly Configuration + Transport | **Confirmed** |
| FR-018 needs new code | `ConsolePicker.Available` is already `!IsInputRedirected && !IsOutputRedirected` | **Reuse, do not reinvent** |

One correction to the plan's prose: it says *"of the twelve types the web console holds outside
its Razor pages, exactly one touches HTTP."* There are **fifteen**, not twelve. The table beneath
it is complete and correct, and the conclusion is unaffected — but the count is wrong and tasks
below name all fifteen.

**And one correction to the classification above, made by the move rather than by re-reading it.**
The test split was screened for `WebApplicationFactory`, `HttpClient` and `ConsoleHost`, and
`VersionTests.cs` uses none of them — so it screened as pure. It imports `Parley.Web.Pages` and
asserts on `IndexModel.ConsoleSpeaks` and on `Index.cshtml`'s markup, which the screen never
looked for. T011's stop rule caught it: *if a file needs more than namespaces and usings to
compile, it was misclassified*. It did, so it went back. The real split is **8 pure, 15
host-bound**.

The screen was too narrow, and the lesson generalises: a filter looking for host *plumbing* will
pass a test that depends on a *page*. What made this safe was not the filter but the rule that
stopped when the filter turned out to be wrong.

---

## Phase 1: Setup

**Purpose**: the new library exists, is wired into the build, and is empty. No behaviour moves yet.

- [X] T001 Create `src/Parley.Client/Parley.Client.csproj` — `Microsoft.NET.Sdk` (not `.Web`),
      `RootNamespace`/`AssemblyName` `Parley.Client`, `IsPackable` false, project references to
      `..\Parley.Configuration\Parley.Configuration.csproj` and
      `..\Parley.Transport\Parley.Transport.csproj` **and nothing else**. Carry a header comment
      in the style of `Parley.Web.csproj`: it exists because two programs read it, and it holds
      the same two references the console already had so no graph grows.
- [X] T002 Create `tests/Parley.Client.Tests/Parley.Client.Tests.csproj` — copy the shape of
      `tests/Parley.Web.Tests/Parley.Web.Tests.csproj`: `<OutputType>Exe</OutputType>`,
      `IsTestProject`, `NoWarn CS1591`, the linked `..\ManifestIsolation.cs`, `xunit.v3`, and the
      `BannedApiAnalyzers` block with `..\..\BannedSymbols.txt`. **Omit
      `Microsoft.AspNetCore.Mvc.Testing`** — its presence would be a route back to a web host.
- [X] T003 Register both projects in `Parley.slnx` under the existing `/src/` and `/tests/`
      folders (note the recorded trap: a solution folder needs a real name, never `/`).
- [X] T004 Verify `dotnet build` succeeds with 0 warnings before anything moves, so a later
      failure is attributable to the move rather than to the scaffolding.

---

## Phase 2: Foundational — the move

**Purpose**: relocate the client half of `Parley.Web` with **zero behaviour change**.

**This phase blocks every user story.** It is also the phase most likely to be declared finished
early: plan.md is explicit that *the web console's tests pass unchanged against relocated code, or
the move is wrong*, and "unchanged" is the load-bearing word. A test edited to accommodate the
move has stopped testing the move.

- [X] T005 Record the pre-move baseline: run each `tests/Parley.*.Tests/bin/*/net10.0/*.exe`
      directly and write the per-assembly totals into this file under **Baseline** below.
      Do not use `dotnet test` for the count — CLAUDE.md records it reporting `Zero tests ran`
      on this machine, and a suite that discovers nothing looks exactly like a suite that passes.
- [X] T006 Move the three top-level types to `src/Parley.Client/`: `BrokerDirectory.cs`,
      `BrokerClient.cs`, `ProjectHealth.cs`. Use `git mv` so history follows.
- [X] T007 Move `src/Parley.Web/Rendering/` entire — `Transcript.cs`, `Visibility.cs`,
      `Roster.cs`, `RosterView.cs`, `Payload.cs`, `Palette.cs` — to
      `src/Parley.Client/Rendering/`.
- [X] T008 Move `src/Parley.Web/Seating/` **less `SeatCookie.cs`** — `Seat.cs`, `SeatBook.cs`,
      `SeatHold.cs`, `SeatVoice.cs`, `Delivered.cs` — to `src/Parley.Client/Seating/`.
      `SeatCookie.cs` stays: it is the one type that knows what HTTP is, and it is the reason the
      seam is where it is.
- [X] T009 Change the moved files' namespaces `Parley.Web[.X]` → `Parley.Client[.X]`, and add the
      `using Parley.Client...;` lines the Razor pages, `ConsoleHost.cs`, `Program.cs` and
      `SeatCookie.cs` now need. **Use Edit/Write, not a script** — memory records regex surgery
      mangling C# in this repository repeatedly; scripted edits are for markdown only.
- [X] T010 Add `<ProjectReference Include="..\Parley.Client\Parley.Client.csproj" />` to
      `src/Parley.Web/Parley.Web.csproj`, and update its header comment: it no longer holds the
      client half, and the comment currently claims it does.
- [X] T011 `git mv` the pure test files to `tests/Parley.Client.Tests/` —
      `BrokerDirectoryTests.cs`, `ConcurrencyTests.cs`, `RegistrationGuidanceTests.cs`,
      `RosterConsistencyTests.cs`, `RosterTests.cs`, `SeatTests.cs`, `UnnamedMachineTests.cs`,
      `VersionTests.cs`, `VisibilityTests.cs` — changing **namespaces and usings only**. If a
      file needs more than that to compile, stop: it was misclassified and the seam is wrong.
- [X] T012 Confirm the fourteen host-bound files stay in `tests/Parley.Web.Tests/` and still
      build: `ActingTests`, `CurrencyTests`, `DeadBrokerConsole`, `ExportTests`, `ForgeryTests`,
      `FrameTests`, `InboxTests`, `JoiningTests`, `LaunchTests`, `ManifestCurrencyTests`,
      `NamingAPageAwayTests`, `RenderingTests`, `ScopeTests`, `StaleSeatTests`.
- [X] T013 **The move's acceptance test.** Re-run every host executable and diff the per-assembly
      totals against T005's baseline. `Web + Client` must equal the old `Web` count exactly, and
      every other assembly must be untouched. A drop of even one test is a test that stopped
      running, which is the failure mode this whole phase is arranged to catch.
- [X] T014 [P] Add `tests/Parley.Client.Tests/GraphTests.cs`: assert `Parley.Client.csproj`
      references exactly Configuration and Transport, and that no assembly reachable from
      `Parley.Client` is `Microsoft.Data.Sqlite`. Mirror the existing check in
      `Parley.Web.Tests` that the console cannot open a store — the constraint moved, so its
      guard must move with it and hold on both.
- [X] T015 [P] Add `tests/Parley.Client.Tests/DoubleSeatTests.cs` for research R2's named
      consequence: one person seated **twice** under one name is two attachments, which is
      exactly the case `connected = 1` got wrong until #77. Probe it — seat twice, release one,
      and assert the person is still present and no in-progress work was released.
- [X] T016 Bump `VersionPrefix` in `Directory.Build.props` **in this commit**. The move changes
      shipped assemblies, and CLAUDE.md is explicit that a change landing without a bump cannot be
      delivered by the documented upgrade path — `dotnet tool update` silently installs nothing.

**Checkpoint — reached.** 1650 tests pass, 0 warnings. The count invariant held exactly:
`Web 105 + Client 64 = 169`, which is the old 168 plus the isolation check the new assembly took
with the guard. Every other assembly is unchanged. The 7 above 1643 are the new guards (3 graph,
4 double-seat).

**Four things the move turned up that reading it had not.**

1. **`VersionTests` was misclassified**, and T011's stop rule caught it rather than a build
   failure. See the correction at the top of this file.

2. **The internals moved out from under their callers.** `SeatHold.Record`, `Settled` and
   `DismissRead` are `internal`, and the console could call them only because it shared an
   assembly with them. Four call sites. Granted with `InternalsVisibleTo("Parley.Web")`, following
   the grant `Parley.Core` already makes to `Parley.Store` — the alternative was making them
   public, which lets anything desynchronise a person's delivered set from what the broker
   believes it sent. **This is a signal, not a solution**: that two surfaces drive a seat's
   bookkeeping from their own code is how they come to disagree about what a person has seen, and
   the fix is for the drive to live beside the state. That is US2's problem, not a move's.

3. **The move silently weakened the console's store guard, and the guard kept passing.** It read
   `Parley.Web.csproj`'s *direct* references, which was a complete check only while the console
   referenced nothing but Configuration and Transport. It now references `Parley.Client`, so a
   store acquired there would arrive transitively and never appear in the file being read. Both
   guards now walk the graph. **Proved rather than argued**: adding `Parley.Store` to
   `Parley.Client` fails four checks, and the console's own reports `Microsoft.Data.Sqlite` — two
   hops out, where the old one-hop version saw nothing. Reverted.

4. **Five `InboxTests` failures**, all one cause: the tests read *source files by path*, and two
   of those files moved. Repointed. The assertions are untouched and the source they read is
   byte-identical apart from its namespace line, so this is a path change and not a test edited to
   accommodate the move.

Item 3 is the one worth keeping. A guard that goes on passing while the thing it guards becomes
reachable is worse than no guard, because it is also an assurance.

---

## Phase 3: User Story 1 — Read the deployment without a browser (P1) 🎯 MVP

**Goal**: a person launches `parley top`, moves between projects, and reads a conversation —
without typing an identifier (SC-001).

**Independent test**: against a broker holding a project, agents and one conversation, confirm
each is reachable and that the transcript matches what the web console renders for the same
conversation.

- [X] T017 Add `Spectre.Console` to `Directory.Packages.props` under a new
      `<ItemGroup Label="Terminal">`, pinned exactly, and reference it from
      `src/Parley.Cli/Parley.Cli.csproj` with **no `Version=` attribute** — CPM makes that a
      restore error. Confirm `NuGetAudit` stays clean; this is the first non-Microsoft,
      non-gRPC dependency on the tool.
- [X] T018 Add `<ProjectReference ...Parley.Client...>` to `src/Parley.Cli/Parley.Cli.csproj`.
      Leave the `Parley.Web` reference alone — `parley console` still starts Kestrel, which
      plan.md records as deliberate.
- [X] T019 [US1] Add the `top` verb to `src/Parley.Cli/Program.cs`, following the existing
      `if (verb == "console")` shape at line ~325, and add its line to the `Help` string.
- [X] T020 [US1] Refuse a non-interactive terminal (FR-018) in `src/Parley.Cli/Top/TopCommand.cs`
      by reusing `ConsolePicker.Available` — it is already
      `!IsInputRedirected && !IsOutputRedirected` and carries the written reasoning. The refusal
      must name what to run instead, not merely decline.
- [X] T021 [P] [US1] `src/Parley.Cli/Top/ProjectList.cs` — list the manifest's projects via
      `BrokerDirectory` (FR-001), offering the fallback broker under a name that says it was not
      configured when the manifest names none.
- [X] T022 [US1] Re-read the manifest while running (FR-002) in `src/Parley.Cli/Top/TopCommand.cs`
      and `src/Parley.Cli/Top/ProjectList.cs` by reading `BrokerDirectory.Current` on each draw —
      it already re-stamps on access. Do **not** snapshot it into a field at startup; that is
      precisely the bug the console shipped and #73 fixed.
- [X] T023 [P] [US1] `src/Parley.Cli/Top/AgentsView.cs` — `DescribeRegistry` on the **Operators**
      client, showing name, presence, tier and owned workspace (FR-003). A registered agent that
      is not running must read as *startable*, not *absent*; quickstart.md §2 names these as
      different facts.
- [X] T024 [P] [US1] `src/Parley.Cli/Top/ConversationList.cs` — `ListConversations` (FR-004).
- [X] T025 [US1] `src/Parley.Cli/Top/TranscriptView.cs` — `Export`, rendered through the moved
      `Parley.Client.Rendering.Transcript` and `Visibility` (FR-004). Deriving it here instead is
      how two surfaces come to disagree, which SC-003 forbids.
- [X] T026 [US1] Contain an unreachable project to itself (FR-017, SC-004) in
      `src/Parley.Cli/Top/ProjectList.cs`, using the moved `ProjectHealth` and reporting within
      five seconds.
- [X] T027 [P] [US1] `tests/Parley.Cli.Tests/Top/NavigationTests.cs` — every step reachable by
      selection, no identifier typed (SC-001).
- [X] T028 [P] [US1] `tests/Parley.Cli.Tests/Top/ColourTests.cs` — **probe SC-007/FR-005**: strip
      all styling from rendered output and assert every fact still reads. Do not inspect the code
      for colour use; render it and read it back with colour removed.
- [X] T029 [P] [US1] `tests/Parley.Cli.Tests/Top/AgreementTests.cs` — **probe SC-003**: the same
      exported conversation, rendered by both surfaces, must agree on participants and per-message
      visibility. This is the test that justifies Phase 2 existing at all.
- [X] T030 [P] [US1] `tests/Parley.Cli.Tests/Top/DeadBrokerTests.cs` — one dead project, others
      still usable, reported inside five seconds (SC-004).
- [X] T031 [US1] `tests/Parley.Cli.Tests/Top/NonInteractiveTests.cs` — redirected output refuses
      and names the alternative (FR-018).

**Checkpoint — reached.** 1669 tests pass, 0 warnings. `parley top` lists the projects with each
broker's health, shows a project's agents, lists its conversations and opens one as a transcript.

**T027 was left open and is now done, and the reason it was open turned out to be wrong.** It
asked for a test that every step is reachable by selection with no identifier typed. That was
deferred on the grounds that no driver existed for Spectre's prompts. There is one:
`Spectre.Console.Testing` ships a `TestConsole` whose input takes pushed keys, it is a leaf
package at the version already pinned, and a spike drove a `SelectionPrompt` first time.

What was genuinely in the way was different, and worth having found: **the views drew to the
static `AnsiConsole`.** Redirecting that for a test means mutating process-global state while
xUnit runs classes in parallel — the exact shape of the bug `BannedApiAnalyzers` exists in this
repository to prevent. So the console is now a parameter, `TopCommand.DriveAsync` takes it, and
every view says where it draws instead of reaching for an ambient one.

**The test proves SC-001 by construction rather than asserting it.** Nothing but arrow keys and
Enter is ever pushed — no text at all — so if any step wanted an identifier typed, its prompt
would find nothing to read and the journey would not finish. Reaching the transcript *is* the
proof that nothing asked. Verified non-vacuous by removing one key: the journey then reaches
Agents instead and the transcript assertion fails.

**T017's flagged risk cleared.** Spectre.Console 0.57.2 has *no* transitive package dependencies
on .NET 10 — checked with `dotnet nuget why`, not assumed — so nothing new is on the graph but the
one assembly, there is no pin to justify, and `NuGetAudit` reports nothing.

**Two probes were made to fail before being trusted.** Removing the escape from the transcript
body fails `A_message_cannot_style_the_terminal_it_is_read_in`; the graph guard fails four ways
when a store is added to the client. A guard nobody has watched fail is a guard nobody has tested.

**And a guard caught the work.** `PluginTests.The_operator_skill_documents_every_operator_command`
failed the moment `top` existed: the plugin's operator skill has to document every verb the tool
grows, because *a command the tool grows and the skill never hears about is one Claude will not
reach for, which reads as the feature not existing*. Documented; it passes.

---

## Phase 4: User Story 2 — Take part in a conversation (P2)

**Goal**: a person seats themselves, answers a question they owe, and opens a new one.

**Independent test**: with a peer agent attached, ask the seated person a question from that peer;
confirm it arrives, that answering closes the exchange, and that the peer receives the answer.

**Depends on**: US1 for navigation.

- [X] T032 [US2] `src/Parley.Cli/Top/Seating.cs` — hold a seat through the moved `SeatBook`,
      reached via `AgentsFor` (FR-006). Present `Seat.Adapter` = `parley-console`, unchanged:
      research R2 settles that a second marker would invent a restriction SPEC.md §14 does not
      have. **Never reach `ClientFor` from here** — contracts/operations.md makes the separation
      the guarantee and the naming explicitly not.
- [X] T033 [US2] Release the seat on exit **including on interrupt** (FR-011, SC-006) in
      `src/Parley.Cli/Top/Seating.cs` — a `PosixSignalRegistration`/`CancelKeyPress` path that
      ends the `Attach` stream. A seat outliving the person is the failure #75 already shipped
      once.
- [X] T034 [US2] Show seated-ness in `src/Parley.Cli/Top/Seating.cs` from the held stream only,
      never from a remembered flag (FR-012). The console's bug was a cookie outliving its seat; a
      bool would be the same bug in a new colour.
- [X] T035 [P] [US2] `src/Parley.Cli/Top/Waiting.cs` — `Waiting`, `DrainInbox`,
      `GetSubQuestionIds`, `ResumeOwedBy` (FR-008), debts first per §7.3. **Say that draining is
      a state change** before doing it: quickstart.md §5 requires the person be told rather than
      discover it.
- [X] T036 [US2] `src/Parley.Cli/Top/Answering.cs` — `Answer`, one resolution per sub-question
      (§5.3), answerable without leaving the transcript.
- [X] T037 [US2] `src/Parley.Cli/Top/Composer.cs` — `Ask`/`AskRole` with intent, sub-questions and
      consequences collected before anything is sent (FR-009), and `Clarify`/`Notify` into an
      open conversation (FR-008).
- [X] T038 [US2] Refuse before the wire in `src/Parley.Cli/Top/Composer.cs` (FR-010): rule 8.5's
      cap **stated as a limit**, rule 8.1's consequences minimum, and an empty sub-question set. When the broker refuses anyway,
      show its own words including the remedy — the shape #71 settled, where a refusal stripped of
      its detail told an agent not to do the very thing the broker had asked for.
- [X] T039 [P] [US2] `tests/Parley.Cli.Tests/Top/SeatGuardTests.cs` — **probe FR-006/FR-007**:
      try to seat under a name the registry holds for a real agent (must refuse), and seat while
      the registry is unreadable (must refuse **outright**). A guard that passes hardest when the
      deployment is least known is worth nothing.
- [X] T040 [P] [US2] `tests/Parley.Cli.Tests/Top/ReleaseTests.cs` — quitting and interrupting both
      release the seat within a minute (SC-006, FR-011).
- [X] T041 [P] [US2] `tests/Parley.Cli.Tests/Top/RestartTests.cs` — the broker restarts underneath
      a running session; the surface must report the person as **no longer seated** rather than
      keep showing them present.
- [X] T042 [P] [US2] `tests/Parley.Cli.Tests/Top/RefusalTests.cs` — an oversize message is refused
      here with the limit stated and never reaches the store; a broker refusal reaches the person
      with its remedy intact.
- [X] T043 [P] [US2] `tests/Parley.Cli.Tests/Top/ConcludedTests.cs` — sending into a conversation
      concluded while it was open on screen is refused **with the reason**, not generically.
- [X] T044 [US2] `tests/Parley.Cli.Tests/Top/RoundTripTests.cs` — the story's own test: a peer
      asks, the person answers from the terminal, the peer receives it (SC-002).

**Checkpoint**: the feature's stated purpose is met — a person takes part without an editor.

---

## Phase 5: User Story 3 — Watch the broker it is talking to (P3)

**Goal**: a Logs view that follows the broker's log, and an action that opens the web console.

**Independent test**: with a broker writing its log, new lines appear without restarting the view,
and the launch action opens the console's address.

- [X] T045 [US3] `src/Parley.Cli/Top/LogTail.cs` — open the log with
      `FileShare.ReadWrite | FileShare.Delete` and **never the default** (FR-013). Research R4
      measured this in this repository: a reader holding a file the default way does not risk a
      stale read, it **stops the writer** — 287 of 300 renames denied. A continuously-open tail is
      the worst case of that.
- [X] T046 [US3] A visible filter over the tail in `src/Parley.Cli/Top/LogTail.cs` (FR-013) — the
      log carries framework noise beside Parley's own lines, and the filter must be on screen, not
      remembered.
- [X] T047 [US3] `src/Parley.Cli/Top/Launch.cs` — open the console address in the default browser
      and keep running (FR-014). Cross-platform: `explorer`/`open`/`xdg-open`, on the three
      platforms CI runs.
- [X] T048 [P] [US3] `tests/Parley.Cli.Tests/Top/LogTailTests.cs` — **probe R4's finding
      directly**: hold the tail open and rename the log underneath it. The rename must succeed.
      Then assert appended lines appear without reopening the view.
- [X] T049 [P] [US3] `tests/Parley.Cli.Tests/Top/LaunchTests.cs` — the launch action resolves the
      console address and does not terminate the application.

---

## Phase 6: User Story 4 — Do the operator jobs without leaving (P4)

**Goal**: name a project and onboard an agent from inside the application.

**Independent test**: name a project here, and confirm `parley project list` reports it
identically to one added from the command line.

- [X] T050 [US4] `src/Parley.Cli/Top/Naming.cs` — call `ProjectManifest.TryUpdate` (the
      lock-holding path #78 added), **not** a re-implementation. The manifest has one writer and
      this is that same program; a second code path writing it would reintroduce exactly the lost
      update that was just fixed.
- [X] T051 [US4] `src/Parley.Cli/Top/Onboarding.cs` — run the enrolment code in-process, and tell
      the person what remains to be done outside the application.
- [X] T052 [P] [US4] `tests/Parley.Cli.Tests/Top/NamingTests.cs` — a project named here and one
      named by the command are indistinguishable in the manifest; a refusal names the rule broken.
- [X] T053 [P] [US4] `tests/Parley.Cli.Tests/Top/ConcurrentNamingTests.cs` — naming from the
      surface while a `parley project add` runs must lose nothing. The lock is the mechanism;
      this proves the surface actually took it.

---

## Phase 7: Polish

- [X] T054 [P] Reflow on resize in `src/Parley.Cli/Top/TranscriptView.cs`, never truncating
      anything that carries meaning (Edge Cases), with a check in
      `tests/Parley.Cli.Tests/Top/ReflowTests.cs`.
- [X] T055 [P] Empty states across `src/Parley.Cli/Top/ProjectList.cs`,
      `src/Parley.Cli/Top/AgentsView.cs` and `src/Parley.Cli/Top/ConversationList.cs` — no
      projects, no agents, no conversations, each saying what to do next.
- [X] T056 Confirm the terminal binds **no** destructive operator operation. `Retire`,
      `Reinstate`, `Purge`, `ForceConclude`, `Instruct` and `Prune` are excluded by
      contracts/operations.md on the argument that a destructive action behind a keystroke is a
      different risk from one behind a page that describes it first. Add a surface test that
      fails if one appears, so the exclusion is enforced rather than remembered.
- [X] T057 Update `CLAUDE.md`: a `src/Parley.Client` entry in Layout saying why it exists, an
      amendment to the `Parley.Web` entry (it no longer holds the client half), and `parley top`
      in the surface list.
- [X] T058 Update `SPEC.md` §17 question 15 with what this closes — the question records the gap,
      so it is where the answer belongs.
- [X] T059 Run the hygiene grep for the originating project's names across **tracked and
      untracked** files. `git grep` sees tracked files only, and a brand-new file leaks the name
      while the check still reports clean — that has happened here.
- [X] T060 Run the three review lenses **to convergence**, not to a fixed count: correctness and
      concurrency, security and data integrity, API surface and maintainability. Re-run a lens
      after fixing what it found. Stop when a full rotation finds nothing above low severity, and
      state how many rounds it took.

      **Two rounds.** Round one found four things, three of them real:

      1. *Correctness.* `LogTail.ShowAsync` awaited the stop-key read outside a `finally`. That
         await throws when the surface is cancelled — Ctrl+C, the ordinary way to leave — so the
         follow task was abandoned still holding the log open while its token source was disposed
         underneath it. An unobserved fault on a background task is how two probes in this
         repository hung for ten minutes instead of failing.
      2. *Correctness.* Nothing in the terminal ever called `SeatBook.Sweep`. `IdleAfter` exists
         because a tab left open is not somebody at a desk; a terminal left open overnight is the
         same claim, and this surface was making it indefinitely. It now sweeps each menu loop and
         touches the seat of whoever is looking at it.
      3. **Security, and the one worth the whole exercise.** `SelectionPrompt` renders its choices
         **as markup** — that is how anybody puts a colour in one. Conversation identifiers, agent
         names and message excerpts went into `AddChoices` unescaped, so an agent naming a
         conversation with a tag chose how the operator's menu was drawn. Panels were escaped from
         the first draft and choosers were not, and that was not a judgement: the panel was simply
         where the hazard had been noticed. **Found by probing the prompt rather than reading it**,
         and the probe is kept as a test that pins the premise the escaping rests on.
      4. *Maintainability.* The escape helper was `ProjectList.Escape`, called from eleven files
         with nothing to do with listing projects — which is how a security property comes to look
         like a formatting convenience. It is `Safe.Text` now, in a type whose remarks say what it
         is for.

      Round one also turned up one thing on re-running lens two: `Launch.Open` accepted any
      absolute URI and hands it to the Windows shell, which launches whatever handler the scheme
      is registered to. Narrowed to http and https. The only caller passes a constant, so it is a
      guard against the second caller — which is the one that opens an address from a manifest.

      **Round two found nothing above low severity.** The sweep for unescaped dynamic text came
      back empty, the seat sweep does not fire while a person is on a screen that is not the menu,
      and the `Safe` extraction changed no behaviour. Stopped there.

      **That was wrong, and the way it was wrong is the point of the rule it broke.** Round two
      cleared the seat sweep by reasoning about it — *"the seat sweep does not fire while a person
      is on a screen that is not the menu"* — rather than by writing the case and watching it run.
      It does fire, and the person it unseats is the one who spent the longest reading a question.
      Three further rounds followed, and every one of them found something.

      **Round three.** Four, all real:

      1. *Correctness.* The sweep ran **before** the touch, so a person who spent longer than
         `IdleAfter` in the inbox — reading a question and writing an answer, the slowest and most
         valuable thing this surface does — came back to a menu offering them a join. `Refresh`
         now touches first and sweeps second, and the order is the whole of it.
      2. *Correctness.* The seat was a local inside one visit to `ProjectMenu`, so going back to
         the project list and returning to the same project lost it — while the book still held
         the attachment and the registry still had the person present. It belongs to the session,
         so `TopCommand` holds it and passes it in.
      3. *Correctness.* The `Ctrl+C` handler could not work and made the banner a lie. Every screen
         blocks inside a synchronous Spectre prompt, which observes no cancellation token, so the
         handler suppressed the exit and nothing else happened; pressing it again did nothing
         either. Removed. Letting the process die is the correct behaviour and not the lesser one —
         §4.0 makes the open connection the evidence, and the process ending closes every attach
         stream, which is exactly how the broker learns somebody left.
      4. *Correctness.* `LogTail.For` called `Path.GetFullPath` on a value out of a hand-edited
         manifest. A store path that is not a path took the whole surface down over its one
         convenience view.

      **Round three also turned up the one finding in this branch that the lenses did not make,
      and it is recorded that way deliberately: the user noticed it.** `LaunchTests` proved its
      guard non-vacuous by calling `Launch.Open` on a loopback URL and asserting no refusal was
      printed, reasoning that whether a browser starts is the platform's business and there is
      none on a CI runner. True on a runner; false on every machine this is developed on, where it
      opened a real browser tab on **every run of the suite** — which is many runs an hour during a
      review. The guard is now a predicate, `Launch.IsWebAddress`, asserted directly; the refusal
      cases still go through `Open`, because the refusal path is the one path that hands nothing to
      the platform. *A test whose side effect is invisible to CI is invisible to the person reading
      CI.*

      **Round four found four more, and two of them were introduced by round three's own fixes** —
      which is why the rule says re-run the lens rather than run the next one:

      1. **Security-adjacent, and the largest of the branch.** A seat outlived its evidence.
         `SeatHold` retries a broker that has gone away, on purpose; but when the reattach is
         *refused* — the name retired, or taken since by something that is not a seat — the retry
         loop stopped and the hold stayed in the book forever with no stream behind it. Both
         surfaces then drew somebody as present in a deployment that had never heard of them. This
         is the cookie-outliving-a-seat failure with the cookie removed, and it was in
         `Parley.Client`, so it was the console's too. `SeatHold.Lost` records it; `SeatBook` acts
         on it in all four places it had to — `Held`, `Seats`, `Sweep`, and **`TakeAsync`, which
         was the door inspection missed**: rejoining after a lost seat was handed back the dead
         hold and told it was already seated.
      2. *Correctness.* Marking the loss at the one exit that was known about left the exit nobody
         thought of. An unexpected exception in the pump faults a task nothing observes, and the
         seat it abandoned would sit in the book until the process restarted. It is a `finally`
         now: however the read ends, only cancellation is a person leaving.
      3. *Correctness, introduced by round three's fix #2.* The menu wrote the hold back over the
         session's identity — `me = hold?.Seat` — so walking into a project you are **not** seated
         in forgot who you were, and coming back out and into the one you *are* seated in showed a
         join. The same disagreement as #2, through the door of a second project. Watched fail
         before being trusted: reinstating the line fails the new test.
      4. *Correctness, same shape.* A **refused** join cleared `me` as well, so somebody seated in
         one project lost that seat's name to a failed attempt to take a second one.

      Round four also over-corrected once and it was caught in the same round: the containment for
      an `RpcException` reaching the menu released the seat, on the reasoning that a broker which
      cannot answer has no seat to answer about. That fights `SeatHold`'s deliberate retry and
      would turn a two-second restart into a departure the person has to undo. It reports and does
      not act; the book decides.

      **Round five found nothing above low severity.** What it did find is written down rather than
      fixed: the fake `Agents` clients are near-duplicated across two test assemblies and cannot be
      shared without a test project that does not exist, and a 20-second polling budget is declared
      in two files.

      **Five rounds.** 1,711 tests, 0 warnings.

---

## Baseline

*Filled by T005 before anything moves, and compared by T013 after.*

| Assembly | Before move | After move |
|---|---|---|
| Parley.Broker.Tests | 218 | 218 |
| Parley.Cli.Tests | 322 | 322 |
| Parley.Core.Tests | 189 | 189 |
| Parley.Headless.Tests | 49 (1 skipped) | 49 (1 skipped) |
| Parley.Mcp.Tests | 171 | 171 |
| Parley.Server.Tests | 85 | 85 |
| Parley.Spec.Tests | 19 | 19 |
| Parley.Store.Tests | 421 | 421 |
| Parley.Web.Tests | 168 | 105 |
| Parley.Client.Tests | 1 | 64 |
| **Total** | **1643** | **1643 — all passing** |

`Parley.Client.Tests` already reports 1 before a single test moves. That is not noise: the linked
`ManifestIsolation.cs` carries its own check, deliberately — *"linked beside it so every assembly
that takes the guard also takes its check"*. The new assembly took the guard, so it took the test.

**The invariant T013 checks**: `Web_after + Client_after == 169`, and every other assembly
unchanged at the numbers above. The skip in Headless is `LiveRunnerTests` opting out because its
environment variable names no real agent runner — pre-existing and unrelated.

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
