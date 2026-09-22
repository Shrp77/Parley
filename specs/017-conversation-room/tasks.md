# Tasks: A conversation is a room

**Spec**: [spec.md](./spec.md) · **Plan**: [plan.md](./plan.md) · **Research**: [research.md](./research.md)

Built around the existing transcript rather than in place of it (research R11), so every guard the transcript
carries keeps passing by construction. Each phase leaves the suite green and is committed.

## Phase 1: The derivation, headless (US1, US2)

The feature's core, fully testable before any layout exists.

- [X] T001 Read `BrokerClient.WaitingAsync`'s reply shape and `ConversationExport` in `src/Parley.Client`, and confirm the join in research R1 is expressible from what both return
- [X] T002 Write `src/Parley.Client/Seating/Pairing.cs`: the pair state per participant, joining the export's exchange snapshots with the waiting edges on exchange identifier
- [X] T003 Write `src/Parley.Client/Seating/Composing.cs`: `Act`, the derivation from pair state, `Draft`, and `Cost`
- [X] T004 [P] Write `tests/Parley.Client.Tests/PairingTests.cs`: the three derivable states, an exchange between two other participants producing none, and a closed exchange producing none
- [X] T005 [P] Write `tests/Parley.Client.Tests/ComposingTests.cs`: each act; one draft to three recipients giving three acts; the answer-or-ask-back pair offering both and defaulting to answer; the cost arithmetic
- [X] T006 Extend `SeatVoice` with one send that performs a draft — a Result, a Clarify or a Request per recipient — reporting per recipient as `RequestEveryoneAsync` already does
- [ ] T007 [P] Write `tests/Parley.Client.Tests/SendingADraftTests.cs`: the payload carries the text and no routing; N recipients is N sends; a partial failure names who got it
- [X] T008 Run the client suite

## Phase 2: The room's layout (US1)

- [X] T009 Write `src/Parley.Terminal/RoomRegion.cs`: participants in their own colours, outstanding items, agent status
- [X] T010 Make the Conversation view three panes in `Shell.cs` — room, transcript, line — with the transcript unchanged
- [X] T011 Sort the conversation-view tests per research R10 and record each file's pile in this file before changing any of them
- [X] T012 Update the tests that assert the arrangement: the layout ladder, the narrow-terminal rule, resize, and which panes exist
- [X] T013 [P] Write `tests/Parley.Terminal.Tests/RoomTests.cs`: two columns at several widths, participants listed with colour, and every fact readable with colour stripped
- [X] T014 Run the terminal suite

## Phase 3: The line (US1, US2)

- [X] T015 Write `src/Parley.Terminal/LineRegion.cs`: the input line and the preview block
- [X] T016 Wire Enter to the active control, Tab to the answer-or-ask-back alternative, and the send through `SeatVoice`
- [X] T017 Re-check the seat at the send, not when the line was focused
- [X] T018 [P] Write `tests/Parley.Terminal.Tests/LineTests.cs`: the preview names every recipient and its act; Enter sends; the seat going mid-draft refuses and keeps the text; over the cap refuses and keeps the text
- [X] T019 Run the terminal and client suites

## Phase 4: Completion and commands (US4)

- [X] T020 Write `src/Parley.Client/Seating/Completion.cs`: one narrowing list over a candidate set, derived from the text
- [X] T021 Write `src/Parley.Client/Seating/Commands.cs`: the declaration, the table, and `/add`
- [X] T022 [P] Write `tests/Parley.Client.Tests/CompletionTests.cs`: narrowing, closing when nothing matches, reopening on delete, prefix versus exact
- [X] T023 [P] Write `tests/Parley.Client.Tests/CommandTableTests.cs`: declarations parsed; a draft command not sendable alone; a missing parameter refused; an unrecognised identifier is text; `/etc/hosts` is text
- [X] T024 Draw the completion list in `LineRegion`, over both sigils, with up/down and Tab
- [X] T025 Resolve `@` into recipients and `/add` into a widened recipient set, with nothing reaching the payload
- [X] T026 The near-miss note: identifier unresolved but a prefix of one that would resolve
- [X] T027 Run the terminal and client suites

## Phase 5: Outstanding items and context (US3)

- [ ] T028 Carry the message identifier on each transcript line in `Reading.cs`
- [ ] T029 Give `TranscriptRegion` a way to put a message on screen with its neighbours visible
- [ ] T030 Fill the room's outstanding list from the same pair state the acts come from, plus closure polls
- [ ] T031 [P] Write the tests: an item per outstanding thing; activating one moves the transcript to it; nothing outstanding says so
- [ ] T032 Run the terminal suite

## Phase 6: Delivery

- [X] T033 Bump `VersionPrefix` to 0.30.0 — a new surface, and the minor moves on a breaking change while the major is zero
- [X] T034 Hygiene grep for the originating project's names over everything added
- [X] T035 Run every suite from its own host executable and record the counts
- [ ] T036 Install with `-Force` (the owner's instruction, given while this was being planned)
- [ ] T037 Write the morning report: what is built, what is tested, what is outstanding, and every decision taken without asking

## The test sort (T011)

Filled in as it is done, so the next reader can tell a file that was considered from one that was missed.

| file | pile | why |
|---|---|---|
| CopyingTests, SeatUnderneathTests | guard, route updated | copy offered without a seat, and a released seat speaking: both reached through `Focus(0)`, which silently became the room. They name the transcript now. |
| ViewRaceTests, EmptyDeploymentTests | description of the arrangement | hard-coded pane counts, 1 for a conversation and 4 across the views. Updated to 2 and 5. |
| ColumnZeroTests, VerbatimTests, WrappingTests, ReflowTests, InjectionTests, ReadableSurfaceTests, RefillRaceTests, PaintedTests, ColourTests, OldShapeOnScreenTests | guard, untouched | every one is a property of the transcript, which this change did not touch. They pass by construction, which is the whole reason the room was built around it (research R11). |
| DeadSurfaceTests | guard, satisfied by the new tests | it reported `RoomRegion.NoSeat`, `NothingOwed` and `ToAddress` as named only in their own file. Writing `RoomTests` is what makes them alive rather than making them private. |
| the remaining 40 files | untouched | projects, naming, enrolment, files, withdrawal, following, status line, layout ladder, narrow terminal, resize. None asserts the conversation view's arrangement. |
