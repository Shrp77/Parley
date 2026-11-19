---

description: "Task list for feature 027: work comes back when the session that took it is gone"
---

# Tasks: Work comes back when the session that took it is gone

**Input**: Design documents from `specs/027-session-owned-delivery/`
**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/wire.md, contracts/spec-text.md, quickstart.md

**Tests**: Requested. User Story 3 and FR-010 require a deterministic reproduction of the race, and the
review gate requires every new storage check to be probed by a case that tries to break it. Write each
test first and watch it fail before the code that makes it pass.

**Organization**: The schema, wire and session plumbing are shared by every story, so they sit in
Phase 2. Each story phase then adds its tests and the behaviour they hold.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependency on an unfinished task)
- **[Story]**: Which user story the task serves (US1, US2, US3, US4)

---

## Phase 1: Setup — the specification first

**Purpose**: Constitution principle I. Code follows the text.

- [ ] T001 Edit SPEC.md §9: add `sessions`, `ix_sessions_agent_open` and the four triggers (`sessions_opened`, `sessions_closed`, `sessions_stay_closed`, `sessions_delete_closed_only`) exactly as in specs/027-session-owned-delivery/data-model.md; add `delivered_session`, `delivered_horizon` and their two `CHECK`s to `messages`; rewrite the `agents.connected` comment (lines ~1308-1317) as contracts/spec-text.md §9 says
- [ ] T002 Edit SPEC.md §6 (*A receiver that restarts does not lose what it owes*, line ~973) with the wording in contracts/spec-text.md, removing the old paragraph (FR-009)
- [ ] T003 Edit SPEC.md §4.0 (*Attaching and detaching are the stream*, line ~258): add the sentence that `HandBack` is not a detach. Edit §10.1: `inbox()` names its session; add `hand_back()`
- [ ] T004 Add SPEC.md §14 subsection *Sessions and the work they hold* after the liveness table: the three-row table and the three rules (hand back, reclaim, newer session), plus the accepted residual and the accepted limit in one sentence each, per contracts/spec-text.md
- [ ] T005 Add the §18 entry for protocol 0.9 (or the next free number if 026 merged first), per contracts/spec-text.md

**Checkpoint**: SPEC.md states the rule once. The proto excerpts are updated with the proto in T006.

---

## Phase 2: Foundational (blocking all stories)

**Purpose**: Wire, schema, and the session id travelling from attach to collection.

### Wire

- [ ] T006 Edit proto/parley.proto and SPEC.md's proto excerpts together, per contracts/wire.md: `Attached` loses `exchanges_redelivered` (`reserved 2;`) and gains `session = 3`; `AttachRequest` gains `previous_session`; `DrainInboxRequest` gains `session = 3`; add `rpc HandBack(HandBackRequest) returns (Empty)` and `HandBackRequest`. Build, then run tests/Parley.Spec.Tests/bin/Debug/net10.0/Parley.Spec.Tests.exe until green
- [ ] T007 [P] Set `ProtocolVersion.Current` to "0.9" (or the number chosen in T005) in src/Parley.Core/ProtocolVersion.cs
- [ ] T008 [P] Bump `VersionPrefix` to 0.43.0 in Directory.Build.props
- [ ] T009 Decide `session_ref`: grep its readers (src/Parley.Store/AgentRepository.cs, src/Parley.Core/AgentRecord.cs, src/Parley.Server/AgentsService.cs, src/Parley.Server/Wire.cs, src/Parley.Client/BrokerClient.cs, src/Parley.Client/Seating/SeatBook.cs). If nothing acts on its value, remove it from the proto, SPEC.md §9, 001_initial.sql and those files; otherwise record in research.md R1 why it stays beside `sessions.id`

### Storage

- [ ] T010 Edit src/Parley.Store/Migrations/001_initial.sql to match SPEC.md §9 from T001, text identical so `Parley.Spec.Tests` keeps agreeing
- [ ] T011 Create src/Parley.Store/SessionRepository.cs: `OpenAsync(agent, previousSession, nowUtc)` generates 32 lowercase hex characters from `RandomNumberGenerator` (research R5), inserts with `INSERT ... SELECT ... WHERE status = 'active'`, and, when `previousSession` names a session of the same agent, sets `place` and moves its deliveries in the same transaction (data-model.md *Reclaim on attach*); returns the id or null for a retired or unknown agent. `CloseAsync(session, nowUtc)` sets `closed_utc` and deletes the row if no message refers to it. `CloseAllAsync(nowUtc)` for broker start: closes every open row, then deletes unreferenced closed rows. `GetOwnerAsync(session)` returns the agent and whether it is open. Each member's `<remarks>` says why
- [ ] T012 Edit src/Parley.Store/AgentRepository.cs: remove `MarkLiveAsync` and `MarkOfflineAsync`; the startup clear (line ~955) calls the sessions close-all instead of writing `connected`; the agent inserts near lines 546 and 624 write `connected = 0` regardless of input; fix the `<remarks>` near lines 78 and 478 that describe counting
- [ ] T013 Edit src/Parley.Store/MessageRepository.cs: `MarkDeliveredAsync` (line ~321) takes the session and writes `delivered_horizon = (SELECT max(seq) FROM sessions)`; `RedeliverAsync` clears all three columns; add `HandBackAsync(agent, session)` and `GiveBackAsync(agent, collectingSession)`, each the single `UPDATE` from data-model.md, returning the count
- [ ] T014 Edit src/Parley.Store/ExchangeRepository.cs: remove `GetAbandonedOwedByAsync`; delete tests/Parley.Store.Tests/AbandonedOwedByTests.cs, moving any scenario that still applies into T015
- [ ] T015 [P] Write tests/Parley.Store.Tests/SessionStorageTests.cs probing each rule by trying to break it: a delivery without a session or horizon is refused; a malformed id is refused; a retired agent gets no session; reopening a closed session aborts; deleting an open session aborts; two opens give `connected = 2`, one close gives 1; close-all leaves `connected = 0` and keeps referenced rows; `GiveBackAsync` returns only the latest owed message, never the agent's own, never a closed exchange's, never an open session's, and never to a collector at or below the horizon; `HandBackAsync` touches only its own session; a reclaim moves deliveries and copies the place, and one naming another agent's session moves nothing. Fix every other Store test that wrote `connected` directly so it opens sessions (AttachmentTests.cs, AgentRepositoryTests.cs and any the build or the triggers reveal)

### Broker and server plumbing

- [ ] T016 Edit src/Parley.Broker/Broker.cs `StartAsync` (line ~824): take an optional previous session; drop the `IsAttachedAsync` read and the redelivery loop; open a session through `SessionRepository` and keep its id; `DetachAsync` closes it. Edit src/Parley.Broker/StartResult.cs: replace `ExchangesRedelivered` with `Session`. Rewrite both methods' `<remarks>`, deleting "Nothing is given again if a session is already attached"
- [ ] T017 Edit src/Parley.Broker/Broker.cs `DrainInboxAsync` (line ~971) to take the session and pass it to `MarkDeliveredAsync`. The give-back comes in T024, after its test fails
- [ ] T018 Edit src/Parley.Server/BrokerRuntime.cs: the startup clear (line ~183) calls close-all; `_attached` keeps each `AttachedAgent` with its session id; add a lookup from session id to its attached `Broker` so a drain runs on the session it names rather than `sessions[^1]`
- [ ] T019 Edit src/Parley.Server/AgentsService.cs: pass `request.PreviousSession` to the start; send `Attached.Session`; `DrainInbox` (line ~299) resolves `request.Session` through T018 and refuses per contracts/wire.md

**Checkpoint**: `dotnet build` is green. Nothing gives work back yet, so tests that asserted `ExchangesRedelivered` fail until Phase 3 rewrites them.

---

## Phase 3: User Story 1 — a fast restart gets its unfinished work back (P1) 🎯 MVP

**Goal**: A new process receives the work of a session that stopped, whichever order the broker hears the stop and the new start.

**Independent test**: tests/Parley.Broker.Tests/SessionOwnedDeliveryTests.cs, the six steps in quickstart.md.

- [ ] T020 [P] [US1] Edit src/Parley.Mcp/ParleyTools.cs (line ~414) to send the current session on `DrainInbox`, and src/Parley.Mcp/BrokerLink.cs to keep the session from each `Attached` event (the read at line ~296 and the first attach)
- [ ] T021 [P] [US1] Edit src/Parley.Client/Seating/SeatVoice.cs (line ~526) and src/Parley.Client/Seating/SeatHold.cs the same way for the person's surfaces
- [ ] T022 [US1] Write tests/Parley.Broker.Tests/SessionOwnedDeliveryTests.cs `A_fast_restart_gets_its_work_back`: `billing` sends `orders` a Request; session A attaches and collects; session B attaches while A is attached; B collects nothing; A detaches with no hand-back; B collects and receives the Request first. Add `The_end_recorded_first_gives_it_back_on_the_first_collection` and `A_slow_restart_still_gets_its_work_back` (US1 scenarios 2 and 3). Confirm the race case fails before T024
- [ ] T023 [US1] Add a shared helper under tests/Parley.Broker.Tests that starts a broker and keeps its session, and use it to pass a session to every other `DrainInboxAsync` caller in that project (the build lists them)
- [ ] T024 [US1] Edit src/Parley.Broker/Broker.cs `DrainInboxAsync`: call `MessageRepository.GiveBackAsync(agent, session)` before `GetPendingAsync`, inside the same write gate. Rerun T022 until green; then delete the call, watch the race case fail (mutation check), and restore it
- [ ] T025 [P] [US1] Add to SessionOwnedDeliveryTests.cs: work collected before a broker restart reaches a fresh session afterwards, and nothing is woken for it beyond what §14's resume policy already wakes (FR-006); a Clarify owed and delivered to a stopped session comes back (spec edge case)
- [ ] T026 [P] [US1] Rewrite the `ExchangesRedelivered` assertions in tests/Parley.Broker.Tests/BrokerLifecycleTests.cs as "the next collection by a newer session returns it", keeping each scenario
- [ ] T027 [P] [US1] The same in tests/Parley.Broker.Tests/AttachEdgeTests.cs
- [ ] T028 [P] [US1] The same in tests/Parley.Broker.Tests/InboxTests.cs and tests/Parley.Broker.Tests/ManyAgentsTests.cs
- [ ] T029 [P] [US1] The same in tests/Parley.Broker.Tests/FullDiscussionTests.cs

**Checkpoint**: Broker tests are green and the race test fails without the give-back.

---

## Phase 4: User Story 2 — two open windows do not both get the same work (P1)

**Goal**: A live window keeps its work; a window that ends normally hands it to the other; a window that was already open never takes another's work after it stops abruptly.

- [ ] T030 [US2] Write the hand-back test first in SessionOwnedDeliveryTests.cs, `The_second_window_gets_it_once_the_first_hands_back`, and watch it fail; then add `HandBackAsync(cancellationToken)` to src/Parley.Broker/Broker.cs calling `MessageRepository.HandBackAsync` for this broker's session, and update `SurfaceBoundaryTests`' expected surface deliberately
- [ ] T031 [US2] Add to SessionOwnedDeliveryTests.cs: `A_second_window_does_not_get_the_first_windows_work`, `The_second_window_closing_leaves_the_first_its_work`, `An_older_window_does_not_get_a_stopped_windows_work` (US2 scenarios 1, 3, 4) and `The_same_session_collecting_twice_is_not_given_its_own_work_again`. Mutation check for scenario 4: drop the horizon condition from `GiveBackAsync` and watch it fail
- [ ] T032 [US2] Edit src/Parley.Server/AgentsService.cs: add `HandBack`, refusing per contracts/wire.md; accepted for a closed session of the caller; idempotent
- [ ] T033 [P] [US2] Edit src/Parley.Mcp/BrokerLink.cs `DisposeAsync` (line ~331): call `HandBack` with the current session before cancelling the stream; a failure there is swallowed, since a missing hand-back loses nothing. Add a comment that this is not a detach (§4.0)
- [ ] T034 [P] [US2] Edit src/Parley.Client/Seating/SeatBook.cs (idle drop near line ~330) and the surface-close path to call `HandBack` before dropping a seat's stream
- [ ] T035 [P] [US2] Run tests/Parley.Client.Tests/TwoSurfaceDeliveryTests.cs and tests/Parley.Client.Tests/DoubleSeatTests.cs; fix them to send their own session, not to change what they assert

**Checkpoint**: Both P1 rules about windows hold in one test class.

---

## Phase 5: User Story 4 — a dropped connection keeps its work (P1)

**Goal**: A process that reconnects after a blip, sleep or broker restart keeps its work, and no other window takes it meanwhile.

- [ ] T036 [US4] Add to SessionOwnedDeliveryTests.cs, test first: `A_reconnect_naming_its_session_keeps_its_work` (US4 scenario 1; nothing is delivered again); `An_older_window_does_not_take_work_while_the_holder_is_away` (scenario 2); `A_broker_restart_under_two_windows_keeps_each_windows_work` in both reconnect orders (scenario 3); `A_reclaim_naming_another_agents_session_opens_fresh`
- [ ] T037 [US4] Edit src/Parley.Mcp/BrokerLink.cs `ReattachAsync` (line ~286): send the last session as `previous_session` on each re-attach (the `_request` is currently reused unchanged), and replace the kept session with the new one from `Attached`. Update the `<remarks>` at line ~276, which says re-attaching "releases whatever this agent abandoned"
- [ ] T038 [P] [US4] Edit src/Parley.Client/Seating/SeatHold.cs `ReattachAsync` (line ~232) the same way
- [ ] T039 [US4] Write tests/Parley.Mcp.Tests/BrokerLinkSessionTests.cs: after the broker restarts under a running adapter, the re-attach names the previous session and the adapter still holds its work; disposing the link hands back. Follow BrokerAwayTests.cs for the restart harness

**Checkpoint**: SC-005 holds at the broker and at the adapter.

---

## Phase 6: User Story 3 — the race and the rules over the wire (P2)

**Goal**: The race, the reclaim and the refusals are reproduced through gRPC with no timing.

- [ ] T040 [US3] Write tests/Parley.Server.Tests/FastRestartTests.cs `A_fast_restart_gets_its_work_back_over_the_wire`: open stream A, collect, open stream B, cancel A and wait with `broker.WaitUntilDetachedAsync` (as NodeTests.cs does) only after B is attached, then collect from B. Add `A_dropped_stream_reattaching_with_its_session_keeps_its_work`. Do not open a `SqliteConnection` or touch a `Broker` (project rule)
- [ ] T041 [US3] Add the refusals from contracts/wire.md to FastRestartTests.cs (`DrainInbox` with no, a closed, and another agent's session; `HandBack` with no, an unknown and another agent's session); assert status code and that the message names the fix
- [ ] T042 [US3] Fix the remaining tests/Parley.Server.Tests callers of `DrainInbox` (ConcurrentDrainTests.cs, LimitedCollectionTests.cs, WakeDuringCollectionTests.cs and whatever the run lists) through the shared attach helper, not file by file
- [ ] T043 [P] [US3] Fix tests/Parley.Cli.Tests and tests/Parley.Terminal.Tests callers the full build reports (AskedWithNobodyAskingTests.cs, FlagsOverTheWireTests.cs and others)

**Checkpoint**: Every test project builds and passes locally.

---

## Phase 7: Polish and cross-cutting

- [ ] T044 Measure research R11: with a deployment running, open an editor on an enrolled repository, collect a Request, close the editor normally, and check whether the broker received `HandBack` (log line or the message's delivery columns). Record the answer in research.md R11
- [ ] T045 Sweep for the old rule (one rule, no history in docs): `git grep -n -i "already attached\|exchanges_redelivered\|ExchangesRedelivered\|given again\|releases whatever"` across SPEC.md, README.md, plugins/parley and src `<remarks>`; rewrite or delete every hit
- [ ] T046 Update the CLAUDE.md State section: version 0.43.0, protocol 0.9 (and how it relates to 026); remove the stale `019-layout` bullet, which merged in #92
- [ ] T047 Run the full suite by running each `tests/Parley.*.Tests/bin/Debug/net10.0/Parley.*.Tests.exe` and read every failure by name (do not tail the output)
- [ ] T048 Run Linux in podman, looping `SessionOwnedDeliveryTests`, `FastRestartTests` and `BrokerLinkSessionTests` 30 times under load
- [ ] T049 Hygiene check before committing: case-insensitive `git grep` for the sibling repository names, plus the new untracked files (SessionRepository.cs, SessionOwnedDeliveryTests.cs, SessionStorageTests.cs, FastRestartTests.cs, BrokerLinkSessionTests.cs)
- [ ] T050 Open the PR (e.g. `fix: work comes back when the session that took it is gone (0.43.0)`); confirm 3 consecutive green CI runs on Windows, Linux and macOS at one commit, checking each run's SHA against `git rev-parse HEAD` (SC-003)
- [ ] T051 Run the review gate's three lenses after the last edit and log them in REVIEW-LEDGER.md; probes to include: reclaiming another agent's session, reclaiming a session that is still open, a drain racing a detach, a hand-back racing a reclaim, a hand-back after the stream dropped, and a retired agent attaching

---

## Dependencies and execution order

- **Phase 1 → Phase 2**: the spec text comes first; T006 carries the proto into SPEC.md; T010 copies T001's DDL.
- **Inside Phase 2**: T009 before T010. T010 → T011–T014 → T015. T016–T017 need T011 and T013. T018–T019 need T016 and T006.
- **Phase 3 (US1)** needs Phase 2. T022 must fail before T024. T025–T029 follow T024.
- **Phase 4 (US2)** needs T024. T030's test fails before its code. T032–T034 need T030.
- **Phase 5 (US4)** needs T024; T037–T039 need T019.
- **Phase 6 (US3)** needs T019, T024, T032.
- **Phase 7** needs everything; T044 needs a built adapter installed in a real deployment.

## Parallel work

- Phase 2: T007, T008 with anything; T015 alongside T016.
- Phase 3: T020 with T021; T025–T029 once T024 is in.
- Phase 4: T033, T034, T035 together.
- Phase 5: T038 alongside T037.
- Phase 6: T043 alongside T040–T042.

## Implementation strategy

**MVP is Phases 1–3 (US1)**: it fixes the reported defect. It must not ship alone, because without
Phases 4 and 5 a blip hands one window's work to another, which is worse than today for two-window
users. **One PR for the whole feature**: the wire change breaks every caller at once, and there is no
compatibility to keep.
