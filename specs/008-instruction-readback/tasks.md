# Tasks: An instruction survives the turn it asks for

**Input**: [spec.md](./spec.md), [plan.md](./plan.md), [research.md](./research.md),
[data-model.md](./data-model.md), [contracts/operations.md](./contracts/operations.md)

Ordered by the two shippable steps R10 defines, not by layer. Each step is independently verifiable
and independently mergeable, and each bumps `VersionPrefix` in the same change — step 1 the minor,
step 2 the patch.

`[P]` marks tasks that can run in parallel with the one before it: different files, no dependency on
an incomplete task.

---

## Phase 1: The failing checks come first

The constitution requires probing rather than reasoning. These are written and watched to fail
*before* anything is fixed, so that what follows is a repair rather than a claim.

- [x] T001 **Reframe, do not invert**, `tests/Parley.Store.Tests/InstructionSettlingTests`. Its first case reads as though it pins the loss; at this level it pins *first-come settlement*, which is correct and is not what changes. Renamed to say so, with a remark naming where the defect actually lives — the server calling this on give-up. A test whose name blames the wrong layer sends the next reader to the wrong file.
- [x] T002 [P] Add `tests/Parley.Server.Tests/InstructionSurvivesTests` — over a real socket, a joined node that receives `DeliverInstruction`, reports late, and whose reply is then read back. Must fail today for two reasons at once (no readback operation exists; the reply is discarded), and the test names both.
- [x] T003 [P] Add `tests/Parley.Core.Tests/TurnBudgetTests` asserting the sweep threshold exceeds the longest permitted turn. This is FR-006's mechanical enforcement and it must fail before `TurnBudget` exists.
- [x] T004 [P] Add `tests/Parley.Store.Tests/InstructionRetentionTests` asserting a pending instruction is reachable by the pruner at all. Fails today: nothing prunes instructions, so the assertion has nothing to act on.

---

## Phase 2: Step 1 — the protocol and the broker (0.21.0)

### The domain

- [x] T005 Add `src/Parley.Core/TurnBudget.cs`: the longest a turn may run, and the sweep threshold derived from it. `<remarks>` carries why it is in Core — the only assembly the broker and the node's invoker can both see, checked before designing around it — and why the two numbers are derived rather than written twice.
- [x] T006 [P] Add `InstructionRecord` and `InstructionPage` to `src/Parley.Core/Instruction.cs`, beside the existing `InstructionResult`. Every public member documented.
- [x] T007 Give `InstructionResult` the instruction's identifier (FR-012), so a caller told *pending* knows where to come back to.
- [x] T008 Derive `InvocationCommand.DefaultTimeout` in `src/Parley.Headless` from `TurnBudget`, so the two cannot drift apart again.

### The store

- [x] T009 Add `InstructionRepository.ListAsync`: optional agent, optional identifier, bounded page, newest first, opaque continuation. Refuses nothing — the name check belongs at the wire edge where a bad name is a caller error.
- [x] T010 [P] Add `InstructionRepository.SweepAsync`, settling every pending instruction issued before an instant as failed. `WHERE outcome = 'pending'` carries FR-016 for free; a test says so rather than an inference.
- [x] ~~T011 Add `InstructionRepository.SettleForMachineAsync`~~ — **built, then removed by the review gate (lens 1).** A node's *stream* ending is not its process dying, so settling on it destroyed the agent's answer on every network blip: the record shut, and the real report was refused on arrival. That is the defect this feature exists to remove, reached through a different door. A turn whose node has gone now stays running until T010's sweep presumes it dead.
- [x] T012 Add the pruner's own delete statement for instructions in `src/Parley.Store/Pruner.cs`, aged on `issued_utc`. `<remarks>` carries the trap: a pending row has no `settled_utc`, so ageing on settlement would skip exactly the rows that most need a bound.

### The broker

- [x] T013 Add the readback and the sweep to `src/Parley.Broker/OperatorConsole.cs`. Operator surface only — `Broker` gains nothing, and `SurfaceBoundaryTests` will say so.
- [x] T014 Call the sweep from `Broker.TickAsync`, beside §14's ghosting. A second reaper would be a second schedule for a strictly rarer case.

### The wire

- [x] T015 Add `Instructions` to the `Operators` service in `proto/parley.proto`, with `InstructionsRequest`, `InstructionRecord` and `InstructionsReply` as contracts/operations.md defines them. `Agents` gains nothing.
- [x] T016 Add `instruction_id` to `InstructReply` as an additive field.

### The server

- [x] T017 **Stop settling on give-up** in `src/Parley.Server/OperatorsService.cs`. This is the defect. The wait drops to 30 seconds and returns `pending` carrying the identifier; nothing is written because the broker stopped waiting.
- [x] T018 Bind `Instructions` in `OperatorsService`, clamping the page the way `ListConversations` clamps its own, and refusing an unparseable agent filter rather than ignoring it (FR-010).
- [x] ~~T019 Settle a departed node's running instructions in `BrokerRuntime.ReleaseNodeAsync`~~ — **built, then removed with T011 and for the same reason.** The seam is still the right one and the comment left there says why nothing uses it, because it is the obvious place for somebody to add this back.

### The client and the command line

- [x] T020 Add `InstructionsAsync` to `IParleyBrokerClient` and `BrokerClient`.
- [x] T021 Add `parley instructions [<agent>] [--limit <n>]` to `src/Parley.Cli/Program.cs`. This is what satisfies `DriftTests` in step 1 with a real binding rather than an exemption.
- [x] T022 Assert in a test that `parley instructions` prints the instruction text and the reply, and that this is deliberate where `parley open` prints no payload — the words are the operator's own and their agent's answer to them, not peer traffic.

### The document

- [x] T023 SPEC.md §10.2: add the readback to the operations table and the prose explaining why a read-only operation exists at all.
- [x] T024 SPEC.md §10.2: four outcomes become five. `pending` is a different fact from `failed`, which claims the turn was attempted and did not complete — a claim nobody may make while it is still being attempted.
- [x] T025 [P] SPEC.md §9.2: commentary on the instructions table saying the unsettled state is one a reader will encounter, not a transient. **No DDL change** — the table was already right.
- [x] T026 [P] SPEC.md §9.4: instructions come under retention, aged from issue, with the reason.
- [x] T027 [P] SPEC.md §14: the sweep, beside the ghosting it already describes. Both are the same shape — a thing left open by something that went away, closed on a bound.
- [x] T028 Run `tests/Parley.Spec.Tests` and confirm every schema fragment still executes in order, every new section reference resolves, no table is ragged, and the new operation name does not collide with anything on §10.1.

### Closing step 1

- [x] T029 Bump `VersionPrefix` to 0.21.0 in `Directory.Build.props`, in this change.
- [x] T030 Record in CLAUDE.md that the console deprecation moves to 0.22.0 and why. *(Done ahead of the tasks, when the collision was found.)*
- [x] T031 Confirm T001 through T004 now pass, and that each failed before its fix. A test that passed throughout proves nothing about what was repaired.

---

## Phase 3: Step 2 — the surfaces (0.21.1)

- [x] T032 `src/Parley.Web/Pages/Projects/Act.cshtml.cs` stops blocking (FR-017). It records, gets the identifier, and returns — *taken* with the reply when the turn beat the wait, *running* otherwise, naming where to look.
- [x] T033 [P] `src/Parley.Web/Pages/Conversations/Detail.cshtml.cs` — the same, on the composer's instruction path.
- [x] T034 Show an agent's recent instructions on `src/Parley.Web/Pages/Projects/Agent.cshtml`, which already shows that agent. A new page would be a second place to look for a fact about the same thing.
- [x] T035 A decline renders as a decline and not a failure (FR-019). §10.2 requires it, and an interface that renders refusal as malfunction teaches whoever reads it to work around the agent.
- [x] T036 A reply renders as the agent's words and cannot be mistaken for the console's own (FR-020) — attributed block, not free-standing prose in the page's voice.
- [x] T037 The terminal binds the readback in its agents view (FR-021), and a reply is subject to the column-zero rule the transcript already carries.
- [x] T038 Bump `VersionPrefix` to 0.21.1.

---

## Phase 4: The gate

- [x] T039 Review gate, lens 1 (correctness and concurrency) — three consecutive runs with no finding above low, each probing something the previous did not. Logged in `REVIEW-LEDGER.md`, clean runs included.
- [x] T040 Review gate, lens 2 (security and data integrity) — three consecutive clean runs.
- [x] T041 Review gate, lens 3 (API surface and maintainability) — three consecutive clean runs.
- [x] T042 Verify on Linux in a container as well as Windows. CI is unavailable (GitHub billing) and the Linux run has already caught two defects Windows passed.
- [x] T043 Hygiene: no trace of the originating private project in anything added. `git grep` sees tracked files only, so check new files by name before relying on it.

---

## Dependencies

```
T001..T004  (failing checks)
     │
     ▼
T005 ──► T008          TurnBudget before anything derives from it
T006, T007 [P]
     │
     ▼
T009..T012             store
     │
     ▼
T013, T014             broker
     │
     ▼
T015, T016             wire  ──► regenerates Parley.Transport
     │
     ▼
T017, T018             server   ◄── T017 is the defect being fixed
     │
     ▼
T020 ──► T021, T022    client, then the command that binds it
     │
     ▼
T023..T028             SPEC.md, then its own suite
     │
     ▼
T029..T031             close step 1 — everything above is one mergeable change
     │
     ▼
T032..T038             step 2, the surfaces
     │
     ▼
T039..T043             the gate, run after the last edit and not alongside it
```

## What is deliberately not here

- **No migration task.** The table already carries every field. A feature that looks like it needs
  storage and does not is worth saying out loud, because the reflex is to add a column.
- **No task removing the deprecated `console` spellings**, though this is 0.21.0 and 007 dated them
  here. R1 settles it and CLAUDE.md records it: they move to 0.22.0, because one release that both
  changes a wire contract and breaks a live autostart entry cannot be reverted in halves.
- **No push, notification or streaming task.** All three are out of scope in the spec and all three
  are the kind of thing that arrives as "while we're here".

## What the review gate changed after the tasks were written

Four findings above low, all fixed, all recorded in `REVIEW-LEDGER.md` with the probe that produced
them. Two removed work this list had asked for, which is why the entries above are struck through
rather than deleted:

1. **A departing node must not settle its turns** (T011, T019). A stream ending is not a process
   dying.
2. **An agent's reply had no bound at all.** `005_instruction_reply_cap.sql` and `MessageLimits.Fit`
   — not in this list, because nobody had noticed the column.
3. **`InstructionsRemoved` was computed and shown to nobody.** The wire carried no field for it, so
   `parley prune` reported only messages.
4. **`parley instructions` flattened the reply and not the instruction**, so a newline in an
   operator's own words forged a line in output that scripts read.

The two-step sequencing held: step 1 shipped at 0.21.0 and step 2 at 0.21.1, and every finding above
was fixed on top rather than folded back into either.
