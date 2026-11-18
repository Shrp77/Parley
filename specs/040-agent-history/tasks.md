# Tasks: Agents read the exchange history

**Input**: `specs/040-agent-history/` (spec, plan, research, data-model, contracts, quickstart)
**Tests**: included. This repository holds every rule by a test, and each guard is watched failing first (CLAUDE.md).
**Order**: spec first (principle I), then Core, Store, Broker, wire, adapter, surfaces, docs. Format: `- [ ] ID [P] [Story] text + path`.

## Phase 1: Setup and the spec

- [x] T001 Edit SPEC.md: principle 2.2 and §2's bullet on terseness (history crosses only as a bounded, framed, recorded read); §10.1 table rows `read_exchange` and `search_history` and the paragraph at "There is deliberately no history" replaced by what is allowed and what stays forbidden; §13 the history envelope; the §17 "Not a shared context store" bullet; §9.2 schema `history_reads` (version 9); §14 purge lists its rows; protocol 0.19. SPEC.md is CRLF: use the scripted-edit approach from the earlier spec edits, not the Edit tool.
- [x] T002 Amend `.specify/memory/constitution.md`: the "Boundaries That Do Not Move" sentence naming conversation history, and principle IV's bound list; bump its version and date.
- [x] T003 Bump `Directory.Build.props` to 0.62.0 and `ProtocolVersion.Current` in `src/Parley.Core/ProtocolVersion.cs` to "0.19"; CLAUDE.md state line.

## Phase 2: Foundational (blocks every story)

- [x] T004 [P] `src/Parley.Core/HistoryLimits.cs`: the numbers once (reply 64,000 characters, page 20, snippet 240, words 200, rate 30 calls and 400,000 characters per minute).
- [x] T005 `src/Parley.Store/Migrations/009_history_reads.sql` per data-model.md, with the checks and indexes; update the test that pins the latest schema version and the schema-one freeze list if it names versions.
- [x] T006 `proto/parley.proto`: the two rpcs and messages from `contracts/agents-history.proto.md`, and `HistoryReadRow reads` on the operator conversation read.
- [x] T007 [P] `tests/Parley.Store.Tests/HistoryReadsSchemaTests.cs`: each check refuses a bad row (kind, null pairing, words length, line-breaking characters); watch each fail with the check removed.

**Checkpoint**: migration applies to a copy of a live-shaped store with its rows kept.

## Phase 3: US1 An agent reads an exchange by number (P1)

**Goal**: `read_exchange` returns the exchange in order, framed, bounded.
**Independent test**: an agent that never collected exchange N reads it whole.

- [x] T008 [P] [US1] `tests/Parley.Core.Tests/HistoryEnvelopeTests.cs`: `WrapHistory` names the sender as `Wrap` does for each of participant, broker notice, facilitator, person's reply; says history and not mail; fresh token; payload inert (also covers US4).
- [x] T009 [US1] `src/Parley.Core/TrustEnvelope.cs`: `WrapHistory(from, responderModel, payloadJson, token, conversationId, exchangeId, createdUtc)`.
- [x] T010 [P] [US1] `tests/Parley.Store.Tests/AgentHistoryTests.cs`: read in order, `after` continuation, 64,000 bound with at least one message, unknown number, destroyed conversation reads gone, exchange moved reads under its new conversation.
- [x] T011 [US1] `src/Parley.Store/HistoryRepository.cs`: `ReadExchangeAsync`, using `MessageRepository` row types; records the read in the same transaction (`RecordReadAsync`).
- [x] T012 [US1] `src/Parley.Broker/Broker.cs` and `src/Parley.Broker/HistoryRate.cs`: `ReadExchangeAsync(caller, id, after)`; the rate window; envelope each message; no delivery, wake or tick.
- [x] T013 [P] [US1] `tests/Parley.Broker.Tests/AgentHistoryBrokerTests.cs`: reads by a non-participant, reader not added as participant, rate refusal with retry seconds.
- [x] T014 [US1] `src/Parley.Server/AgentsService.cs` and `Wire.cs`: `ReadExchange`; map `Found`.
- [x] T015 [US1] `src/Parley.Mcp/ParleyTools.cs`: `parley_read_exchange` with the description in `contracts/tools.md`; the adapter's unknown-argument refusal covers it; `BrokerLink` call.
- [x] T016 [P] [US1] `tests/Parley.Mcp.Tests/ReadExchangeToolTests.cs` and `tests/Parley.Server.Tests/ReadingAnExchangeTests.cs`: whole path over the loopback socket.

**Checkpoint**: the reported bug is fixed end to end.

## Phase 4: US3 Reading never changes the work (P1)

- [x] T017 [US3] `tests/Parley.Server.Tests/ReadingHistoryChangesNothingTests.cs`: with open, owed, delivered and closed exchanges, read and search everything, then compare every agent's inbox, debts, delivered flags, participants, exchange state and turn data before and after (SC-003). Watch it fail with a `MarkDelivered` in the read path.
- [ ] T018 [US3] Add the mutation checks: confirm the test fails when the read path marks delivered, adds a participant, or wakes.

## Phase 5: US4 What is read is data (P1)

- [x] T019 [US4] `tests/Parley.Core.Tests/HistoryInjectionTests.cs`: a payload imitating the envelope, a header, a fake token and an instruction arrives inside the real delimiters; no field addresses the caller; control characters in a snippet neutralised.
- [x] T020 [US4] `tests/Parley.Server.Tests/AgentsCannotSeeTurnsTests.cs`: extend the walk to the two replies; they carry no turn information.

## Phase 6: US2 Search (P2)

**Independent test**: after a restart an agent finds an old exchange by a phrase.

- [x] T021 [P] [US2] Tests in `AgentHistoryTests.cs`: all-words match, case-insensitive, text in escaped characters found, operators as plain text (`"` `%` `_` `*` `NEAR`), empty and over-long refused, filters by agent, conversation, type, time, paging with `not_shown`, a snippet's bounds.
- [x] T022 [US2] `HistoryRepository.SearchAsync` with `json_tree` and `instr`, newest first, cursor `continue_after`; snippet cut in C# at word-safe bounds.
- [x] T023 [US2] `Broker.SearchHistoryAsync`, `AgentsService.SearchHistory`, `BrokerLink`, `parley_search_history`.
- [x] T024 [P] [US2] `tests/Parley.Mcp.Tests/SearchHistoryToolTests.cs`, `tests/Parley.Server.Tests/SearchingTheHistoryTests.cs`.
- [ ] T025 [US2] A size check: a store of 100,000 messages searches within the goal; recorded in the test as a skipped-on-CI `[Slow]` test.

## Phase 7: The person sees each read (FR-016)

- [x] T026 `HistoryRepository.ReadsOfAsync` and the operator conversation read in `src/Parley.Broker/OperatorConsole.cs` and `OperatorsService.cs` return `reads`; `Parley.Client` view models carry them.
- [x] T027 [P] Web: `src/Parley.Web/Pages/Conversations/Detail.cshtml` (a line under the exchange: "read by x at t"), the agent's page (its searches with words and match counts); `tests/Parley.Web.Tests/HistoryReadsAreShownTests.cs`.
- [x] T028 [P] Terminal: the exchange view shows the lines; `tests/Parley.Terminal.Tests` drawn-screen test.
- [x] T029 Purge: `AgentRepository` purge removes the agent's `history_reads`; test in `AgentPurgeTests`; retiring keeps them.

## Phase 8: US5 Spec, tools and tests agree (P3)

- [x] T030 Rewrite `SurfaceBoundaryTests` to list the two new agent operations and still fail when the surface grows; `AgentsCannotSetTheirOwnLineTests` and the word-match tests stay green; add `tests/Parley.Spec.Tests/HistoryIsOneOperationTests.cs` (the spec's §10.1 rows exist, no sentence saying there is no history operation).
- [x] T031 Sweep every doc so the new rule is the only statement: `README.md`, `docs/*.md`, `plugins/parley/**` prose and skills (how an agent resumes from a number), the `parley_inbox` description's remark about history, `SPEC.md` leftovers (`git grep -n -i "no history"`). Delete superseded wording; no history in the docs.
- [x] T032 Hygiene `git grep` against sibling repository names, tracked and untracked files.

## Phase 9: Polish and release

- [x] T033 `pwsh scripts/test.ps1 -Category Smoke -Build`; fix; the full run `-Category All` before the PR.
- [ ] T034 Review gate (`review/README.md` and the three lens files): three clean runs of each lens on the shipped code. Fix findings and repeat.
- [ ] T035 Commit in meaningful commits; do not push or open a PR until the review is clean and the owner has seen the result.

Not done: T018 (the mutation checks were run by hand: a read marking delivered, and the rate check removed, each failed a test), T025 (the 100,000-message size test; a probe of 20,000 messages searched in about 0.2 s), T034 (the review gate runs in rounds) and T035.

## Dependencies

T001 to T003 first. Phase 2 blocks all. US1 (3) before US3 (4), US4 (5) and US2 (6). Phase 7 needs T011 and T026. Phase 8 last.
Parallel: T004, T007; T008 with T010; T013 with T016; T021 with T024; T027 with T028.

## Strategy

MVP is Phase 1 to 3: the reported bug fixed. Then the guards (4, 5), then search, then what the person sees, then the sweep.
