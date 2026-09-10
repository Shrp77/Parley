# Tasks: Waking notifications

**Feature**: `specs/010-waking-notifications/` | **Branch**: `010-waking-notifications`
**Ships as**: 0.23.0 | **Protocol**: 0.4

**Every phase leaves the tree green.** A phase that needs the next one to compile is two phases
written as one.

**Failing tests come before the fix they pin.** House style, and it is load-bearing here: a test
that passed throughout proves nothing about what was repaired. Each `[TEST-FIRST]` task must be seen
to fail for the stated reason before its implementation task is started.

**`dotnet test` reports `Zero tests ran` on this machine.** Run the host executables directly:
`tests/Parley.*.Tests/bin/*/net10.0/Parley.*.Tests.exe`. And **rebuild with nothing running** — a
build blocked by a live test exe leaves a stale assembly beside the tests, which has cost this
repository a debugging session twice.

---

## Phase 1: The bound, with nothing calling it

**Goal**: coalescing exists, is tested, and changes no behaviour because no caller consults it yet.

**Independent test**: `RequestAttention` marks attention pending; a drain clears it; the ceiling
expires it. Notifications are not involved.

- [ ] T001 [TEST-FIRST] Add `tests/Parley.Server.Tests/WakeCoalescingTests.cs` asserting that
      attention pending for an agent is set by a wake, cleared by a drain, and expired by the
      ceiling. **Must fail to compile** — the marker does not exist. That is the intended first
      failure.
- [ ] T002 Add the pending-attention marker to `src/Parley.Server/BrokerRuntime.cs`: a
      `ConcurrentDictionary<string, DateTimeOffset>` keyed on the agent's full name, set by every
      wake, with a `remarks` recording that it is in memory on purpose — a broker restart holds no
      residents, so a remembered wake would assert something about a process that no longer exists.
- [ ] T003 Add the ceiling as a named constant beside it, with the reasoning from `research.md` R4
      in its `remarks`: a failure valve rather than the mechanism, erring long because the error is
      asymmetric, and provisional pending measurement from `invocations`.
- [ ] T004 Add the read side — whether attention is already pending for an agent — and the clear.
      Nothing calls either yet.
- [ ] T005 Make T001 pass. Verify the ceiling test uses a fake `TimeProvider` rather than sleeping;
      a test that waits two minutes will be deleted by whoever next runs the suite.
- [ ] T006 [P] Add a concurrency case to `WakeCoalescingTests`: sixty concurrent wakes for one agent
      produce one pending mark. **It must actually race** — sixty sequential calls exercise a path
      that says nothing about the concurrent one, which is the trap `RefillRaceTests` was written
      for.

**Checkpoint**: suite green, behaviour identical to before.

---

## Phase 2: The wire

**Goal**: protocol 0.4 and the new reply field exist. Nothing behaves differently.

- [ ] T007 [TEST-FIRST] Add a test asserting `ProtocolVersion.Current` is `0.4` and that an adapter
      announcing `0.3` is refused at attach. The refusal already exists; this pins that the bump
      reaches it.
- [ ] T008 Add `WakeOutcome` and `NotifyReply.wake = 4` to `proto/parley.proto`, per
      `contracts/wire.md`. Field 4 is new; 1 to 3 untouched.
- [ ] T009 Move `ProtocolVersion.Current` to `0.4` in `src/Parley.Core/ProtocolVersion.cs`.
- [ ] T010 Map the enum in `Conversions` — **not** at the call site. One home per mapping is what
      lets the reflection test check them all; a mapping written where it is used is one the guard
      cannot see.
- [ ] T011 Add the §18 entry to `SPEC.md` from `contracts/wire.md`. `Parley.Spec.Tests` must stay
      green, which it will only if every section reference in it resolves.
- [ ] T012 Check `The wire against the document` (added in 008) still passes — it compares SPEC.md
      against `proto/parley.proto` and is the test that exists because nothing used to check the
      proto at all.

**Checkpoint**: suite green. Every adapter in the repository now speaks 0.4.

---

## Phase 3: The wake

**Goal**: a notification starts its recipient, bounded. This is the phase that must be revertible
alone.

- [ ] T013 [TEST-FIRST] Add `tests/Parley.Server.Tests/NotifyWakesTests.cs`: a notification to an
      unattached agent asks its node for a start. **The fixture must have a node** — with none,
      `RequestAttention` returns false at its first line and the test would pass having exercised
      the early return and nothing else.
- [ ] T014 [TEST-FIRST] Sixty notifications to one idle agent produce **one turn**, not sixty and
      not merely one process. Assert on turns; `NodeCommand.StartAsync` dispatches a turn for a
      live resident, so a process count would pass while the defect stood.
- [ ] T015 [TEST-FIRST] A notification arriving while a question's turn is pending asks for nothing
      further (FR-008a).
- [ ] T016 [TEST-FIRST] A refused notification (retired recipient, closed conversation) and a
      deduplicated one each wake nobody (FR-002, FR-003).
- [ ] T017 [TEST-FIRST] An agent that attaches and dies without draining does not clear the marker —
      the case that rules out keying on the attach, and the one the spec's first draft got wrong.
- [ ] T018 Make `AgentsService.Notify` ask for attention, consulting the marker, and return the
      outcome. Keep the existing `NudgeAsync`; the wake is additional, not a replacement.
- [ ] T019 Clear the marker in `AgentsService.DrainInbox` — the single place a collection is
      observable.
- [ ] T020 Make every other wake (`WakeAsync`, `NudgeRecipient`) set the marker without consulting
      it, per FR-008a.
- [ ] T021 Run Phases 1 to 3's tests together. This is the revert boundary: everything after here
      is surfaces and prose.

**Checkpoint**: SC-001 to SC-004 hold. A notification wakes; sixty cost one turn.

---

## Phase 4: The sentence

**Goal**: the sender learns whether anything is going to read it.

- [ ] T022 [TEST-FIRST] Add cases to the client's tests: each of the four wake outcomes produces a
      distinct sentence, and **none of the four reads as a failure** when the message was stored
      (FR-013).
- [ ] T023 Extend `SeatVoice.NotifyAsync` in `src/Parley.Client/Seating/SeatVoice.cs` to report the
      outcome. Written once; both surfaces call it.
- [ ] T024 Check the deduplicated sentence still says what it said. It is a separate fact from the
      wake and must not be swallowed by the new one.

**Checkpoint**: SC-005 holds on both surfaces, because both read one implementation.

---

## Phase 5: All agents

**Goal**: one act tells every agent in a project.

- [ ] T025 [TEST-FIRST] Roster resolution: every non-retired agent, **no seats** — not the sender's
      and not anybody else's (FR-015, SC-007). **Key the fixture the way callers key it.** Three
      suites in 009 keyed catalogues by `role@machine`, a shape no caller produces, which is why a
      closely covered method stayed broken.
- [ ] T026 [TEST-FIRST] An empty roster says so and sends nothing (FR-019).
- [ ] T027 [TEST-FIRST] A partial broadcast reports counts and names what could not be started
      (FR-018).
- [ ] T028 Add the roster resolution and the fan-out to `Parley.Client`. N ordinary notifications;
      no wire operation.
- [ ] T029 Re-check the seat **at each send**, not once at the start — the rule established in
      0.18.9, where a seat swept mid-compose still spoke and the transcript recorded words under a
      released name.
- [ ] T030 Add **All agents** to the terminal's peer picker in `src/Parley.Top/Shell.cs`, and its
      wording to `src/Parley.Top/Participation.cs`.
- [ ] T031 Name the count before sending (FR-020).
- [ ] T032 Add the same target to the console's composer. **Read it first** — the terminal's picker
      landed in 0.22.2 and the console's composer is older; the plan does not assume they are the
      same shape.
- [ ] T033 Check `ActionShapeTests.No_two_dialogs_answer_to_the_same_title` still passes. A new
      dialog sharing a title with the picker broke it once already in 0.22.2.
- [ ] T034 Check `DriftTests` — it compares every operation on `BrokerClient` against both surface
      trees, so a broadcast bound on one surface and not the other fails the build. That is the
      intended behaviour, not an obstacle.

**Checkpoint**: SC-006 and SC-007 hold.

---

## Phase 6: The document

**Goal**: SPEC.md says what the code does, and the reversal is on the record.

- [ ] T035 §5.4: a notification causes its recipient to be started, and the coalescing bound as part
      of the same rule (FR-021).
- [ ] T036 §17 question 15: answered, with why the unboundedness objection does not survive
      coalescing. **Kept and answered rather than deleted** — the trade it describes is the one
      being reversed, and 007's `--with-console` paragraph is the precedent for correcting in place
      rather than removing (FR-022).
- [ ] T037 §17 question 1: a notification now costs a turn on the recipient's machine, and where
      that cost is measurable. The question itself stays open — this bounds turns, not messages
      (FR-023).
- [ ] T038 Run `Parley.Spec.Tests`. Every section reference must resolve, no table may be ragged,
      and the §8 rules must stay contiguous.

---

## Phase 7: The leftovers

- [ ] T039 [P] Fix the `InvocationRepository.FindWaitingAsync` remark: it claims four conditions and
      names an `invoke_when_idle` column that exists nowhere. Consent moved to the node catalogue;
      the query has two conditions.
- [ ] T040 [P] Update the plugin's skill description of `notify` if it states the old behaviour
      (FR-024). A test compares the verbs in the tool's source against the skill's.
- [ ] T041 `VersionPrefix` to `0.23.0` in `Directory.Build.props`.
- [ ] T042 Update CLAUDE.md's active-plan line to match. `ProjectRecordTests` enforces that the
      notes name the shipping version, and it derives the number from the props file rather than
      from a literal — so both must move together.
- [ ] T043 Update `README.md` if it describes what a notification does. It must stand alone: no
      spec section references.
- [ ] T044 Add the upgrade instructions from `quickstart.md` wherever the repository tells somebody
      how to upgrade. **A broker upgraded without its agents is a deployment where nothing
      attaches**, and the failure is only diagnosable from the broker's log.

---

## Phase 8: The review gate

**Three consecutive clean runs per lens. Any finding above low resets that lens to zero.** Each run
must probe something the previous runs did not. Logged in `REVIEW-LEDGER.md`, clean runs included.

- [ ] T045 Lens 1, correctness and concurrency ×3. **The marker is the obvious target**: it is a
      dictionary read and written from gRPC handler threads, and 0.18.5 records what happened last
      time this repository swapped state under a reader.
- [ ] T046 Lens 2, security and data integrity ×3. A notification now spends another deployment's
      compute — probe the bound adversarially rather than reading it. Can a peer defeat the
      coalescing by varying the recipient, the conversation, or the timing?
- [ ] T047 Lens 3, API surface and maintainability ×3. `SurfaceBoundaryTests`, `DriftTests`,
      `GraphTests`, `DeadSurfaceTests`.
- [ ] T048 Full sweep, all twelve host executables, nothing else running.
- [ ] T049 Verify on a live deployment per `quickstart.md`. Needs the owner present: it stops a
      broker and upgrades every agent.
- [ ] T050 [P] Linux in a container — `podman` with `mcr.microsoft.com/dotnet/sdk:10.0`, one host
      looped under Windows load. CI is billing-refused; this is the recorded practice for that.
- [ ] T051 PR, then squash-merge. The PR title becomes the commit message on `main`. Design
      rationale in the body — that is the durable record.

---

## Dependencies

```
Phase 1 (the bound) ──┐
                      ├──> Phase 3 (the wake) ──> Phase 4 (the sentence) ──┐
Phase 2 (the wire) ───┘                                                    ├──> Phase 6, 7, 8
                                                     Phase 5 (All agents) ─┘
```

Phase 5 depends on Phase 4 only for the sentence it aggregates. Phases 1 and 2 are independent of
each other and could be done in either order; Phase 1 first because it is the risky half and a
version bump is cheap to add to a branch that already works.

## What is deliberately not here

- **Bounding Notify's volume.** §17 question 1 stays open.
- **`parley fleet` in the autostart entry.** Recorded in the spec's out-of-scope section.
- **The roster display spelling.** Deferred from 009; a catalogue key may not be a valid role, and
  settling it reaches into the purge and retire doors, which take a name typed back.
