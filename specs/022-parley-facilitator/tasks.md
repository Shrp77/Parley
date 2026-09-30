# Tasks: Parley as facilitator

**Input**: Design documents from `specs/022-parley-facilitator/`
**Prerequisites**: plan.md, spec.md, research.md (15 findings, R1–R12), data-model.md, contracts/operations.md,
contracts/asking.md, quickstart.md
**Constitution**: **3.0.0 today; 4.0.0 is T001**, amended in its own commit before anything below it. Governance:
"A principle changes in its own amendment, never inside a feature's implementation."
**Branch**: `019-layout`, with 019–021. **One combined review gate covers 019–022** (the owner's call of
2026-09-28); it is the last task here and must not be run before the last edit.

**Tests**: included, and not optional. The feature's promises — *no agent can speak as Parley*, *nothing is asked
twice*, *no agent is restarted mid-turn*, *a person's words are never shown as Parley's* — are each worth only
what the probe of them is worth. **Every guard task names what it must fail against before it is believed.**

## Format: `[ID] [P?] [Story] Description`

- **[P]**: different files, no dependency on anything incomplete — safe to run together
- **[Story]**: the user story it serves (US1–US4, spec.md)
- Paths are repository-relative
- **IDs are stable, not sequential.** `/speckit-analyze` (2026-09-28) added T052–T055, placed where they run, and
  moved T008 and T046 into Phase 2

---

## The six rules this feature is most likely to break

1. **`Parley.Spec.Tests` executes every schema fragment in SPEC.md, in order.** SPEC.md §9.2 and
   `001_initial.sql` move in the same commit or the build goes red.
2. **`SurfaceBoundaryTests`** fixes the public surfaces of `Broker` and `OperatorConsole`, and forbids
   `OperatorConsole` any Request. The facilitator is a third surface, added to that test with its own list.
3. **`ContractSeparationTests`** requires `IParleyBrokerClient` to hold exactly SPEC.md §10.2's operations;
   **`DriftTests`** requires every `BrokerClient` operation on both surfaces; **`EveryActionHasADoorTests`** requires
   a GET description for every web action.
4. **`AgentsCannotSetTheirOwnLineTests`**: no Agents-service name may contain "Ownership" or "Owns", and no MCP
   tool name may contain "own". Every new operation here is an operator operation.
5. **`GraphTests`**: `Parley.Setup` reaches no broker or store; only `Parley.Cli` may join the runtime to
   `OwnershipLine`.
6. **`OwnershipRefusalsTests`** scans the ownership sources for four things that must never be said; new
   ownership text joins its list.

---

## Phase 1: Setup — the documents first (blocks everything)

- [X] T001 Amend `.specify/memory/constitution.md` to **4.0.0 (MAJOR)** in its own commit, with a Sync Impact
  Report: Principle II names the facilitator as a second asymmetry beside the operator — neither agent nor
  person, may ask, reply with a person's words and agree its own conversation done, and nothing else;
  Principle III names the one identity not fixed by a launch (the facilitator's, fixed by the broker for its own
  store and unclaimable by any launch or call); Principle IV records Auto Accept as a further reduction in the
  ownership control, in the words it already uses for 020's removed bound, with what replaces it (provenance and
  review). Make the same change to CLAUDE.md's Standing constraints in the same commit — Governance: "an
  amendment here that is not also made there is a defect". Cite research.md findings 1–2
- [X] T052 Re-run plan.md's Constitution Check against the amended 4.0.0 text and record the result in plan.md
  (Governance: "the feature's plan then checks against the amended text"). Before T002
- [X] T002 Amend SPEC.md §3 (Terminology) and principle 2.10 to name the facilitator as a third kind of
  participant and a third starter of a conversation, and §5's "The broker writes no message on anyone's behalf"
  and §9.3's "the broker writes nothing else" to state the facilitator's two attributions (`parley:facilitator`,
  `parley:person`) as the other exceptions; and state in §5 that an exchange the facilitator opened never
  chooses the conversation an agent's own Request is assigned to (research R13), in SPEC.md
- [X] T003 Amend SPEC.md §10.1's "which it is not" paragraph to say what the facilitator is and why §10.1 still
  gains no operation; add the five §10.2 rows from contracts/operations.md (`facilitation()`,
  `set_auto_accept(on)`, `ask_again(agent)`, `reply_to_asked_back(agent, text)`, `discard_answer(agent)`) and
  the two new `registry()` fields, in SPEC.md
- [X] T004 Amend SPEC.md §11.1's "Three things end a resident agent, and nothing else may" to add the node told
  to refresh an agent whose first ownership line was just accepted automatically, idle, once, and §11.3's
  reserved-namespace table to add `parley:facilitator` and `parley:person`, in SPEC.md
- [X] T005 Amend SPEC.md §12.3 ("only the agent can make it") for the one row the broker writes itself, and §14
  to say the facilitator is never retired, routed to, or listed by `agents()`, in SPEC.md
- [X] T006 Amend SPEC.md §17 question 2 (a seat is a person; the facilitator is not a seat) and **§17 question
  10 for 020 and 022 together** — 020's owed amendment (asked, then accepted; research finding 3) and Auto
  Accept, recorded as a reduction in a control in the §9.2 `agents.owns` comment's style — in SPEC.md
- [X] T007 Add a second `#### Amended` entry under §18's 0.7 (not a new version; research R12), and correct
  L3992's `accept_ownership` to `record_ownership` (research finding 4), in SPEC.md

## Phase 2: Foundational — storage, core, the facilitator's identity (blocks every story)

**Checkpoint**: the facilitator exists in every store, cannot be claimed, and is on no roster.

- [X] T009 [P] Write `tests/Parley.Store.Tests/FacilitatorRowTests.cs` first: a row with role
  `parley-facilitator` and any other adapter is refused; adapter `parley-facilitator` under any other role is
  refused; the facilitator cannot be retired; a row with role `parley` and an ordinary adapter is **admitted**
  (this repository's own agent). **Must fail against the schema as it stands**, where the first three are
  admitted
- [X] T046 [P] Write `tests/Parley.Store.Tests/OwnershipCascadeTests.cs` first (research finding 14): destroy an
  `owns-` conversation that produced an accepted line and holds a question, and purge the agent that was asked —
  both commit, the questions go with the conversation, and the accepted line stays with its link cleared. **Must
  fail against the schema as it stands**
- [X] T010 Add to `src/Parley.Store/Migrations/001_initial.sql` in place (research R5): the two facilitator
  triggers (role `parley-facilitator` ⇔ adapter `parley-facilitator`) and the no-retire rule on `agents`; table
  `ownership_questions` with its state CHECK, the partial unique index (one live question per agent), the
  "accepted only with a line" trigger, and `conversation_id` and `exchange_id` `ON DELETE CASCADE`; table
  `facilitation` (one row, `auto_accept` default 1); `source` admitting `automatic` with `proposed`'s
  cross-checks; `agent_ownership_accepted.conversation_id` `ON DELETE SET NULL`, with the conversation
  requirement moved from a CHECK to a BEFORE INSERT trigger so the SET NULL is not refused (research finding 14).
  Makes T009, T011 and T046 pass
- [X] T008 Write SPEC.md §9.2's schema fragments for data-model.md (the facilitator's triggers,
  `ownership_questions`, `facilitation`, `source` admitting `automatic`) **in T010's commit**, and run
  `tests/Parley.Spec.Tests` — it executes them
- [X] T011 [P] Write `tests/Parley.Store.Tests/OwnershipQuestionTests.cs`: a second live question for one agent is
  refused by the index; `waiting` without `waiting_because` is refused; `accepted` without an accepted line is
  refused; `automatic` without a conversation is refused. **Each must fail with its rule removed**
- [X] T012 [P] Add `OwnershipSource.Automatic` to `src/Parley.Core/OwnershipSource.cs`, `OWNERSHIP_SOURCE_AUTOMATIC`
  to `proto/parley.proto`, and the mappings in `src/Parley.Server/Wire.cs`, `src/Parley.Client/BrokerClient.cs`
  and `src/Parley.Store/AgentRepository.cs` (`Recorded`)
- [X] T013 [P] Add the reserved-role rule to `src/Parley.Core/Facilitation.cs` (new): the role
  `parley-facilitator`, the adapter `parley-facilitator`, the display name **Parley**, the attributions
  `parley:facilitator` and `parley:person`, records for a question and its state, and the two question texts of
  contracts/asking.md — here because the broker sends them and cannot reference `Parley.Client` (research R3).
  No I/O (Core's rule)
- [X] T014 Write the facilitator's row when a store is opened, if absent, in `src/Parley.Store/AgentRepository.cs`
  and `src/Parley.Server/BrokerRuntime.cs` (the constructor, beside `_house.ServeFrom`), named
  `parley-facilitator@<MachineName.Current>`, and found by its adapter rather than its name so a renamed machine
  does not make a second row
- [X] T015 Refuse the reserved role at attach in `src/Parley.Server/AgentsService.cs` and at seating in
  `src/Parley.Client/Seating/SeatBook.cs`, and at enrolment in `src/Parley.Setup/EnrollmentPlan.cs` (which offers
  another role), each with a sentence, before storage refuses it
- [X] T016 [P] Write `tests/Parley.Server.Tests/NobodyIsParleyTests.cs`: attach as `parley-facilitator@x` refused;
  seat as `parley-facilitator` refused; an adapter sending with attribution `parley:facilitator` or
  `parley:person` refused (`Broker.RequireAdapterResponder` already refuses the prefix — assert it covers both);
  a Request addressed to the facilitator, and a Clarify on an exchange it did not open, refused; enrolling a
  folder named `Parley` still enrols `parley`. **Must fail against T015 and T053 reverted**
- [X] T017 Omit the facilitator from `agents()` (`ListRoutableAsync` in `src/Parley.Store/AgentRepository.cs`) and
  return `facilitator` and `owns_source` on every `registry()` row (`GetAllAsync`, `proto/parley.proto`,
  `src/Parley.Server/Wire.cs`, `src/Parley.Core/AgentRecord.cs`, `src/Parley.Client/BrokerClient.cs`)
- [X] T053 Refuse the facilitator as a recipient in `Broker` admission, `src/Parley.Broker/Broker.cs`: a Request
  addressed to it and a Clarify on an exchange it did not open, each with a sentence — omitting it from the roster
  does not stop an agent naming it (FR-003)
- [X] T018 [P] Write `tests/Parley.Server.Tests/ParleyIsOnNoRosterTests.cs`: an agent's `agents()` never lists
  the facilitator; `registry()` lists it flagged. **Must fail with T017's filter removed**
  *(Done inside `NobodyIsParleyTests.No_agent_finds_the_facilitator_on_its_roster`, mutation-checked there.)*
- [X] T019 Refuse the facilitator to every operator operation on agents — retire, reinstate, purge, instruct,
  start — in `src/Parley.Broker/OperatorConsole.cs`, with a probe per operation in
  `tests/Parley.Broker.Tests/ParleyCannotBeWithdrawnTests.cs`
  *(The refusals live in `OperatorsService`, so the probe is `tests/Parley.Server.Tests/ParleyCannotBeWithdrawnTests.cs`, over the wire.)*
- [X] T054 [P] Write `tests/Parley.Broker.Tests/ParleyIsNotThePersonSeatedTests.cs`: with the facilitator present
  and no seat held, a question that needs a person goes back to its originator and never to the facilitator
  (principle 2.6, FR-004, SC-005). **Must fail against a seat check that admits the facilitator's adapter**

## Phase 3: User Story 1 — every new agent is asked, with nobody asking (P1) 🎯 MVP

**Goal**: an attached agent with no line is asked once; with Auto Accept on, its answer becomes its line and the
agent is refreshed when idle.
**Independent test**: quickstart.md §1–§3.

- [X] T020 [P] [US1] Write `tests/Parley.Broker.Tests/FacilitatorAsksTests.cs` first: an attached agent with no
  line and no question is asked; one with a line, a seat, a retired agent and the facilitator are not; an agent
  with any live question is not asked again. **Must fail before T021 exists**
- [X] T021 [US1] Create `src/Parley.Broker/Facilitator.cs`: `DueAsync` (who needs asking — attached now, research
  R4), `AskAsync` (open the Request in an `owns-<role>-<stamp>` conversation, attribution `parley:facilitator`,
  written through the repositories as the `closed` path is, and the `ownership_questions` row, in one
  transaction), `AnsweredAsync` (questions whose exchange the agent has answered or clarified), `SettleAsync`,
  `AgreeDoneAsync` (propose closing its conversation and vote done once its question is settled — nobody else has
  a reason to propose it; FR-003, research finding 8)
- [X] T055 [US1] Leave exchanges the facilitator opened out of `AssignAsync` in `src/Parley.Broker/Broker.cs`
  (research R13), with `tests/Parley.Broker.Tests/ParleyChoosesNoConversationTests.cs`: an agent holding the
  facilitator's question in `asked_back` sends a Request naming no conversation, and it is assigned exactly as it
  would be without the question. **Must fail without the filter**
- [X] T022 [US1] Add the `Facilitator` surface to `tests/Parley.Broker.Tests/SurfaceBoundaryTests.cs` as a third
  fixed list, and assert `Broker` and `OperatorConsole` gained nothing
- [X] T023 [US1] Read the two question texts from `src/Parley.Core/Facilitation.cs` (T013) in
  `src/Parley.Client/OwnershipAsk.cs`, chosen by the switch, and change recognition to "the facilitator's own Request" rather than the `owns-` prefix
  (research finding 11); extend `tests/Parley.Client.Tests/AskingWhatAnAgentOwnsTests.cs` — an agent's own
  `owns-` conversation is not a question. **Must fail against prefix recognition**
  *(The recognition test is in `TheProposalIsTheLatestThingSaidTests.An_agents_own_owns_conversation_is_not_a_question`, mutation-checked.)*
- [X] T024 [US1] Add `OwnershipStatement.Flatten` to `src/Parley.Core/OwnershipStatement.cs` (collapse runs of
  whitespace including line and paragraph separators to one space, trim; nothing else — research R7), with
  `tests/Parley.Core.Tests/FlatteningChangesNoWordTests.cs`: a multi-paragraph answer becomes a valid line with
  the same words; a format character still refuses
- [X] T025 [US1] Add `FacilitateAsync` to `src/Parley.Server/BrokerRuntime.cs`: under the gate, ask everyone due
  and wake each receiver as `AgentsService.Request` does (`NudgeAsync` + `RequestAttentionForMail`); return the
  questions answered since the last pass
- [X] T026 [US1] Add the per-project facilitation loop to `src/Parley.Cli/ServeCommand.cs` beside the sweep
  (`hosted.Tasks.Add(ContainAsync(...))`, `hosted.Stopping.Token`): each tick, `FacilitateAsync`; for each answer,
  flatten it; if Auto Accept is on, the line is valid and `OwnershipLine.NotHere` passes, write it with
  `OwnershipLine.WriteAsync` **then** record it as `automatic` (file first, 020's order); otherwise mark it
  `waiting` with the reason
- [X] T027 [P] [US1] Add `WhenIdleAsync` to `src/Parley.Headless/ResidentAgent.cs` (waits on the turn lock without
  taking a turn) and `RefreshAgent` to `proto/parley.proto`'s `NodeCommand` and to
  `src/Parley.Server/NodesService.cs` (`JoinedNode.RequestRefresh`, true only for a role the node announced)
- [X] T028 [US1] Handle `RefreshAgent` in `src/Parley.Cli/NodeCommand.cs`: only a resident this node started;
  `WhenIdleAsync`, then stop and start it with its launch configuration read afresh; otherwise say so and do
  nothing
- [X] T029 [US1] Send `RefreshAgent` from the facilitation loop only for an agent's **first** automatic line, only
  when it owes no open exchange and has no attention pending, and only once (`restart_sent`), in
  `src/Parley.Server/BrokerRuntime.cs`
- [X] T030 [P] [US1] Write `tests/Parley.Cli.Tests/RefreshWaitsForTheTurnTests.cs`: a refresh sent while a turn
  holds the lock stops the agent only after the turn ends; a second automatic line sends no refresh; an agent the
  node did not start is not refreshed. **Must fail against a refresh that ignores the lock**
  *(The turn-lock half is `tests/Parley.Headless.Tests/RefreshWaitsForTheTurnTests.cs`, where a stand-in process can hold a turn; the first-line-only rule is in `FacilitatorAsksTests`; the node's own refusal is `tests/Parley.Cli.Tests/NodeRefreshTests.cs`. `WhenIdleAsync` became `ResidentAgent.ShutdownWhenIdleAsync`, which holds the turn across the stop.)*
- [X] T031 [US1] Write `tests/Parley.Server.Tests/NothingIsAskedTwiceTests.cs` over a real socket: restart the
  agent, restart the broker, attach the agent from two sessions — exactly one question each time. **Must fail
  with the partial unique index dropped**
  *(Honest note: two checks in code sit in front of the index, so dropping the index alone does not fail this test. The index's own refusal is `OwnershipQuestionTests`, mutation-checked.)*
- [X] T032 [US1] Write `tests/Parley.Cli.Tests/AskedWithNobodyAskingTests.cs`: a served project with a scripted
  agent that answers in paragraphs; with no surface open the line is accepted as `automatic`, the answer's words
  are unchanged but for whitespace, and the recorded length is the agent's raw message. Covers FR-006, FR-011,
  FR-013, FR-014

## Phase 4: User Story 2 — a person can see and correct what every agent owns (P1)

**Goal**: one review, on both surfaces, of every line and where it came from, with accept, edit, write, discard,
reply and ask again — no seat needed.
**Independent test**: quickstart.md §4–§5.

- [X] T033 [US2] Add the §10.2 operations to `src/Parley.Core/IParleyBrokerClient.cs`, `src/Parley.Broker/OperatorConsole.cs`,
  `proto/parley.proto` (Operators), `src/Parley.Server/OperatorsService.cs` and `src/Parley.Client/BrokerClient.cs`:
  `facilitation()`, `ask_again(agent)`, `reply_to_asked_back(agent, text)`, `discard_answer(agent)` (the switch
  is US3). `record_ownership` refuses `automatic` from an operator call
  *(The operations act through `Facilitator`, reached by `BrokerRuntime.UseFacilitatorAsync`, not through `OperatorConsole`: that type may not send a Request, and asking again is one. `record_ownership` refuses `automatic` in `OperatorConsole`, and a person's recorded line settles Parley's waiting question in `OperatorsService`.)*
- [X] T034 [US2] `reply_to_asked_back` sends on the facilitator's exchange with attribution `parley:person`, held
  to the message cap, in `src/Parley.Broker/Facilitator.cs`; and the transcript renders `parley:person` as a
  person's words sent through Parley in `src/Parley.Client/Rendering/` (both surfaces read it)
- [X] T035 [P] [US2] Write `tests/Parley.Broker.Tests/APersonsWordsAreNeverParleysTests.cs`: a reply from the
  review is stored and rendered as `parley:person`. **Must fail against a reply attributed `parley:facilitator`**
- [X] T036 [US2] Create the terminal ownership review, `src/Parley.Terminal/OwnershipReview.cs`: every agent, its
  accepted line and the line peers read now, provenance (`automatic` visibly distinct — FR-018), and any question
  and its state; actions accept, edit, write, discard, reply, ask again; offered on the agents pane as
  **Review ownership** (`v`) in `src/Parley.Terminal/Shell.cs`
  *(Provenance wording is `Parley.Client.Provenance`, read by both surfaces.)*
- [X] T037 [US2] Change 020's `k` and `e` in `src/Parley.Terminal/Shell.cs` and `src/Parley.Terminal/Ownership.cs`
  to **Ask again** through `ask_again`, needing no seat, and remove the seat-required refusal the owner met
- [X] T038 [US2] Add the same review to the web UI: `src/Parley.Web/Pages/Projects/Ownership.cshtml(.cs)`, actions
  on `src/Parley.Web/Pages/Projects/Act.cshtml.cs`, each described on a GET before a POST performs it
  *(`Act`'s description became `OnGetAsync`, because accepting an answer shows it; `EveryActionHasADoorTests` follows. The new actions are `acceptanswer`, `discardanswer`, `replyaskedback` and `autoaccept`; `askownership` and `askeveryownership` now go through Parley with no seat.)*
- [X] T039 [P] [US2] Extend `tests/Parley.Terminal.Tests/` and `tests/Parley.Web.Tests/` for the review: provenance
  shown, `automatic` distinct, each action reaching its operation; and confirm `DriftTests`,
  `EveryActionHasADoorTests` and `ContractSeparationTests` pass with the new operations bound on both surfaces

## Phase 5: User Story 3 — with Auto Accept off, answers wait for a person (P2)

**Goal**: the per-project switch on both surfaces; answers wait; accepting, editing and discarding work.
**Independent test**: quickstart.md §4.

- [X] T040 [US3] Add `set_auto_accept(on)` through the same five layers as T033, reading and writing the
  `facilitation` row; a store without the row reads as on (FR-010)
- [X] T041 [US3] Offer the switch on the terminal's projects pane, **Auto Accept on/off** (`a` — free there; `o` is
  Instruct on the agents pane), showing its state, under the Project menu, in `src/Parley.Terminal/Shell.cs`,
  and on the web project page with a GET description; `NoTwoActionsShareAKeyTests` passes
  *(Terminal key `a` on the projects pane, under the Project menu, in `AutoAccepting.cs`. Web: the ownership page's own link.)*
- [X] T042 [P] [US3] Write `tests/Parley.Broker.Tests/AnswersWaitWhenTheSwitchIsOffTests.cs`: off, an answer waits
  and nothing is written; accepted unchanged it is `proposed`; edited it is `edited`; discarded, nothing is
  written; turning it on leaves waiting answers waiting (FR-015); discarded with no line, the agent is **not**
  asked on the next tick (FR-008, research R4). **Must fail against a loop that ignores the switch, and against
  R4's old rule**
  *(The loop's half is `AskedWithNobodyAskingTests.With_Auto_Accept_off_the_answer_waits_and_nothing_is_written`; the store half is in `APersonsWordsAreNeverParleysTests`.)*

## Phase 6: User Story 4 — the facilitator is visibly not an agent and not a person (P2)

**Goal**: shown as Parley everywhere; offered for no action on agents.
**Independent test**: quickstart.md §6.

- [X] T043 [P] [US4] Show the facilitator as **Parley**, apart from agents and seats and offered for nothing, in
  `src/Parley.Terminal/AgentsRegion.cs` (its own heading), the room column, and the web agents list
  *(Parley is filtered out once at `AgentsRegion.Show`'s input, so every row, standing and recipient list loses it together; the room column names it and never addresses it.)*
- [X] T044 [P] [US4] Render `parley:facilitator` messages as Parley's in transcripts on both surfaces and in the
  export, extending `src/Parley.Core/TrustEnvelope.cs`'s treatment of the broker's own notices so an agent reading
  its inbox knows the question is from Parley, not a peer
  *(`Parley.Client.Rendering.Speaker` names who spoke for every transcript and the export; `TrustEnvelope` names Parley's question and a person's reply for the agent.)*
- [X] T045 [P] [US4] Write `tests/Parley.Terminal.Tests/ParleyIsShownApartTests.cs` and the web equivalent: Parley
  is not in the agents group or the seats group, and no action is offered on its row. **Must fail against T043
  reverted**

## Phase 7: Polish and cross-cutting

- [X] T047 [P] Add every new ownership source to `tests/Parley.Cli.Tests/OwnershipRefusalsTests.cs`'s scanned list
- [X] T048 [P] Update README.md, `plugins/parley/commands/setup.md` and `plugins/parley/skills/parley-operator/SKILL.md`:
  agents are asked by Parley, Auto Accept, the ownership review; remove "press `k` to ask" as the way a line is
  obtained
- [X] T049 Bump `VersionPrefix` in `Directory.Build.props` to **0.35.0** (a feature's minor, as 019–021 each took)
  and record 022 in CLAUDE.md's plan section
  *(Recorded in CLAUDE.md as the latest plan.)*
- [X] T050 Walk `quickstart.md` against packed tools in a scratch root with every `PARLEY_` variable cleared,
  recording each check, as 019's and 020's walks were
  *(Walked on 2026-09-28 with `walk.ps1` beside the quickstart: every check passed against tools packed at 0.35.0, with the agents played over `parley-mcp`'s own stdio. Two things recorded rather than checked: the refresh, because a walked agent is not started by a node; and §3 as "attached, never asked", because no store holds agents from before 022.)*
- [ ] T051 **Run the combined review gate for 019, 020, 021 and 022** per CLAUDE.md — three consecutive clean runs
  per lens, logged in `REVIEW-LEDGER.md` including the clean runs. Not before the last edit. This supersedes
  020's T064 and 019's T110, which were deferred into it

---

## Dependencies

- **T001 before everything.** The constitution changes in its own commit.
- **T052, then T002–T007, before any code.** SPEC.md is the product (Principle I). T008 lands in T010's commit,
  after T009, T011 and T046 have been watched to fail against the schema as it stands.
- **Phase 2 before every story.** Every story needs the facilitator's row, its refusals and the roster omission.
- **US1 before US2's "ask again"** (T037 calls `ask_again`, which reuses T021's `AskAsync`).
- **US3 depends on US1's loop** (the switch changes what T026 does). **US4 depends only on Phase 2.**
- **T051 is last**, after every edit.

## Parallel opportunities

- Phase 2: T009, T011, T046, T012, T013 together; then T016 and T018 once T015, T017 and T053 exist; T054 any
  time after T014.
- US1: T020, T024 and T027 together; T030 once T028 exists.
- US2: T035 and T039 alongside T036–T038.
- US4: T043, T044, T045 together, independent of US2 and US3.
- Polish: T047, T048 together.

## Implementation strategy

**MVP is Phase 1, Phase 2 and US1**: the documents amended, the facilitator unclaimable and off every roster,
and every attached agent asked once and accepted automatically. That alone answers the owner's request — "Parley
should ask every agent what they are responsible for. It should not be an explicit call from the user." US2 makes
it safe to leave on (D3), US3 gives the off switch, US4 finishes how it looks. The gate runs once, at the end,
over all four features.
