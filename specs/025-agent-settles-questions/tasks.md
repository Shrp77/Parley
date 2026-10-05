# Tasks: The person talks; the agent decides what it settled

**Input**: Design documents from `specs/025-agent-settles-questions/`
**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/wire.md, contracts/surfaces.md, quickstart.md

**Tests**: Required. This repository's constitution asks for every storage rule to be tested by removing it and watching
the test fail (principle V), and every behaviour change to be driven over the wire where it can be. Each test task names
what it must fail against.

**Order rule (the owner's, 2026-10-03)**: SPEC.md first, then **all** the code, then the walk, then the review gate.
No part is reviewed while the code is behind the spec.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (different files, no dependency on an unfinished task)
- **[Story]**: US1–US5 from spec.md

## Phase 1: Setup — the specification first

**Purpose**: Principle I. SPEC.md is amended in its own commit before any code.

- [X] T001 Amend `SPEC.md`: new §5.6 *A person's line, and what the agent settles* (the person's line is always a Request;
  `settles` on a Result or Clarify from the receiver of the person's request; settling the agent's own request closes it
  `settled` citing the settling exchange; naming the person's own request awaiting a clarification hands the turn back;
  the five refusals of research R5; refusal is whole; limited to the person — FR-009; the settleable list and its bounds,
  R8). Cross-reference it from §5.2, §5.3 (the outcome table gains `settled`, "written by the requester, for the
  person's line"), §12.3 (surfaces send a person's line as a Request) and §13 (the settleable list is the agent's own
  words and is bounded)
- [X] T002 Amend `SPEC.md` §9.2's `exchanges` DDL (around line 1390) to match data-model.md: `outcome` admits
  `settled`, `settled_by` column with both CHECKs; add `tr_settled_by_its_requester` to §9.2 and a row for it in §9.3's
  invariant table
- [X] T003 Amend `SPEC.md` §10.1 (`result` and `clarify` take `settles`; what `inbox` returns beside a person's request)
  and §18 (protocol 0.8: what changed and why a 0.7 adapter is refused); run
  `tests/Parley.Spec.Tests/bin/Debug/net10.0/Parley.Spec.Tests.exe` (the schema fragments execute) and commit SPEC.md
  alone

---

## Phase 2: Foundational — the vocabulary, the wire and storage

**Purpose**: Everything every story reads. Blocks all stories.

- [X] T004 [P] `src/Parley.Core/ProtocolVersion.cs`: `Current` = `0.8`
- [X] T005 [P] `src/Parley.Core/ExchangeOutcome.cs`: add `Settled = 3`; `src/Parley.Core/TransitionError.cs` (or where
  `TransitionError` lives): add `CannotSettle`; new `src/Parley.Core/SettleProblem.cs` with the five problems of
  contracts/wire.md
- [X] T006 `src/Parley.Core/Exchange.cs`: `SettledBy` property; `Settle(AgentName from, long settling, DateTimeOffset now)`
  (open, `from` is requester, turn is the receiver's → closed, `Settled`, `SettledBy`, `ClosedUtc`); `TurnBack(AgentName
  from)` (open, `from` is receiver, turn is the requester's → `OwedBy` receiver, depth unchanged); carry `SettledBy`
  through the rehydrating constructor
- [X] T007 [P] `tests/Parley.Core.Tests/SettlingTransitionsTests.cs`: each transition's success and every refusal
  (closed, wrong party, wrong turn); `TurnBack` leaves `ClarifyDepth` unchanged
- [X] T008 `src/Parley.Store/Migrations/001_initial.sql`: `exchanges.outcome` admits `settled`; `settled_by INTEGER
  REFERENCES exchanges(id)` with `CHECK ((outcome = 'settled') = (settled_by IS NOT NULL))` and `CHECK (settled_by IS
  NULL OR settled_by <> id)`; trigger `tr_settled_by_its_requester` per data-model.md (same conversation; settling
  exchange's requester = this receiver and receiver = this requester; this receiver's adapter is `parley-console`)
- [X] T009 `src/Parley.Store/ExchangeRepository.cs` (and `ExchangeRow.cs`): read and write `settled_by`; map
  `settled` both ways; new `GetSettleableAsync(AgentName agent, AgentName person, string conversationId, int limit, …)`
  returning the open exchanges between them in that conversation owed by the person, oldest first, with the last thing
  the agent said in each and the total count
- [X] T010 [P] `tests/Parley.Store.Tests/SettledExchangeTests.cs`: a well-formed settle is stored; each storage rule
  refuses its malformed case — `settled` without `settled_by`, `settled_by` without `settled`, settling itself, a
  settling exchange in another conversation, one between other parties, one whose receiver is not the person, settling
  the person's own request. **Remove each rule from the schema in turn and watch its case be admitted** (from a copy of
  the file, never `git checkout`)
- [X] T011 `proto/parley.proto` per contracts/wire.md: `settles` on `ClarifyRequest` and `ResultRequest`; `SayReply.
  cannot_settle`; `SettleRefusal`; `SettleProblem`; `TRANSITION_ERROR_CANNOT_SETTLE`; `DeliveredMessage.settleable` and
  `settleable_left_out`; `SettleableQuestion`; `EXCHANGE_OUTCOME_SETTLED`; `ExchangeSnapshot.settled_by`
- [X] T012 `src/Parley.Server/Wire.cs`: map every new enum and field both ways; `tests/Parley.Server.Tests`'s
  `TransportEnumTests` must cover `Settled`, `CannotSettle` and every `SettleProblem` (none may map to unspecified)
- [X] T013 [P] `src/Parley.Client/BrokerClient.cs` and `src/Parley.Core/ExchangeSnapshot.cs` (or where the client-side
  snapshot lives): carry `SettledBy` and `Settled` through `Export`

**Checkpoint**: builds; Core, Store and Spec suites green.

---

## Phase 3: User Story 1 — A reply is never filed as the answer to the wrong question (P1) 🎯 MVP

**Goal**: every line the person types to an agent is a new Request.

**Independent test**: three of `orders`' requests owed by the person; a line to `orders`; a fourth exchange opens owed
by `orders` and the three stay open.

- [X] T014 [P] [US1] (built as the reworked `ComposingTests` and `LineIsARequestOverTheWireTests`: `Plan` and the acts were removed, so there is no derivation left to test headless) `tests/Parley.Client.Tests/EveryLineIsARequestTests.cs`: `Composing.Plan` gives `Act.Ask` for a
  peer the person owes one request, several, a clarification on the person's own request, and nothing; and for several
  recipients at once. Fails against today's `Plan`
- [X] T015 [US1] `src/Parley.Client/Seating/Composing.cs`: `Plan` returns `Act.Ask` for every recipient; rewrite the
  remarks (why the surface no longer derives an act, citing the 2026-10-05 report and research R1); remove the
  `Answer`/`Clarify` arms
- [X] T016 [US1] `src/Parley.Client/Seating/SeatVoice.cs`: `SendAsync` no longer has answer/clarify branches or the
  "refused act falls back to a Request" path for the box (0.30.7's fallback is moot); keep `ResultAsync` and
  `ClarifyAsync` for the explicit paths (web Inbox, terminal delivery dialogs)
- [X] T017 [US1] `src/Parley.Client/Seating/Pairing.cs`: `PairState` stops carrying one chosen exchange for the box;
  keep turn and role per peer for the room's standing and add the list of every exchange the person owes each peer
  (used by US3/US4). Update the remarks
- [X] T018 [US1] `src/Parley.Terminal/LineRegion.cs` (and wherever the preview is written): the preview says who the
  line goes to and nothing about answering or clarifying (FR-003)
- [X] T019 [P] [US1] `tests/Parley.Terminal.Tests/LineIsARequestOverTheWireTests.cs`: driven terminal, real loopback
  broker: `orders` sends the seated person three requests; the person types a line to `orders` and presses Enter;
  assert through `Export` that a new exchange requested by the person is open and the three are still open and owed by
  the person. Fails against 0.40.1
- [X] T020 [US1] Sweep and fix the tests that asserted the old acts (`ComposingTests`, `LineTests`, any `Answer`
  preview assertions): each is either replaced by its US1 equivalent or deleted with a line in the commit saying which
  and why

**Checkpoint**: Client and Terminal suites green; US1 testable alone.

---

## Phase 4: User Story 2 — The agent says which of its questions the line settled (P1)

**Goal**: `settles` on the agent's reply closes its own questions to the person, or hands the person's own request back.

**Independent test**: `orders` with two requests open to the person replies to a person's request settling one; one
closes `settled` citing the person's exchange, one stays open.

- [X] T021 [US2] `src/Parley.Broker/Broker.cs`: `ResultAsync` and `ClarifyAsync` take `IReadOnlyList<long> settles`;
  `SayAsync` validates and applies them inside its `WriteScope` before appending the message (research R5's five checks
  in order; any failure returns `SayOutcome.Refused(TransitionError.CannotSettle, …)` with each id and problem and
  stores nothing); de-duplicate ids; at most 50; settling the agent's own request calls `Exchange.Settle`, the person's
  own request calls `Exchange.TurnBack`; both saved in the same transaction. `SayOutcome` carries the refusals and what
  was settled and turned back
- [X] T022 [US2] `src/Parley.Broker/Broker.cs` `DrainInboxAsync`: for a Request whose sender is the person (`PersonSeat.
  Is`), attach the settleable list from `GetSettleableAsync` (20 entries, 160 characters each cut at a character
  boundary with `…`, plus the count left out); `src/Parley.Broker/Inbox.cs` / `DeliveredMessage` carry it
- [X] T023 [US2] `src/Parley.Server/AgentsService.cs`: pass `settles` through from both requests; return
  `cannot_settle`; `DeliveredMessage.settleable` out
- [X] T024 [P] [US2] `tests/Parley.Broker.Tests/SettlingTests.cs`: settle one of two; settle the person's own request
  after a Clarify (turn back, depth unchanged, no message on it); each of the five refusals refuses the whole reply and
  stores nothing (message count and the person's request state unchanged); a repeated id settles once; a reply that is
  not on a person's request refuses any `settles`; between two agents nothing can be settled (FR-009)
- [X] T025 [P] [US2] `tests/Parley.Server.Tests/SettlingOverTheWireTests.cs`: the quickstart's steps 1–5 through
  `AttachedClient`/`LoopbackBroker`: what the agent is told on drain, the settle, the refusal, the turn back; and two
  replies racing to settle one exchange (one wins, the other is refused whole)
- [X] T026 [US2] `src/Parley.Mcp/ParleyTools.cs`: `parley_result` and `parley_clarify` take `settles` (array of
  exchange ids) and report `settled` / `handed_back`, and each refusal as a sentence per contracts/surfaces.md;
  `parley_inbox` emits `your_open_questions_to_them`, `more_open_questions` and the `settling` line beside a person's
  request
- [X] T027 [US2] `src/Parley.Mcp/ParleyInstructions.cs` and the two tool descriptions: the paragraph of research R11,
  including that the reply must say which questions it settled and that asking for clarification settles nothing
  (FR-021, FR-022)
- [X] T028 [P] [US2] `tests/Parley.Mcp.Tests/SettlesToolTests.cs`: the built server over stdio: inbox shows the list;
  a Result with `settles` settles; a bad id comes back as a sentence naming it
- [X] T029 [US2] `src/Parley.Client/Rendering/Reading.cs`: a settled exchange reads `settled by <agent> in exchange <n>`
  on both surfaces and in the export (FR-017); and under the agent's reply that settled questions, a line *Settled:
  "<start>", …* found by research R12's match (FR-021), drawn by both surfaces since both lay out through `Reading`;
  `tests/Parley.Client.Tests/SettledReadsAsSettledTests.cs` covers both, including a reply whose own words say nothing
  about settling

**Checkpoint**: Broker, Server, Mcp and Client suites green; US1+US2 together are the shippable fix.

---

## Phase 5: User Story 3 — One row per agent with a count, and the question list (P2)

**Goal**: the room's *waiting on you* group as the owner described it.

**Independent test**: one agent with three questions waiting, another with one: two rows with 3 and 1; Enter lists
three oldest first; choosing the second shows it in the transcript.

- [X] T030 [US3] `src/Parley.Client/Seating/WaitingOnYou.cs` (new): the shared derivation — per peer, every exchange the
  person owes in the conversation, oldest first, with the message to show (the latest thing the peer said in it, 0.30.1's
  rule) and the start of its text. Used by both surfaces
- [X] T031 [US3] `src/Parley.Terminal/Shell.cs` `Outstanding` and `src/Parley.Terminal/RoomRegion.cs`: one row per peer,
  `<name>  <count>`, no "waiting for your reply"; the row keeps its peer and its questions
- [X] T032 [US3] `src/Parley.Terminal/Shell.cs`: Enter on such a row opens `Prompting.Choose` titled
  `Waiting on you from <name>`, entries `MMM dd HH:mm  <start of question>` cut to the width; choosing one moves the
  transcript to the message (the room's existing jump, two rows of context); a question gone from the transcript says
  "That question has been settled since." and does not move it
- [X] T033 [P] [US3] `tests/Parley.Terminal.Tests/WaitingOnYouListTests.cs`: drawn rows and counts read off the laid-out
  screen; Enter opens the list in order; choosing moves the transcript (assert the row on screen); the stale entry; the
  old "waiting for your reply" text never drawn. Fails against 0.40.1
- [X] T034 [US3] Keep `NoTwoActionsShareAKeyTests`, `EveryKeyIsOfferedTests` and `KeysNeedAModifierTests` green: Enter
  on the room's row is already offered; name the list on the status line if the action table requires it

---

## Phase 6: User Story 4 — The web UI offers the same (P2)

- [X] T035 [US4] `src/Parley.Web/Pages/Conversations/Detail.cshtml.cs` and `Detail.cshtml` (or `_Conversation.cshtml`
  so the follow partial carries it): a *Waiting on you* section from `WaitingOnYou`, one line per agent with the count,
  each question a link to the message's existing anchor
- [X] T036 [P] [US4] `tests/Parley.Web.Tests/WaitingOnYouOnThePageTests.cs`: the page lists the same agents and counts
  as the terminal's derivation for one export; each link targets an anchor that exists on the page; agent-written text
  in the question starts is escaped

---

## Phase 7: User Story 5 — The person is never shown as open outside Parley (P3)

- [X] T037 [US5] `src/Parley.Server/OperatorsService.cs` (or `BrokerRuntime`'s registry fill): never set
  `open_outside` on the person's or the facilitator's row
- [X] T038 [P] [US5] `tests/Parley.Server.Tests/PersonIsNotOpenOutsideTests.cs`: seat the person, read the registry,
  both rows false. Fails against 0.40.1

---

## Phase 8: Polish & cross-cutting

- [X] T039 `Directory.Build.props`: `VersionPrefix` 0.41.0 (minor: protocol change); every test that pins the
  protocol version reads `ProtocolVersion.Current`
- [X] T040 [P] `README.md` and `plugins/parley/` (skills and commands that describe `parley_result`, answering the
  person, or what the room shows): say a person's line is a request and that agents settle their own questions;
  `PluginTests` green
- [X] T041 [P] `CLAUDE.md`: the 025 entry — what building it found
- [X] T042 Full suites through the executables (`tests/Parley.*.Tests/bin/Debug/net10.0/Parley.*.Tests.exe`), not
  `dotnet test`; every one green
- [ ] T043 Write `specs/025-agent-settles-questions/walk.ps1` and walk quickstart steps 1–5, 7 and 8 against packed tools
  in a scratch root with every `PARLEY_` variable cleared; record each check in `REVIEW-LEDGER.md`
- [ ] T044 Quickstart step 6 (SC-007) with a real agent on the live deployment — **the owner's call to run it**; record
  what the agent actually did
- [X] T045 Hygiene: case-insensitive grep for the private names and for any quotation from the live conversation,
  **including untracked new files**, run on its own before each commit
- [ ] T046 The combined review gate (019–025, T060 of 023): three lenses, three consecutive clean runs each, from zero,
  logged in `REVIEW-LEDGER.md`, agents isolated from the live deployment

---

## Dependencies & Execution Order

- **Phase 1 (SPEC.md)** blocks everything (principle I).
- **Phase 2** blocks every story.
- **US1 and US2 ship together**: US1 alone would leave every question an agent asks the person open for ever (spec,
  US2's priority note). Build US1 first, then US2, before releasing either.
- **US3 and US4** depend on T017 and T030; independent of each other once T030 exists.
- **US5** depends only on Phase 2.
- **Phase 8** after every story; T046 last of all, after the last edit.

### Parallel opportunities

- T004, T005, T007 (Core); T010 once T008 exists; T013 beside T011–T012.
- Within US2: T024, T025 and T028 once T021–T023 and T026 exist.
- US3's T033 and US4's T036 in parallel once T030 exists; US5 any time after Phase 2.

## Implementation Strategy

One push, spec first, under the owner's rule that the code matches the spec before review. US1+US2 are the fix the live
deployment needs; US3–US5 follow in the same build. The walk and the gate come after the last edit, never alongside it.
