# Tasks: The person always has a seat

**Input**: Design documents from `specs/023-always-a-seat/`
**Prerequisites**: plan.md, spec.md, research.md (R1–R11), data-model.md, contracts/person-seat.md, quickstart.md
**Constitution**: **4.0.0 today; 5.0.0 is T001**, amended in its own commit before anything below it. Governance:
"A principle changes in its own amendment, never inside a feature's implementation."
**Branch**: `019-layout`. Whether 023 joins the combined 019–022 review gate is the owner's call (T060).

**Tests**: included, and not optional. *No agent can be enrolled, attached as an agent, or stored as the person*,
*the person is never told unreachable* and *no surface names the seat for itself* are each worth only what the
probe of them is worth. What the reservation does **not** stop is recorded in the spec, and T049 probes that too. **Every
guard task names what it must fail against before it is believed.**

## Format: `[ID] [P?] [Story] Description`

- **[P]**: different files, no dependency on anything incomplete — safe to run together
- **[Story]**: the user story it serves (US1–US5, spec.md)
- Paths are repository-relative

---

## The rules this feature is most likely to break

1. **`Parley.Spec.Tests` executes every schema fragment in SPEC.md, in order.** SPEC.md §9.2 and
   `001_initial.sql` move in the same commit or the build goes red.
2. **`SurfaceTests`** allows `Reinstate` in exactly one terminal file, which must carry the typed-name prompt.
   Removing the retired-seat path (`Withdrawing.ReinstateSeatAsync`) must leave that rule true, not delete it.
3. **`EveryActionHasADoorTests`** and **`DeadSurfaceTests`** read the surfaces' sources. Removing `/Join`,
   `/Leave` and the terminal's join actions removes arms both of them count.
4. **`GraphTests`**: nothing new may give `Parley.Client`, `Parley.Web` or `Parley.Terminal` a route to a store.
5. **`SurfaceBoundaryTests`**: `Broker`'s public surface does not grow. Nothing here needs it to.
6. **The storage rule restricting `parley-console` to `human-user` breaks every typed-name seat.** It lands in
   Phase 7 (US4), after Phase 4 (US2) has moved both surfaces and every fixture off typed names. Landing it
   earlier turns ~53 test files red at once and hides which ones matter.
7. **The facilitator is renamed in this feature** (`parley-facilitator@<project>`, spec D7). 022's tests that
   build it with `EnsureFacilitatorAsync("test")` or `Facilitation.On("test")` keep working only because `test` is
   a valid project name; any that assert the machine half, or a `nodes` row for it, must change.
8. **`AgentName.Machine` reads the string**, so for the two broker-provided participants it returns the project.
   Nothing may treat that as a machine (T013a).

---

## Phase 1: Setup — the documents first (blocks everything)

- [X] T001 Amend `.specify/memory/constitution.md` to **5.0.0 (MAJOR)** in its own commit, with a Sync Impact
  Report: Principle III names the person seat as a second identity the broker fixes rather than a launch, beside
  the facilitator — `human-user@<project>`, reserved in storage in both directions, spoken as by every
  surface that reaches the broker, which §17 question 2 already concedes of any seat. Both broker-provided
  identities are named for their project and have no machine. Make the matching change to
  CLAUDE.md's seat paragraphs in the same commit (replace "a seat must not outlive the person" with "a seat
  outlives the person's presence; the claim that they are present does not"). Cite research.md R9
- [X] T002 Re-run plan.md's Constitution Check against 5.0.0 and record the result in
  `specs/023-always-a-seat/plan.md`. Before T003
- [X] T003 Amend SPEC.md principle 2.6, §5 *Blocked* and §8 rule 10: "the person seated" means the project's
  person seat whether or not the person is here; the hand-it-back branch applies only to an implementation that
  provides no person seat; waiting for the person's decision is correct, in `SPEC.md`
- [X] T004 Amend SPEC.md §3 (Terminology: *Person*, and a *Person seat* row beside *Facilitator*), §4.1.1 (for the
  two broker-provided participants the part after `@` is the project and they have no machine), §12.3 (a second
  broker-written row, reserved role `human-user`; the facilitator renamed `parley-facilitator@<project>`), §10.1 `agents()` (lists the person with here/away) and §10.2
  (retire, reinstate, purge, start, instruct and ask_again refuse the person), in `SPEC.md`
- [X] T005 Amend SPEC.md §4.0 and §14: for the person, a connection is evidence of presence, not of reachability;
  §14's table gains `here` and `away`; a message to the person is `with the person`, never unreachable; §5's wake
  outcomes list gains it, in `SPEC.md`
- [X] T006 Amend SPEC.md §17 question 2: close the open problem that a seat "makes `startable` read oddly"; record
  having a seat versus being in it, the owner's answer A, and that everyone using one project is one `human-user`
  (spec D4); keep the `human:` attestation question open, now noting that the reserved role would let a broker
  refuse `human:` from anything but the person seat, in `SPEC.md`
- [X] T007 Add to SPEC.md §9.2 as schema fragments **only what lands in Phase 2**: the `agents.machine` column and
  its two checks, the check that the two broker-provided participants have no `owns` line, the
  `tr_agent_declares_its_machine` guard, `tr_person_is_never_retired`, `tr_person_is_never_purged` and
  `ux_one_person`. **Not** the role-to-adapter pair, which T045 adds to SPEC.md and
  the schema together (`/speckit-analyze` issue 4). Record in §18 a further amendment to 0.7 (unreleased): two
  presence values, one wake outcome, and the two renamed participants, in `SPEC.md`

**Checkpoint**: `Parley.Spec.Tests` is red until T010 lands the same DDL. T007 and T010 go in one commit.

---

## Phase 2: Foundational — the seat exists (blocks every story)

- [X] T008 [P] Change `Facilitation.On` to take a `ProjectName`, in `src/Parley.Core/Facilitation.cs`. Add
  `PersonSeat` (Role `human-user`, Adapter `parley-console`, `On(ProjectName)`, `Is(AgentName)`,
  `IsReserved(role)`, the refusal sentence) with `<remarks>` saying why it mirrors `Facilitation`, in
  `src/Parley.Core/PersonSeat.cs`; fold `Seats.Adapter` into it, in `src/Parley.Core/Seats.cs`
- [X] T009 [P] Add `PersonSeatTests` for `Is`/`IsReserved`/`On`, in `tests/Parley.Core.Tests/PersonSeatTests.cs`
- [X] T010 In `src/Parley.Store/Migrations/001_initial.sql`: make `agents.machine` NULL for the two reserved roles
  and add its two checks; add the check that neither has an `owns` line (the second `/speckit-analyze`, issue 3); add the `WHEN` guard to `tr_agent_declares_its_machine`; add `tr_person_is_never_retired`,
  `tr_person_is_never_purged` and `ux_one_person` (all per data-model.md). Confirm the SQLite bundled with
  Microsoft.Data.Sqlite is 3.31 or later, since generated columns need it. The person rules are **keyed on the role
  `human-user`, never on the adapter** —
  typed seats still carry `parley-console` until Phase 4, and an adapter key would collide the broker's own row
  with the first of them (data-model.md). The reserved-role pair waits for T045, rule 6 above. Same commit as T007
- [X] T011 Add `AgentRepository.EnsurePersonAsync(project)` and `PersonAsync()`, finding the row by role and
  inserting `human-user@<project>`, `parley-console`, `pull`, `active` only if absent; change
  `EnsureFacilitatorAsync` to take the project, in `src/Parley.Store/AgentRepository.cs`
- [X] T012 Store tests: ensure is idempotent across two calls; both rows have a NULL machine and no `nodes` row is
  written for the project; an agent still declares its machine; a malformed project half
  (`parley-facilitator@Alpha!`, `@a.b`, empty) is refused; an `owns` line on either participant is refused, by
  insert and by `RefreshOwnershipAsync`; retire and purge refused; a second person refused by `ux_one_person`. **Remove each rule in turn and watch its test fail**, in
  `tests/Parley.Store.Tests/PersonSeatStoreTests.cs`
- [X] T013 `BrokerRuntime.Open` takes a required `ProjectName`, passed by `src/Parley.Cli/ServeCommand.cs` and the
  14 real-store call sites; `OpenInMemory`, test-only, takes an optional one defaulting to `test`. The constructor
  calls `EnsurePersonAsync(project)` and `EnsureFacilitatorAsync(project)`, and exposes `Person { get; }`, in
  `src/Parley.Server/BrokerRuntime.cs`. **Bump `VersionPrefix` to 0.36.0 in this commit**, the first to change
  `src` (`/speckit-analyze` issue 5; CLAUDE.md *Versioning*), in `Directory.Build.props`. **In the same commit,
  the reset note of T052a for the installers and CLAUDE.md**, so the first build that says 0.36.0 is also the
  first to say an older store must be reset (the third `/speckit-analyze`, issue 1)
- [X] T013a Audit every C# read of `AgentName.Machine`, and every comparison of an agent's machine to this one's,
  for a path that can meet `human-user@<project>` or `parley-facilitator@<project>`; each is either unreachable for
  them (say why in a remark) or changed. Record the list in this file

  **T013a's audit, 2026-09-30** — every `.Machine` read on an agent's name in `src`:
  - **Changed**: `AgentRepository.UpsertAsync` and `RegisterIfAbsentAsync` wrote the `nodes` row themselves, before
    the trigger — so a surface attaching as the person would have recorded a machine called after the project. Both
    now skip the two roles (`HostedOnAMachine`), and `PersonSeatStoreTests` holds it. **The one real finding.**
  - **Unreachable for them**: `BrokerRuntime.Ask` (1111) — the facilitator returns before it, and the person will
    from T016; until then the person's project has no node, so it answers Unreachable, which T016 replaces.
    `BrokerRuntime.DeliverInstructionAsync` (803) — `instruct` refuses the facilitator today and the person from T048.
    `BrokerRuntime` refresh (1381) — `DueAsync` excludes both adapters. `OwnershipLine.NotHere` — ownership excludes
    both. `ServeCommand` (953) — refreshed agents only.
  - **Harmless for them**: `AgentStanding` (166, 188, 236) compares a machine only against catalogued roles, and no
    catalogue can hold either reserved role once enrolment refuses them (T047). `ServerOptions` (183) — a launch
    naming `human-user@<project>` fails the this-machine check, which is correct.
  - **Goes in T026**: `Shell.Mine` (3305), with the rest of the typed-seat code.
  - **Not an agent's name**: `LocalLauncher` (a file location's machine).
- [X] T014 Server test: a fresh broker for project `alpha` holds exactly one `human-user@alpha` and one
  `parley-facilitator@alpha` before anything attaches, and still one of each after a restart; `Nodes.Describe`
  of `alpha` says no such machine, in `tests/Parley.Server.Tests/PersonSeatExistsTests.cs`

**Checkpoint**: every served project has a person seat. Nothing yet uses it.

---

## Phase 3: User Story 1 — An agent asks the person, hours later (P1) 🎯 MVP

**Goal**: A message to the person is always accepted and its sender is told the person will answer.
**Independent test**: with no surface open and no node connected, an agent's Request to `human-user` returns
`WITH_THE_PERSON`, the message waits, and a Result from the person reaches the agent.

- [X] T015 [P] [US1] Add `WakeOutcome.WithThePerson = 4` with `<remarks>` on why it is not `AlreadyComing`, in
  `src/Parley.Core/WakeOutcome.cs`; add `WAKE_OUTCOME_WITH_THE_PERSON = 5` in `proto/parley.proto`; map it in
  `src/Parley.Server/Wire.cs`
- [X] T016 [US1] In `RequestAttentionForMail`, return `WithThePerson` for `PersonSeat.Is(agent)` after the nudge
  and before any claim, and skip the person in `WakeForOutstandingWorkAsync`, in `src/Parley.Server/BrokerRuntime.cs`
- [X] T017 [US1] Server probe: a Request to the person with no node at all, with a node whose catalogue lists
  a role `human-user`, **and while a surface holds the seat** (`/speckit-analyze` issue 7) all return
  `WITH_THE_PERSON`; no node receives a start or turn command; the attached surface receives its notice. **Watch it fail
  against today's code (Unreachable)**, in `tests/Parley.Server.Tests/MessageToThePersonTests.cs`
- [X] T018 [P] [US1] Render `WithThePerson` in `ParleyTools.Woken` ("with the person: they have it and will
  answer. They may be away; waiting for their decision is correct."), in `src/Parley.Mcp/ParleyTools.cs`; handle
  it in `SeatVoice.Waking` and `Reported` so no switch falls through, in `src/Parley.Client/Seating/SeatVoice.cs`
- [X] T019 [US1] Replace the "if nobody is seated" clause in the MCP instructions and in `parley_result`'s
  description with contracts/person-seat.md's sentence, in `src/Parley.Mcp/ParleyInstructions.cs` and
  `src/Parley.Mcp/ParleyTools.cs`
- [X] T020 [US1] MCP test: the instructions and the `parley_result` description name `human-user`, say waiting is
  correct, and no longer contain "nobody is seated", in `tests/Parley.Mcp.Tests/PersonInstructionsTests.cs`
- [X] T021 [US1] Server test over a socket, as a theory over **1 minute, 20 minutes, 1 hour and 7 hours** on a fake
  clock (SC-002, `/speckit-analyze` issue 9): an agent's Request to the person is admitted, waits uncollected, is
  drained by the person, and the person's Result reaches the agent attributed to `human-user@<project>` with
  responder `human:human-user`. No retention case: the person's mail follows the normal rules (FR-010), in `tests/Parley.Server.Tests/AskThePersonLaterTests.cs`

**Checkpoint**: SC-002 holds at the protocol. Surfaces still use typed seats.

---

## Phase 4: User Story 2 — The person never has to join (P1)

**Goal**: Both surfaces act as the project's person seat on open, with the name from the registry.
**Independent test**: on a machine with projects `alpha` and `beta`, open either surface and send in each with no
join step, as `human-user@alpha` and `human-user@beta`; with `PARLEY_MACHINE=laptop`, still `human-user@alpha`.

**T022 to T031 land as one commit** (`/speckit-analyze` issue 6). T022 and T023 remove API the terminal and the web
UI call until T026–T031 change them, so the build is expected to be red between them. No temporary bridge.

- [ ] T022 [US2] Change `Seat` to hold the `AgentName` read from the registry's person row, keeping `Adapter` and
  giving `Responder = human:human-user`; delete `TryRead`, `Correct`, `Rule`, `Pattern`, `Longest`, `Allowed`, in
  `src/Parley.Client/Seating/Seat.cs`
- [ ] T023 [US2] Change `SeatBook.TakeAsync(project, registry)` to find the person row (`PersonSeat.Is`) and attach
  as it; refuse with a plain sentence when the registry is null or holds no person row; delete `Leave`; key holds
  by project alone, in `src/Parley.Client/Seating/SeatBook.cs`
- [ ] T024 [US2] Client tests: the seat's name comes from the registry and not `MachineName.Current` (fail against
  `Seat.Name` as it is today) — this is FR-017's evidence (`/speckit-analyze` issue 10); no person row refuses; unreadable registry refuses; two surfaces holding one
  project's seat are one name, in `tests/Parley.Client.Tests/SeatFromTheRegistryTests.cs`
- [ ] T025 [US2] **Convert the terminal's `Seated` fixture** to the person seat before any terminal code changes,
  and record in this file, per file, which of the 37 terminal test files that rest on it were kept, replaced or
  deleted, and why, in `tests/Parley.Terminal.Tests/Seated.cs`
- [ ] T026 [US2] Take the person seat in every project when the terminal opens and when a project appears;
  remove `Join`, `Join(then)`, `LeaveSeat`, `StartSeatFirst`, `SeatSelected`, `Mine`, `Person`, the "Join as"
  dialog, the `j`/`l`/`t` actions, and the "seats — withdrawn" group (`RetiredSeats`, `/speckit-analyze` issue 13),
  in `src/Parley.Terminal/Shell.cs` and `src/Parley.Terminal/AgentsRegion.cs`
- [ ] T027 [US2] Remove the retired-seat reinstate path (`Withdrawing.ReinstateSeatAsync` and its dialog),
  leaving `Withdrawing.cs` the one file `SurfaceTests` allows `Reinstate` in, with its typed-name prompt intact,
  in `src/Parley.Terminal/Withdrawing.cs`
- [ ] T028 [US2] Remove every "take a seat to…" prompt and the `Released` / `Left` / `SeatWithoutProject`
  sentences that described a released seat; `Voice()` returns the person seat whenever the project is known, in
  `src/Parley.Terminal/RoomRegion.cs`, `LineRegion.cs`, `Participation.cs`, `Shell.cs`
- [ ] T029 [US2] Terminal tests, driven over a socket: open with two projects and send in each with no key
  before the message; the sender is the registry's person row; no action named join or leave is offered
  (`DeadSurfaceTests` and `NoTwoActionsShareAKeyTests` stay green), in
  `tests/Parley.Terminal.Tests/NoJoinTests.cs`
- [ ] T030 [US2] Web UI: take the person seat in every project in the frame; delete `Pages/Join.*`,
  `Pages/Leave.*` and `Seating/SeatCookie.cs`; remove the Join and Leave links from `Shared/_Layout.cshtml`,
  in `src/Parley.Web`
- [ ] T031 [US2] Web UI: `Conversations/Detail` always speaks as the person seat and never falls back to an
  instruction; `Inbox` reads the person seat's mail with no redirect to `/Join`, in
  `src/Parley.Web/Pages/Conversations/Detail.cshtml(.cs)` and `src/Parley.Web/Pages/Inbox.cshtml(.cs)`
- [ ] T032 [US2] Web tests: a message posted from a conversation is the person's, not an instruction (fail
  against today's unseated fallback); `/Join` and `/Leave` are gone; the inbox needs no cookie, in
  `tests/Parley.Web.Tests/NoJoinOnTheWebTests.cs`; sort the 7 web test files that rest on joining as T025 does
- [ ] T033 [P] [US2] Sort the 9 client test files that rest on typed seats (kept, replaced or deleted, with why),
  recorded in this file. **`TwoSurfaceDeliveryTests` is kept and converted**, not deleted: it is the evidence for
  the spec's "mail is collected once across two surfaces" (`/speckit-analyze` issue 12)

**Checkpoint**: SC-001 holds. No fixture seats anybody under a typed name.

---

## Phase 5: User Story 3 — Everyone can see whether the person is here (P2)

**Goal**: The person reads `here` or `away` everywhere, and never `startable`.
**Independent test**: open a surface → `here`; fire the idle seam → `away`, still listed; touch → `here`.

- [ ] T034 [P] [US3] Add `AgentPresence.Here = 3` and `Away = 4`; `AgentLiveness.Read` takes the adapter and
  returns them for the person seat only, and returns `Running` for the facilitator, which SPEC.md §14 already
  calls "present whenever its project is served" (without this it would read `Stranded`, having no machine; the
  second `/speckit-analyze`, issue 4). The person reads `Stranded` between T010 and this task, a state that exists
  only mid-build, in `src/Parley.Core/AgentTier.cs` and `src/Parley.Core/AgentLiveness.cs`;
  add the wire values in `proto/parley.proto` and map them in `src/Parley.Server/Wire.cs`
- [ ] T035 [US3] Pass the adapter to `AgentLiveness.Read` from `ListRoutableAsync` and `GetAllAsync`, in
  `src/Parley.Store/AgentRepository.cs`
- [ ] T036 [US3] Core and store tests: the person is `here` with a connection and `away` without, on a machine
  with and without a node; no agent ever reads `here` or `away`, in `tests/Parley.Core.Tests/PersonPresenceTests.cs`
  and `tests/Parley.Store.Tests/PersonPresenceStoreTests.cs`
- [ ] T037 [US3] `SeatBook.Sweep` drops an idle hold's stream and keeps the seat; the next `Touch` re-attaches
  silently; `Held` never returns null for a project whose registry has a person row, in
  `src/Parley.Client/Seating/SeatBook.cs` and `SeatHold.cs`
- [ ] T038 [US3] Client test on a fake clock: idle drops the stream (the broker reads `away`), a touch re-attaches
  (the broker reads `here`), and at no point is the seat unavailable to `SeatVoice`, in
  `tests/Parley.Client.Tests/AwayAndBackTests.cs`
- [ ] T039 [P] [US3] Show `here`/`away` for the person in the agents region and in `parley_agents`' output, and
  remove the terminal's "released" state, in `src/Parley.Terminal/AgentsRegion.cs` and `src/Parley.Mcp/ParleyTools.cs`
- [ ] T040 [P] [US3] Show `here`/`away` for the person on the web agents page and in `parley registry`, in
  `src/Parley.Web/Pages/Projects/Agents.cshtml(.cs)` and `src/Parley.Cli/OperatorCommands.cs`
- [ ] T041 [US3] Show the waiting count for the person on both surfaces whether here or away, replacing the
  unseated branch (`WaitingUnseatedAsync`, the inbox's "seated nowhere" text), in `src/Parley.Terminal/Shell.cs`,
  `src/Parley.Web/Pages/Shared/_Layout.cshtml`, `src/Parley.Web/Pages/Inbox.cshtml`

**Checkpoint**: SC-004 and SC-005 hold.

---

## Phase 6: User Story 5 — A flag about the person reads as the person's to-do (P3)

Placed before US4 because it touches no rule US4 depends on and US4 is the phase most likely to reset.

**Goal**: A flag owed by the person reads *waiting on you* and suggests no remedy for an agent.
**Independent test**: a Request to the person past the flag threshold, read on all three surfaces.

- [ ] T042 [P] [US5] One wording for a flag whose owing participant is the person — "waiting on you: <asker> asked
  <when>" — with no "ask the agent" remedy, in `src/Parley.Client/Rendering/FlagWording.cs` (new)
- [ ] T043 [US5] Use it in the CLI, the terminal's Health dialog and the web Health page, in
  `src/Parley.Cli/OperatorCommands.cs`, `src/Parley.Terminal/Health.cs`,
  `src/Parley.Web/Pages/Projects/Health.cshtml.cs`
- [ ] T044 [US5] Tests: each surface words a person-owed flag as waiting on you and an agent-owed flag as before,
  in `tests/Parley.Client.Tests/FlagWordingTests.cs`

---

## Phase 7: User Story 4 — The name cannot be taken by mistake (P2)

**Depends on Phase 4**: rule 6 above.

**Goal**: No repository can be enrolled as `human-user`, no agent attaching as an agent can be it, and nothing but
the broker's own row can be stored as it or under the seat's adapter. What this does not stop is the spec's
*What the reservation does not do*.
**Independent test**: enrol as `human-user`; attach under it with any adapter but the seat's; direct insert as
`human-user` under another adapter, and of any other role as `parley-console`; all refused.

- [ ] T045 [US4] Add `tr_person_role_is_reserved` and `tr_person_role_is_reserved_on_update` to
  `src/Parley.Store/Migrations/001_initial.sql` and to SPEC.md §9.2 in the same commit
- [ ] T046 [US4] Store probes: `human-user@x` as `claude-code`, `orders@x` as `parley-console`, and updating the
  person's adapter — all refused. **Remove each trigger and watch its probe pass**, in
  `tests/Parley.Store.Tests/PersonSeatStoreTests.cs`
- [ ] T047 [P] [US4] Refuse `human-user` at attach and in `Caller`, in `src/Parley.Server/AgentsService.cs`; at
  enrolment, in `src/Parley.Setup/EnrollmentPlan.cs`
- [ ] T048 [P] [US4] Refuse the person seat in `OperatorsService.Subject` and `Instruct` with a sentence saying it
  is the person (`start`, `retire`, `reinstate`, `purge`, `ask_again`, `instruct`), in
  `src/Parley.Server/OperatorsService.cs`
- [ ] T049 [US4] Probes over a socket and through the binary, **with the person row already present**, as it always
  is: `parley-mcp` launched as `human-user` (adapter `claude-code`) is refused at attach; `parley enroll` of a
  repository as `human-user` refuses; `parley retire` and `purge` of the person refuse; the facilitator and the
  person cannot take each other's role. **And one probe that asserts the recorded limit**: calling
  `BrokerRuntime.AttachAsync` directly, bypassing `AgentsService`, with adapter `claude-code` under the person's
  name writes **no new row** and fires no reserved-role trigger, **raises the person's connected count** (so the
  person reads `here`), and **cannot plant an `owns` line**, because storage refuses it (T010). A claimant whose launch carries an `owns`
  line therefore fails at the refresh: assert that the attach is refused with a sentence naming the person seat,
  not a raw constraint error. So the check at
  `AgentsService` is the whole of the guard against a claimant, as the spec's *What the reservation does not do*
  says. If that probe ever fails, the spec's section is out of date, in
  `tests/Parley.Server.Tests/PersonNameIsReservedTests.cs` and `tests/Parley.Cli.Tests/PersonNameIsReservedTests.cs`
- [ ] T050 [US4] Re-check the existing facilitator probes (`ParleyIsNotThePersonSeatedTests`,
  `APersonsWordsAreNeverParleysTests`) against the person seat: the facilitator's relay of a person's reply is
  attributed to `human-user`, in `tests/Parley.Server.Tests` and `tests/Parley.Broker.Tests`

**Checkpoint**: SC-003 holds.

---

## Phase 8: Polish and cross-cutting

- [ ] T051 [P] Rewrite README's "Taking part yourself" and the `parley web-ui` help line: no join, you are
  `human-user`, here and away, what agents are told, in `README.md`
- [ ] T052 [P] Update CLAUDE.md's `Parley.Web` paragraphs on the seat (the typed name, `SeatBook` refusing names,
  `IdleAfter` releasing a seat), in `CLAUDE.md`
- [ ] T052a [P] **Say that 0.36.0 needs a reset.** The installer and CLAUDE.md parts land with T013; what is left
  here is the README, and checking the installers' wording against the finished build. Schema 1 is edited in place and its number does not move, so a
  store created before this change keeps its old shape with no warning: no person-seat rules, a real `nodes` row
  named for the project, and the facilitator under its machine name. The owner's call (the second
  `/speckit-analyze`, issue 1): documented, not refused. Say it in both installers' output when upgrading across
  0.36.0, in README's upgrade notes and in CLAUDE.md, naming the installers' own `-Reset` / `--reset` (which run
  `reset.ps1` / `reset.sh`; both exist), in `install.ps1`, `install.sh`, `README.md`, `CLAUDE.md`
- [ ] T053 Confirm `VersionPrefix` is 0.36.0 (bumped in T013) and that nothing after it needs a further patch
  bump before the walk, in `Directory.Build.props`
- [ ] T054 Sweep for every sentence that still tells a person to join or take a seat, or says a seat was
  released — `git grep -i` for `take a seat`, `join`, `released`, `seated nowhere`, `Join as` across `src`,
  `plugins`, `README.md` — and fix or justify each
- [ ] T055 Run every test host executable directly (`tests/Parley.*.Tests/bin/*/net10.0/Parley.*.Tests.exe`),
  not `dotnet test`, and record the counts per project from a build made after the last edit
- [ ] T056 Write `specs/023-always-a-seat/walk.ps1` and walk `quickstart.md` steps 1–6 against packed tools in a
  scratch root with every `PARLEY_` variable cleared; record each check in `REVIEW-LEDGER.md`
- [ ] T057 Walk quickstart step 7 with this repository's own agent and record what it actually did (SC-006)
- [ ] T058 Hygiene: case-insensitive `git grep` for the originating project's names, **including untracked new
  files** (`git grep` misses them), before committing
- [ ] T059 Update CLAUDE.md's plan pointer for 023 with what building it found
- [ ] T060 **Ask the owner** whether 023 joins the combined 019–022 review gate or gets its own, then run the gate
  after the last edit: three consecutive clean runs per lens, logged in `REVIEW-LEDGER.md`

---

## Dependencies

```text
Phase 1 (T001 → T002 → T003–T007)
   └─▶ Phase 2 (T008–T014)
          ├─▶ Phase 3  US1  (protocol; independent of surfaces)
          ├─▶ Phase 4  US2  (surfaces; T025 before T026–T029)
          │      └─▶ Phase 7  US4  (the reserved-role triggers need every typed seat gone)
          ├─▶ Phase 5  US3  (T037 needs T023)
          └─▶ Phase 6  US5
Phases 3–7 ─▶ Phase 8 (T060 last, after the last edit)
```

- US1 and US2 are both P1 and can run in parallel after Phase 2.
- US3's T037 edits `SeatBook` after T023 does.
- US4 must follow US2 (rule 6).

## Parallel examples

- After Phase 2: T015 (Core/wire) ∥ T022 (Client seat) ∥ T034 (presence enum) — three different files.
- Within US1: T018 (renderers) ∥ T019 (instructions) once T015 is in.
- Within US3: T039 (terminal + MCP) ∥ T040 (web + CLI).
- Within US4: T047 (attach + enrolment) ∥ T048 (operator refusals).

## Implementation strategy

**MVP is Phases 1–3**: the documents, the seat existing, and an agent never told the person is unreachable. That
alone fixes the defect that makes agents give up on the person, even while surfaces still ask for a name.

Then US2, because it is the owner's opening request and the largest change; then US3, US5 and US4; then polish,
the walk and the gate.
