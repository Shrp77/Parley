# Tasks: The Inbox follows an exchange after it is answered

**Input**: `specs/036-inbox-follows-exchanges/` (spec, plan, research, data-model, contracts)
**Tests**: included. The repository's rule is that a behaviour change carries the test that holds it.

## Phase 1: Setup

- [x] T001 Bump `VersionPrefix` to 0.56.0 in Directory.Build.props (a new wire read and new views) and the State line in CLAUDE.md

## Phase 2: Foundational

- [x] T002 Add `rpc LocateExchange(LocateExchangeRequest) returns (LocateExchangeReply)` to the Operators service in proto/parley.proto
- [x] T003 Add a read of an exchange's current conversation to src/Parley.Store/ExchangeRepository.cs (from its messages; null when none)
- [x] T004 Add `LocateExchangeAsync` to `IParleyBrokerClient` in src/Parley.Core and implement it in src/Parley.Broker/OperatorConsole.cs
- [x] T005 Bind `LocateExchange` in the server's Operators service in src/Parley.Server
- [x] T006 Add `BrokerClient.LocateExchangeAsync(long, CancellationToken) → string?` in src/Parley.Client/BrokerClient.cs
- [x] T007 [P] Server test: a known exchange names its conversation, after a move names the new one, an unknown one is empty, in tests/Parley.Server.Tests
- [x] T008 [P] Store test for the read in tests/Parley.Store.Tests

## Phase 3: User Story 1 — In flight (P1)

**Goal**: an answered or sent exchange stays visible under In flight with a chip.
**Independent test**: reply to a question; it is under In flight with a chip; when the agent asks back, it is under Waiting on you.

- [x] T009 [US1] Add `InboxView`, `InboxRow`, `InFlightStanding`, `ExchangeEnding`, `InboxReading` in src/Parley.Client/Seating/Inbox.cs
- [x] T010 [US1] Add `InFlightStanding` with the words of research R2 in src/Parley.Client/Seating/Inbox.cs
- [x] T011 [US1] Add `Inbox.ReadAsync(BrokerClient, AgentName me, window, older, ct)` building Waiting on you and In flight from the graph, registry, flags and exports in src/Parley.Client/Seating/Inbox.cs
- [x] T012 [P] [US1] Unit tests for view assignment (exclusive), the chip table and its precedence in tests/Parley.Client.Tests/InboxViewsTests.cs
- [x] T013 [US1] Inbox page: view switch (Waiting on you, In flight, Closed) with counts, In flight rows with chips and a conversation link, in src/Parley.Web/Pages/Inbox.cshtml(.cs)
- [x] T014 [US1] Digest changes when an edge the person is in appears, goes, or changes side, in src/Parley.Web/Pages/Inbox.cshtml.cs
- [x] T015 [US1] Web tests: a reply moves a row to In flight, an ask-back moves it back, chips read as R2, in tests/Parley.Web.Tests/InboxFollowsExchangesTests.cs

## Phase 4: User Story 2 — Closed (P2)

**Goal**: look back at what closed within the window.
**Independent test**: send a Result; it is under Closed with its outcome.

- [x] T016 [US2] Closed rows in `Inbox.ReadAsync`: conversations active since the window start, closed exchanges the person was on, outcome from the snapshot and the closing message, pages for "show older", in src/Parley.Client/Seating/Inbox.cs
- [x] T017 [P] [US2] Unit tests for endings (Done, Answered, Received, Blocked with kind, Settled, ClosedByPerson), retention-pruned exchanges, the window bounds in tests/Parley.Client.Tests/InboxViewsTests.cs
- [x] T018 [US2] Closed view on the Inbox page with "show older" and the window named, in src/Parley.Web/Pages/Inbox.cshtml(.cs)
- [x] T019 [US2] Web tests for Closed in tests/Parley.Web.Tests/InboxFollowsExchangesTests.cs

## Phase 5: User Story 3 — Follow up (P2)

- [x] T020 [US3] `OnPostFollowUpAsync` sending a Request to the row's agent in its conversation, words kept on refusal, not offered for the facilitator, in src/Parley.Web/Pages/Inbox.cshtml.cs
- [x] T021 [US3] Follow-up box (shared `_Composer`) on every row but the facilitator's in src/Parley.Web/Pages/Inbox.cshtml
- [x] T022 [US3] Web tests: sent to the right agent and conversation, old exchange untouched, refusal keeps words, no box for the facilitator, in tests/Parley.Web.Tests/InboxFollowsExchangesTests.cs

## Phase 6: User Story 6 — Exchange references (P2)

- [x] T023 [P] [US6] `ExchangeReferences.Find` per the grammar in src/Parley.Client/Rendering/ExchangeReferences.cs
- [x] T024 [P] [US6] Unit tests for every row of the contract's table in tests/Parley.Client.Tests/ExchangeReferencesTests.cs
- [x] T025 [US6] `ExchangeDetail.ReadAsync(BrokerClient, long, ct)` in src/Parley.Client/Seating/ExchangeDetail.cs
- [x] T026 [US6] References become buttons in plain text nodes only, in src/Parley.Web/MarkdownText.cs; `data-project` on message containers in Inbox and _Conversation
- [x] T027 [US6] Panel page src/Parley.Web/Pages/Exchanges/Panel.cshtml(.cs), no layout, read only
- [x] T028 [US6] Panel script and styles in src/Parley.Web/Pages/Shared/_Layout.cshtml
- [x] T029 [US6] Web tests: buttons only for the form, not in code or links; panel shows the exchange, says when there is none; reading changes nothing, in tests/Parley.Web.Tests/InboxFollowsExchangesTests.cs
- [x] T030 [US6] "exchange #N" in `StillOpen.Ask` (src/Parley.Client/Seating/StillOpen.cs) and the instruction bullet in src/Parley.Mcp/ParleyInstructions.cs; plugin README; update tests that pin either

## Phase 7: User Story 4 — The window setting (P3)

- [x] T031 [P] [US4] `ParleySettings` read and atomic write of `<root>/settings.json` in src/Parley.Configuration/ParleySettings.cs
- [x] T032 [P] [US4] Tests: missing file, bad values, out of range, unknown keys kept, in tests/Parley.Configuration.Tests
- [x] T033 [US4] Settings page section and handler in src/Parley.Web/Pages/Settings.cshtml(.cs); Inbox reads the window
- [x] T034 [US4] Web tests for the setting in tests/Parley.Web.Tests/ParleySettingsTests.cs

## Phase 8: User Story 5 — The terminal (P3)

- [x] T035 [US5] `InboxWindow` dialog (TableDialog, KeyContext, CommandTables) in src/Parley.Terminal/InboxWindow.cs
- [x] T036 [US5] `ExchangeView` dialog in src/Parley.Terminal/ExchangeView.cs
- [x] T037 [US5] Shell commands: open the Inbox, follow up, open exchange #N from the transcript cursor, in src/Parley.Terminal/Shell.cs and ShellCommands.cs
- [x] T038 [US5] Terminal tests in tests/Parley.Terminal.Tests/TheInboxOnTheTerminalTests.cs; KeysTakenBeforeTheTableTests samples

## Phase 9: Polish

- [x] T039 SPEC.md §5.6 (the views, the reference form as display only) and §10.2 (`LocateExchange`)
- [x] T040 Unit, web, client, terminal, server suites green; hygiene check; line endings kept
