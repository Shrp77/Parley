---

description: "Task list: a Request is the ask"
---

# Tasks: A Request is the ask

**Input**: Design documents from `specs/016-request-is-the-ask/`
**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/, quickstart.md

**Tests**: included, and not optional here. This repository's build is its lint gate and its suites are how a protocol
change is held to its own document; `Parley.Spec.Tests` executes SPEC.md's own schema fragments.

**Organization**: by user story, after the document and the shared foundation. US1 and US2 are both P1 and are the
two halves of the same wire change; US3 is what keeps the owner's existing store readable.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (different files, no dependency on unfinished work)
- **[Story]**: US1, US2, US3
- Paths are repository-relative

---

## Phase 1: The document first

**Purpose**: SPEC.md is the primary artifact (Principle I). It is rewritten before the code, as 0.5 was, so the code
follows a document rather than the document recording what the code did.

- [X] T001 Rewrite §5.1 in `SPEC.md`: a Request is the ask and its conversation; replace the four-field example with the two-field one from `contracts/payloads.md`, and remove the `intent`, `done_when` and `gate` paragraphs
- [X] T002 Rewrite §5.3 in `SPEC.md`: a Result answers the exchange whole under the item `request`; keep the outcome table, `sources`, and the whole of **Blocked**
- [X] T003 [P] Amend §5.2 in `SPEC.md` to say Clarify is how a receiver settles an ask it cannot scope, including by asking other agents
- [X] T004 [P] Amend §7.2 in `SPEC.md` where it refers to gated items — it never did: §7.2 is about exchanges, and the only gate anywhere was the schema trigger
- [X] T005 Replace rule 8.1 in `SPEC.md` with the one research R5 describes, renumber the rules of §8 contiguously, and update §8's enforcement table
- [X] T006 Update §9.2's `messages` triggers in `SPEC.md` to the shape `data-model.md` gives, and §9.3's invariant list to match
- [X] T007 [P] Update §10.1's `request` and `result` rows in `SPEC.md`
- [X] T008 [P] Update §13 in `SPEC.md` wherever the envelope's wording names an intent or a finish line
- [X] T009 Add §18's `0.7 — a request is the ask` entry to `SPEC.md`: what a second implementation observes, why the removed fields went, and that stored 0.5 and 0.6 messages are kept and still read
- [X] T010 Run `tests/Parley.Spec.Tests` and fix what it reports: section references resolve, tables are well formed, §8's rules are contiguous and all in the table, and every schema fragment executes in order

**Checkpoint**: the document says what the protocol is. Nothing below changes it again.

---

## Phase 2: Foundation — storage, core and the wire

**Purpose**: what every surface depends on. Blocking for US1 and US2.

- [X] T011 Write `src/Parley.Store/Migrations/011_request_is_the_ask.sql` exactly as `data-model.md` gives it: drop and recreate `tr_request_is_well_formed`, touch no row
- [X] T012 Bump `Expected` in `tests/Parley.Store.Tests/MigrationDiscoveryTests.cs` to 11
- [X] T013 [P] Write `tests/Parley.Store.Tests/RequestIsTheAskMigrationTests.cs`: build a schema-10 store from the embedded migrations, write 0.5 and 0.6 traffic into it, migrate, and assert every stored payload is byte-identical, the integrity check passes, and every foreign key resolves
- [X] T014 [P] Extend `tests/Parley.Store.Tests/AdmissionGuardTests.cs` (or a new constraint file) with the malicious cases: a Request with no `ask`, an empty `ask`, a whitespace `ask`, and one carrying `intent`/`items`/`done_when` and no `ask`
- [X] T015 Update `src/Parley.Core/ProtocolVersion.cs` to `0.7`
- [X] T016 [P] Update `src/Parley.Core/PayloadShape.cs`: `ItemIds` keeps reading `items` and `sub_questions` for stored messages, and its remarks say why it is a reader of history rather than of the current shape
- [X] T017 [P] Update `src/Parley.Core/MessageIdentity.cs`, `src/Parley.Core/TrustEnvelope.cs` and `src/Parley.Core/Outcome.cs` where they name the removed fields
- [X] T018 [P] Update `proto/parley.proto`: `OpenRequest.payload_json`'s comment, and any comment naming items or a finish line
- [X] T019 Update `src/Parley.Server/AgentsService.cs` where it validates a Request or a Result against the old shape
- [X] T020 Update the remarks in `src/Parley.Broker/Broker.cs` that describe the old shape (`:183`, `:495-505`); the code itself already answers a Request with no readable items under `request`
- [X] T021 Run `tests/Parley.Store.Tests` and `tests/Parley.Broker.Tests`, updating `tests/Parley.Store.Tests/Work.cs` and `tests/Parley.Broker.Tests/Requests.cs` — the two helpers most suites build their traffic through

**Checkpoint**: a Request with an ask can be stored, and one with the old shape cannot.

---

## Phase 3: US1 — a person asks an agent something (P1)

**Goal**: two fields, and a Result that closes the exchange whole.

**Independent test**: take a seat in the terminal, start a conversation with one sentence, read the Result in the
transcript.

- [X] T022 [US1] Update `src/Parley.Client/Seating/SeatVoice.cs`: a request takes a conversation and an ask; a result takes one outcome, its text, its sources and its blocker
- [X] T023 [P] [US1] Update `src/Parley.Client/Rendering/Framing.cs`: the removed constants go, and what is left is what a composer labels two fields with
- [X] T024 [P] [US1] Update `src/Parley.Client/Rendering/Payload.cs` and `Reading.cs` to render a 0.7 Request as its ask and keep rendering a 0.6 one whole
- [X] T025 [US1] Update the terminal composer in `src/Parley.Terminal/Shell.cs` and `src/Parley.Terminal/Participation.cs` to two fields, and the result form to one outcome
- [X] T026 [P] [US1] Update `src/Parley.Terminal/TranscriptRegion.cs` for both shapes
- [X] T027 [US1] Update the web composer in `src/Parley.Web/Pages/Conversations/Detail.cshtml` and `Detail.cshtml.cs` to two fields, with the same rules stated in the page and applied by the server
- [X] T028 [P] [US1] Update `src/Parley.Web/Pages/Conversations/_Conversation.cshtml` and `src/Parley.Web/Pages/Inbox.cshtml` for both shapes
- [X] T029 [P] [US1] Write `tests/Parley.Client.Tests/AskShapeTests.cs`: `SeatVoice` sends a payload carrying `ask` and nothing else, and a Result carrying one outcome named `request`
- [X] T030 [P] [US1] Update `tests/Parley.Terminal.Tests` composer and transcript tests to the two-field form, including `StartingAConversationTests`
- [X] T031 [P] [US1] Update `tests/Parley.Web.Tests` composer and rendering tests to the two-field form
- [X] T032 [US1] Run the terminal, client and web suites

**Checkpoint**: US1 is deliverable on its own — a person can hold a whole conversation.

---

## Phase 4: US2 — an agent asks another agent (P1)

**Goal**: `parley_request` takes a recipient and an ask; `parley_result` takes one outcome.

**Independent test**: from a repository with the adapter, send a request with two arguments and collect the Result.

- [X] T033 [US2] Rewrite `parley_request` in `src/Parley.Mcp/ParleyTools.cs` to `to`, `ask`, optional `conversationId`, and delete the `RequestItem` record
- [X] T034 [US2] Rewrite `parley_result` in `src/Parley.Mcp/ParleyTools.cs` to one outcome, its text, its sources and its blocker, writing the single-entry `outcomes` array itself; keep answering a stored 0.6 Request's ids, as `contracts/surfaces.md` describes
- [X] T035 [P] [US2] Rewrite the lines in `src/Parley.Mcp/ParleyInstructions.cs` that teach intent, items, kinds and a finish line into the one line research R10 and `contracts/surfaces.md` give
- [X] T036 [P] [US2] Update `tests/Parley.Mcp.Tests/RequestFramingTests.cs` to the new tool surface, and the other adapter suites through their own helpers
- [X] T037 [P] [US2] Write `tests/Parley.Server.Tests/AskOverTheWireTests.cs`: two attached agents, a Request carrying only an ask, a Result closing it whole, over a real loopback socket
- [X] T038 [P] [US2] Write the adversarial probes beside it: a Request with an empty ask, one over the cap, one carrying the old fields, and an adapter announcing 0.6 refused at attach
- [X] T039 [US2] Run the adapter and server suites

**Checkpoint**: both halves of the protocol speak the new shape.

---

## Phase 5: US3 — a conversation written under 0.5 or 0.6 still reads (P2)

**Goal**: nothing stored is lost, and an exchange opened under 0.6 can still be answered.

**Independent test**: open a 0.6 conversation in the terminal, the web console and `parley export`.

- [X] T040 [P] [US3] Write `tests/Parley.Client.Tests/OldShapeRendersTests.cs`: a stored 0.6 Request renders with its intent, its items and its `done_when`, and a 0.6 Result with one line per item
- [X] T041 [P] [US3] Extend `tests/Parley.Terminal.Tests` and `tests/Parley.Web.Tests` with a 0.6 conversation on screen
- [X] T042 [P] [US3] Extend `tests/Parley.Cli.Tests/ExportSafetyTests.cs` with a 0.6 conversation exported
- [X] T043 [US3] Write `tests/Parley.Server.Tests/AnsweringAnOldExchangeTests.cs`: an exchange opened under 0.6, still open after the migration, answered by its ids and closed

**Checkpoint**: the owner's store is safe to migrate.

---

## Phase 6: Every document that teaches the shape

- [X] T044 [P] Update `README.md` wherever it shows a Request or names the removed fields
- [X] T045 [P] Update `plugins/parley/commands/**` and the operator skill for the new shape
- [X] T046 [P] Rewrite CLAUDE.md's **Surveying the peers** instruction as one ask (research R10)
- [X] T047 [P] Update `specs/016-request-is-the-ask/quickstart.md` if anything learned while building it contradicts the walk

---

## Phase 7: Delivery

- [X] T048 Bump `VersionPrefix` in `Directory.Build.props` in the same change (0.29.0: the minor moves on a breaking change)
- [X] T049 Search the repository for `done_when`, `doneWhen`, `intent`, `kind`, `gate` and `RequestItem`, and confirm every remaining occurrence is the migration, SPEC.md §18's history, `PayloadShape`'s reader of stored messages, or the record of this change (SC-004)
- [X] T050 Run every suite from its own host executable, as CLAUDE.md requires, and record the counts
- [X] T051 Walk `quickstart.md` end to end against a throwaway project and a store holding 0.6 traffic, and record the result in `REVIEW-LEDGER.md`
- [X] T052 Run the review gate: three lenses, three consecutive clean runs each, probing rather than reading, logged in `REVIEW-LEDGER.md`
- [X] T053 Write the CLAUDE.md record of what the gate found, and what building it taught that the plan did not know

---

## Dependencies

- **Phase 1 (document)** → everything. The code follows the document.
- **Phase 2 (foundation)** → US1 and US2. Neither can be sent or stored without the migration and the version.
- **US1 and US2** are independent of each other once Phase 2 is done, and can be built in either order or together.
- **US3** depends on Phase 2 only, and is where the migration is proved; it can run in parallel with US1 and US2.
- **Phase 6** depends on the shape being settled (Phases 3 and 4), or the documents will be rewritten twice.
- **Phase 7** is last, and T052 is after the last edit — not alongside it.

## Parallel opportunities

- T003, T004, T007, T008 (different sections of SPEC.md — one writer at a time per section)
- T013, T014 (different test files)
- T016, T017, T018 (different projects)
- T023, T024, T026, T028 (renderers in different projects)
- T029, T030, T031 (different suites)
- T035, T036, T037, T038 (adapter and server, different files)
- T040, T041, T042 (three surfaces, one stored shape)
- T044, T045, T046 (three documents)

## Implementation strategy

**MVP is Phase 1 + Phase 2 + US1**: the document, the migration and the person's composer. That is the report this
came from, end to end, and it is testable on its own.

**Then US2**, which is what makes it the protocol rather than a surface; **then US3**, which must pass before the
owner's store is migrated for real.

**Delivery is not the end**: T052's gate is what this repository means by finished, and T051's walk is what catches
what no suite can.
