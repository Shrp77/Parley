# Implementation Plan: Waking notifications

**Branch**: `010-waking-notifications` | **Date**: 2026-09-10 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `specs/010-waking-notifications/spec.md`

## Summary

A notification asks for attention on the same path a question does, and is coalesced per recipient
so that many notifications cost one turn. The composer gains an **All agents** target that sends N
ordinary notifications. SPEC.md §5.4 gains the behaviour, §17 questions 1 and 15 are answered, and
§18 gains **protocol 0.4**.

**The coalescing is not an optimisation; it is the condition on which the wake is allowed to ship.**
§17 question 15 refuses the wake on the grounds that it would be unbounded. That objection is
answered by question 1's own evidence — `inbox` collects everything pending, so one turn drains
sixty notifications — but only if the implementation actually produces one turn.

## Technical Context

**Language/Version**: C# / .NET 10
**Primary Dependencies**: gRPC (`Grpc.AspNetCore`), Terminal.Gui v2 (terminal only), no new packages
**Storage**: SQLite via Dapper — **no schema change expected**; the pending marker is in memory
**Testing**: xUnit v3 on Microsoft.Testing.Platform; run host `.exe`s directly on this machine
**Target Platform**: Windows and Linux verified; macOS via CI when billing permits
**Project Type**: Multi-project solution — protocol, broker, transport, surfaces
**Constraints**: `Parley.Core` no I/O; no store on `Parley.Client` / `Parley.Web` / `Parley.Top` / `Parley.Setup`
**Scale/Scope**: A deployment of ~20 agents on one machine is the case that motivated this

## The consequence that costs the most, named first

**This is protocol 0.4, and while the major is zero the minor carries breaking changes.** §18:

> Two implementations whose minors differ do not interoperate, and §4.0's `Attach` refuses the
> pairing rather than discovering it later.

`AgentsService` enforces that strictly — any difference is refused. So **every agent on the
deployment must be upgraded together with the broker**, or its adapter is refused on attach and the
agent silently stops being reachable.

That is unavoidable rather than a choice about how to package this. A notification that starts an
agent is observable by a second implementation, which is §18's own test for what moves the number,
and §18 gives a protocol no patch level in which a compatible change could hide.

**Two things follow for how this ships.**

1. **This release carries the wire change and nothing else that could need reverting.** CLAUDE.md
   records the reasoning from 008 — a release that changes a versioned wire contract *and* something
   an operator's scheduled task depends on "cannot be reverted in halves". The wire change must be
   revertible alone.
2. **The upgrade instructions are part of the deliverable, not an afterthought.** An operator who
   upgrades the broker and not the agents gets a deployment where every agent fails to attach, and
   the failure names a version mismatch rather than looking like a broken agent — but only if
   somebody reads the log.

**Package version**: `0.22.2` → **`0.23.0`**. Minor, because the wire contract breaks.

## Constitution Check

*GATE: passed before Phase 0. Re-check after Phase 1.*

| Principle | Verdict |
|---|---|
| I. The specification is the product | **Held, and this is the shape it is meant to have.** The change is argued in SPEC.md first — §5.4 gains the rule, §17 questions 1 and 15 are answered, §18 records 0.4. The code follows. Question 15 is answered rather than deleted, because the trade it describes is the one being reversed. |
| II. No agent has authority over another | **Held, and worth checking rather than assuming.** A notification causes a peer's machine to spend a turn, which is a cost imposed on another deployment. It is not *authority*: the agent decides what to do with the turn, may drain and do nothing, and is not told what to do. This is the same relationship a question already has, and questions have always started agents. What the coalescing adds is that the cost is bounded by the recipient's own reading rather than by the sender's volume. |
| III. Identity is fixed by the launch | **Untouched.** No operation gains a sender parameter. The All-agents fan-out is N ordinary sends under the sender's own identity. |
| IV. Terseness is a security control | **Held.** The one new field that crosses is an enum, not text, so there is nothing to bound. The broadcast sends N ordinary notifications, each subject to the existing cap — it does not create a path by which more text crosses per send than today. |
| V. Invariants live in storage | **Held, with the argument made rather than skipped.** Nothing new is stored, so there is no invariant to put in the schema. The pending marker is deliberately *not* durable: a broker restart has no residents, so a remembered wake would assert something about a process that no longer exists. FR-009 makes the same argument for the ceiling being a constant rather than a schema fact — unlike §13.1's window, it changes the meaning of no stored row. |
| Agent vs operator surfaces | **Held.** Everything here is on `Agents`. Nothing is added to `Operators`. |
| A green build is the whole lint gate | Unchanged. |

**No violation to justify.** The one that needed argument rather than assertion is II, and it is
made above rather than waved at.

## Project Structure

### Documentation (this feature)

```text
specs/010-waking-notifications/
├── plan.md              # This file
├── research.md          # The decisions, with what was read rather than reasoned
├── contracts/
│   └── wire.md          # What moves on the wire, and the version it costs
├── quickstart.md        # Verifying it on a live deployment
└── tasks.md             # /speckit-tasks output
```

No `data-model.md`: nothing is stored. Writing one would be a page saying so.

### Source touched

```text
proto/parley.proto                        NotifyReply gains a wake outcome; new enum
src/Parley.Core/ProtocolVersion.cs        0.3 -> 0.4
src/Parley.Server/BrokerRuntime.cs        the pending marker, set / consulted / cleared
src/Parley.Server/AgentsService.cs        Notify asks for attention; DrainInbox clears
src/Parley.Client/Seating/SeatVoice.cs    the outcome sentence; the broadcast
src/Parley.Top/Participation.cs           All agents as a target, and its wording
src/Parley.Top/Shell.cs                   All agents in the peer picker
src/Parley.Web/...                        the same target on the console's composer
src/Parley.Store/InvocationRepository.cs  the carried remark defect
SPEC.md                                   5.4, 17 q1, 17 q15, 18
plugins/parley/...                        notify's description, if it states the old rule
Directory.Build.props                     VersionPrefix 0.23.0
CLAUDE.md                                 active plan line
```

## Where each rule lives

**One implementation, not six.** The spec's requirements map to exactly one place each.

| Rule | Where it lives | Read by |
|---|---|---|
| FR-001 a notification asks for attention | `AgentsService.Notify` | the wire |
| FR-002 a refusal wakes nobody | `AgentsService.Notify`, after the outcome | — |
| FR-003 a deduplicated notification wakes nobody | `AgentsService.Notify`, on `outcome.Stored` | — |
| FR-005..FR-008a the bound | `BrokerRuntime`, one private method | every caller of `RequestAttention` |
| FR-009 the ceiling | a named constant on `BrokerRuntime` | the bound |
| FR-011 the outcome on the reply | `proto/parley.proto` + `Conversions` | both surfaces |
| FR-012, FR-013 the sentence | `SeatVoice.NotifyAsync` | terminal and console |
| FR-014..FR-020 All agents | `SeatVoice` for the send, `Participation` for the words | terminal and console |

**The bound cannot live in the node**, and that was established by reading rather than by argument.
`NodeCommand.StartAsync` dispatches a turn when the resident is already alive — deliberately, with
a comment explaining the race it is closing. So the node already prevents a duplicate *process* and
does not prevent a duplicate *turn*, and it could not: it does not know a command originated in a
notification rather than a question, and it must not learn, because that would put a protocol
decision in the thing that launches processes.

**The marker keys on the drain, not on the attach**, for the same reason. Sixty notifications to an
idle agent produce one start; the agent attaches within a second or two and the remaining
fifty-nine each find it attached and ask for a *turn*. A marker cleared by the attach would clear
before the flood arrived and bound nothing. §4.2 already draws exactly this distinction — waking an
adapter is not the model taking a turn — and `AgentsService.DrainInbox` is the one place a
collection is observable.

**Every wake sets the marker; only a notification consults it** (FR-008a). A question keeps today's
behaviour because §7.1's per-pair cap already bounds questions, which is precisely the bound §17
question 15 identified as holding the line today. What changes is that a notification will not stack
a turn on top of one a question has already asked for.

## Phases

Each phase leaves the tree green.

**Phase 1 — the bound, with nothing calling it.** The pending marker, the ceiling, and the tests
that pin the coalescing, against `RequestAttention` directly. Nothing about notifications yet. The
bound is testable on its own and is the risky half.

**Phase 2 — the wire.** `ProtocolVersion` to 0.4, `NotifyReply` gains its outcome, `Conversions`
maps it, and the version-mismatch refusal is exercised. Nothing behaves differently yet.

**Phase 3 — the wake.** `AgentsService.Notify` asks for attention and reports what happened;
`DrainInbox` clears the marker. This is the phase SC-001 to SC-004 are about, and the phase that
must be revertible alone.

**Phase 4 — the sentence.** `SeatVoice.NotifyAsync` reports the outcome. Both surfaces show it
because both call it.

**Phase 5 — All agents.** The roster resolution, the fan-out, the counts, the pre-send description
of what it will reach.

**Phase 6 — the document.** SPEC.md §5.4, §17 q1, §17 q15, §18. `Parley.Spec.Tests` must stay green,
which it will only if every section reference added resolves.

**Phase 7 — the leftovers.** The `invoke_when_idle` remark, the plugin's wording, `VersionPrefix`,
CLAUDE.md's active-plan line (guarded by `ProjectRecordTests`).

**Phase 8 — the review gate.** Three consecutive clean runs per lens. A protocol change with a
version bump and a new concurrency-sensitive marker is exactly the shape lens 1 exists for.

## Failing tests come first

House style, and it is load-bearing here: **a test that passed throughout proves nothing about what
was repaired.** The specific traps this feature offers —

- A coalescing test that never floods. Sixty sends have to actually race, or the test passes on a
  serial path and says nothing about the concurrent one. `RefillRaceTests` is the precedent.
- A wake test whose fixture has no node. `RequestAttention` returns false with no node and the test
  would pass having exercised the early return.
- A drain test that asserts on the attach. The whole finding above, re-encoded as a check.
- A broadcast test whose roster is keyed the way no caller keys it. Three suites did exactly that
  in 009 and it is why a closely covered method stayed broken.

## Open, and to be settled in tasks rather than assumed

- **The ceiling's value.** Two minutes is the proposal; `research.md` R4 carries why, and it is
  recorded as provisional on the same terms as the message cap — moved on evidence from
  `invocations`, which already records when starts happened, not because a case felt slow.
- **Whether the console composer already has a peer target to extend**, or needs one built. The
  terminal's picker landed in 0.22.2; the console's composer is older. Read it before planning the
  edit.
