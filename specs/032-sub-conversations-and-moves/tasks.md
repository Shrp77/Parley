# Tasks: Sub-conversations and moving exchanges

**Input**: [spec.md](spec.md), [plan.md](plan.md), [data-model.md](data-model.md), [contracts/operations.md](contracts/operations.md)

## Phase 1: The rule first

- [x] T001 Change `SPEC.md`: principle 2.10, rule 9, §5.0, §9.2 (new tables, trigger, comment), §9.3, §9.4, §10.1 (`move_notices`), §10.2 (four rows, `export`, `conversations`), §14, §18 (0.12, schema 6) and the header. One rule stands; the old wording goes.
- [x] T002 Spec change from the plan: FR-007 becomes "the link is recorded before anything is sent".

## Phase 2: Foundation (blocks every story)

- [x] T003 `006_conversation_moves.sql`; update `MigrationDiscoveryTests`; add `MigrationSixTests` (rows kept); `MovingExchangesTests` holds the trigger refusing each case.
- [x] T004 `proto/parley.proto`, Core types in `ConversationMove.cs`, `ConversationSummary.Parent`, `ConversationExport` additions, `IParleyBrokerClient`, `ProtocolVersion`, `Directory.Build.props` 0.52.0.
- [x] T005 `ConversationMoveRepository`: `SpinOffAsync`, `PreviewAsync`, `MoveAsync`, with store tests.

## Phase 3: US1 spin-off (P1)

- [x] T006 `OperatorConsole`, `OperatorsService`, `BrokerClient`: `SpinOff`. Broker test: nothing is sent; repeat succeeds; refusals.
- [x] T007 `ListAsync` and export read links. `ConversationRow.Parent`, list wording on web and terminal (FR-025), with tests that read what is drawn.
- [x] T008 `Parley.Client.Quoting`, with tests.
- [x] T009 Web `Conversations/SpinOff` page, links from each turn and from the panel, links shown in `_Side`. Test: a link cannot fill words or recipients; refused send keeps all; nothing exists before Send.
- [x] T010 Terminal `SpinningOff.cs`, command, `SurfaceTests` row, driven test.

## Phase 4: US2 move (P2)

- [x] T011 `OperatorConsole`, `OperatorsService`, `BrokerClient`: `PreviewMove`, `MoveExchanges`. `SurfaceBoundaryTests`.
- [x] T012 Reconcile participants, void polls, cycles, lengths; `ListAsync` keeps an emptied source; `Pruner` idle rule; destroy deletes notices. Tests for each.
- [x] T013 Export carries moves and `settled_elsewhere`; `Reading` and `Transcript.Write` use them. Transcript notes on web and terminal.
- [x] T014 Web `Conversations/Move` page, tick boxes on the conversation page. Tests: GET changes nothing; a group left out is refused; POST-only binding.
- [x] T015 Terminal `Moving.cs`, command, `SurfaceTests` row, driven test.

## Phase 5: US3 work carries on (P2)

- [x] T016 Notices in `Broker` collection and acknowledge, `InboxHolds`, `AgentsService`, `Wire`, `ParleyTools`; test helpers drain them.
- [x] T017 Over the wire: a move starts no turn; notice at next collection; a Result after a move lands in the destination; a send during a move lands in one place; finished exchanges tell nobody; `BrokerV1Tests` keeps the operations off `Agents`.

## Phase 6: Polish

- [x] T018 CLI refuses `spin-off` and `move`, naming the terminal. `docs/operating.md`, plugin prose, `parley-operator` skill, MCP tool description.
- [x] T019 `CLAUDE.md` state: version, protocol, schema, feature list, migration note for 027 (now 007), the move and spin-off behaviour.
- [ ] T020 Full run, drive the pages in a browser, hygiene check, PR, review gate.
