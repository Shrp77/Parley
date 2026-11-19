# Implementation Plan: Work comes back when the session that took it is gone

**Branch**: `027-session-owned-delivery` | **Date**: 2026-10-23 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `specs/027-session-owned-delivery/spec.md`

Version target: **0.43.0, protocol 0.9 (or the next number if 026 lands first), schema 1 edited in
place.** Nothing is released and there is no compatibility to keep.

## Summary

SPEC.md §6 and §7.3 already promise that work collected by a session which ended without a Result is
delivered again. The code keeps that promise only when the broker hears the old session leave before
the new one attaches, because it decides at attach time from a count of sessions. The fix names and
orders each session and records on every delivery which session took it. Then each way a session can
end has its own rule (research R3):

- **Controlled end:** the adapter calls `HandBack` and the work returns at once.
- **Dropped connection, same process:** the adapter reconnects naming its old session and keeps its
  work.
- **Stopped and never came back:** the work goes to the next session that opened after it was
  collected.

A live session keeps its work. A window that was already open never takes another's work after a
dropped connection, so two windows still do not duplicate each other.

## Technical Context

**Language/Version**: C# on .NET 10
**Primary Dependencies**: Grpc.AspNetCore / Grpc.Net.Client, Dapper, Microsoft.Data.Sqlite
**Storage**: SQLite, schema 1 edited in place (`src/Parley.Store/Migrations/001_initial.sql`, SPEC.md §9)
**Testing**: xUnit v3 on Microsoft.Testing.Platform; `Parley.Spec.Tests` executes SPEC.md's DDL
**Target Platform**: Windows, Linux, macOS (CI runs all three)
**Project Type**: multi-project .NET solution (protocol, broker, server, client, MCP adapter)
**Performance Goals**: the give-back is one indexed `UPDATE` per collection, under the existing gate
**Constraints**: nothing times out; no "working" state; payloads inert; invariants in storage
**Scale/Scope**: sessions per agent are a handful (windows on one repository)

## Constitution Check

| Principle | Result |
|---|---|
| I. The specification is the product | **Pass, by ordering.** SPEC.md changes first (contracts/spec-text.md); `Parley.Spec.Tests` executes the new DDL and holds the proto against the text. This is the code catching up with a promise §6 already makes, plus the schema and wire to keep it. |
| II. No agent has authority over another | **Pass.** One agent operation is added, `HandBack`, and it acts only on the caller's own session. An agent cannot affect another agent's sessions; the server refuses a session belonging to another agent, and a reclaim naming one is ignored. |
| III. Identity is fixed by the launch | **Pass.** The session identifier is issued by the broker, never chosen by the caller, and does not identify the agent; the agent is still the launch's name. Not a credential (§16's concession is restated). |
| IV. Terseness is a security control | **Pass.** One fixed-size field (32 hex characters) crosses, checked in storage. |
| V. Invariants live in storage | **Pass.** "Delivered means a session took it" is a `CHECK` and a foreign key; `connected` is derived from `sessions` by trigger, and triggers refuse reopening a session or deleting an open one. |
| Agent and operator surfaces separate | **Pass.** Only `Agents` changes. `SurfaceBoundaryTests` grows by `HandBack` on `Broker`, deliberately; `AgentsCannotSeeTurnsTests` is unaffected. |
| §14: no `Detach` | **Pass.** `HandBack` returns work and says nothing about presence; a missing call loses nothing (FR-013). SPEC §4.0 says so beside the rule. |
| Nothing times out | **Pass.** Work comes back because a session closed, never because time passed. |

No violations; Complexity Tracking is empty.

## Project Structure

### Documentation (this feature)

```text
specs/027-session-owned-delivery/
├── spec.md
├── plan.md              # this file
├── research.md          # R1–R7: decisions and rejected alternatives
├── data-model.md        # sessions table, messages.delivered_session, the give-back query
├── quickstart.md        # the race as a test, and by hand
├── contracts/
│   ├── wire.md          # Attached.session, DrainInboxRequest.session, refusals
│   └── spec-text.md     # SPEC.md wording, section by section
└── checklists/requirements.md
```

### Source Code

```text
SPEC.md                                      §4.0, §6, §9, §10.1, §14, §18
proto/parley.proto                           Attached.session; AttachRequest.previous_session;
                                             DrainInboxRequest.session; HandBack; field 2 reserved
src/Parley.Core/ProtocolVersion.cs           0.8 → 0.9
src/Parley.Store/Migrations/001_initial.sql  sessions + four triggers; messages.delivered_session,
                                             delivered_horizon + checks
src/Parley.Store/SessionRepository.cs        new: open (with reclaim), close, close all on start, owner lookup
src/Parley.Store/AgentRepository.cs          MarkLive/MarkOffline removed; connected only via trigger
src/Parley.Store/MessageRepository.cs        MarkDelivered takes the session and horizon; HandBackAsync;
                                             GiveBackAsync (the newer-session query)
src/Parley.Store/ExchangeRepository.cs       GetAbandonedOwedByAsync removed
src/Parley.Broker/Broker.cs                  StartAsync opens a session (reclaiming if named) and returns its id;
                                             HandBackAsync; DrainInboxAsync takes the session and gives back
                                             first; redelivery loop removed
src/Parley.Broker/StartResult.cs             ExchangesRedelivered → Session
src/Parley.Server/BrokerRuntime.cs           sessions keyed by id; close all on start; resolve a session for a drain
src/Parley.Server/AgentsService.cs           Attached.session; previous_session; DrainInbox and HandBack check the
                                             session (contracts/wire.md)
src/Parley.Mcp/BrokerLink.cs                 keep the session; send it as previous_session; HandBack on dispose
src/Parley.Mcp/ParleyTools.cs                send the session on DrainInbox
src/Parley.Client/Seating/                   SeatHold, SeatVoice, SeatBook: the same for the person's surfaces
Directory.Build.props, CLAUDE.md             0.43.0, protocol 0.9

tests/Parley.Broker.Tests/SessionOwnedDeliveryTests.cs   new: the race, two windows, hand back, reclaim,
                                                         newer session, broker restart
tests/Parley.Server.Tests/FastRestartTests.cs            new: the race and a dropped stream over the wire;
                                                         refusals
tests/Parley.Mcp.Tests                                   BrokerLink re-attach names its session; dispose hands back
tests/Parley.Store.Tests                                 the checks and triggers, probed
tests/Parley.Broker.Tests (Attach/Inbox/Lifecycle/...)   ExchangesRedelivered assertions rewritten as collections
```

**Structure Decision**: no new project. The change follows the existing layers: storage holds the
invariant, `Broker` holds the rule, the server carries the session, adapters echo it.

## Order of work

1. **SPEC.md first**: §4.0, §9 DDL, §6, §10.1, §14, §18. Run `Parley.Spec.Tests`.
2. **Proto and protocol version.** Build fails in every caller; that is the checklist.
3. **Storage**: `sessions`, triggers, the delivery columns, the three queries. Store tests probe each
   check by trying to break it (a delivery without a session; reopening a closed session; deleting an
   open one; a session for a retired agent).
4. **Broker**: write the race test first and watch it fail on today's code; then each rule, test first.
5. **Server and adapters**: carry the session; reclaim on re-attach; `HandBack` on shutdown;
   refusals; the wire-level tests. Measure whether the editor ends the adapter cleanly (research R11).
6. **Rewrite the `ExchangesRedelivered` tests** as "the next collection returns it". Each keeps its
   scenario.
7. Version bump, CLAUDE.md state line, full suite, Linux in podman, CI 3× green.

## Risks

- **A collection path that holds no Attach stream.** R4 found two (MCP, SeatVoice), both attached.
  If a third turns up, the refusal surfaces it at once instead of silently creating orphans.
- **Store tests that write `connected` directly.** They now go through sessions; a test seeding
  `connected = 1` without a row will read wrong. The `CHECK` and the trigger make this loud.
- **026 also claims protocol 0.9.** Resolved by merge order (research R6).
- **Accepted residual** (research R10): a window opened after the work was collected, collecting while
  the holder's connection is down, can take it. **Accepted limit**: a crash beside an older window
  waits for §6's stall flag, as today.
- **Session rows accumulate** while messages refer to them. They go when retention prunes the
  messages; bounded by the number of editor opens, not by traffic.

## Complexity Tracking

None.
