# Implementation Plan: A Request is the ask

**Branch**: written on `015-file-store` (which carries uncommitted 0.28.x work); the branch this ships on is the owner's call
**Date**: 2026-09-17 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `specs/016-request-is-the-ask/spec.md`

## Summary

A Request becomes the conversation it belongs to and the ask. `intent`, `items`, item ids, item kinds, gates and
`done_when` leave the protocol; a Result answers the exchange whole. Clarify, which already exists, is where a
receiver goes when an ask is unclear or unbounded.

The approach rests on one thing already built: **a Request whose items a Result cannot name is already answered as a
whole, under the item `request`** (`PayloadShape.WholeRequest`, `Broker.cs:495-505`). That path exists for questions
carried forward from 0.4. A new Request carries no items at all, so `PayloadShape.ItemIds` returns nothing and the
existing machinery asks for exactly one outcome, named `request`. The result tables, the exchange's rolled-up
outcome, the flags and the health reports all keep working with no new vocabulary.

Storage is where the old shape is enforced, so storage is where the change is: migration 011 replaces
`tr_request_is_well_formed` with one that holds a new Request to having an ask, and leaves every stored message
untouched. Nothing rewrites history.

## Technical Context

**Language/Version**: C# 13 / .NET 10
**Primary Dependencies**: Dapper + Microsoft.Data.Sqlite (store), Grpc.AspNetCore (transport), ModelContextProtocol (adapter), Terminal.Gui v2 (terminal), ASP.NET Core Razor Pages (web UI)
**Storage**: SQLite, schema 10 today; this adds schema 11
**Testing**: xUnit v3 on Microsoft.Testing.Platform; probes over a real loopback broker in `Parley.Server.Tests`; SPEC.md's own fragments executed by `Parley.Spec.Tests`
**Target Platform**: Windows, Linux and macOS; one broker process per store
**Project Type**: protocol, with a reference implementation across a broker, an adapter and two operator surfaces
**Performance Goals**: unchanged; this removes work rather than adding it
**Constraints**: rule 8.4's 8,000-character cap is unchanged and stays in storage; every stored message is immutable
**Scale/Scope**: protocol 0.7. SPEC.md §5.1, §5.3, §7.2, §8 and §9.2/§9.3; one migration; the adapter's two tools and its standing instructions; both composers and every transcript; the README, the plugin and CLAUDE.md

## Constitution Check

*GATE: checked before Phase 0 and again after Phase 1.*

| Principle | Verdict | Why |
|---|---|---|
| I. The specification is the product | **Pass, and it leads** | SPEC.md is rewritten first and the code follows, as 0.5 was done. The version moves because a second implementation would observe the change. |
| II. No agent has authority over another | **Pass** | Nothing changes about refusal: `blocked` keeps its blocker and its trail. A receiver that needs scope asks rather than being told to guess, which is more of II rather than less. |
| III. Identity is fixed by the launch | **Pass** | No operation gains a sender. |
| IV. Terseness is a security control | **Pass** | The cap is unchanged. What crosses is strictly less: one text where four were. |
| V. Invariants live in storage | **Pass, with care** | The invariants that remain — the cap, a Result answering its own open exchange, sources naming registered agents, a blocker on `blocked` — stay in storage. The ones being removed are removed from storage too, rather than left enforced and worked around in code, which is the failure V exists to prevent. |

**Post-Phase-1 re-check**: unchanged. The one judgement call is R2's, and it keeps storage the enforcer.

## Project Structure

### Documentation (this feature)

```text
specs/016-request-is-the-ask/
├── spec.md
├── plan.md              # this file
├── research.md          # the decisions, with what was rejected
├── data-model.md        # the payload shapes and the schema change
├── contracts/
│   ├── payloads.md      # Request and Result, before and after
│   └── surfaces.md      # what each composer asks for
├── quickstart.md        # the walk that proves it
└── checklists/requirements.md
```

### Source (the files this touches)

```text
SPEC.md                        §5.1, §5.3, §7.2, §8 (rules and the enforcement table), §9.2, §9.3, §10.1, §13, §18
proto/parley.proto             Request and Result messages; protocol version comment
src/Parley.Core/               PayloadShape, MessageLimits, MessageIdentity, TrustEnvelope, Outcome, ProtocolVersion
src/Parley.Store/              Migrations/011_request_is_the_ask.sql; MessageRepository if it reads the removed fields
src/Parley.Broker/             Broker.cs's item derivation (already correct; its comments are not)
src/Parley.Server/             AgentsService request/result validation
src/Parley.Mcp/                ParleyTools (parley_request, parley_result, RequestItem), ParleyInstructions
src/Parley.Client/             Framing, Payload, Reading, SeatVoice, Transcript
src/Parley.Terminal/           Participation, Shell's composer and result form, TranscriptRegion
src/Parley.Web/                Conversations/Detail, _Conversation, Inbox
README.md, plugins/parley/**, CLAUDE.md ("Surveying the peers")
```

## Phase 0: Research

See [research.md](./research.md). Ten decisions; the three that shape everything else:

- **R1**: a new Request's payload is `{"type": "request", "ask": "…"}` — a flat field, no items array.
- **R2**: migration 011 replaces the two shape triggers and rewrites nothing; old rows keep their fields and their
  readers.
- **R3**: a Result keeps `outcomes` as an array and carries exactly one entry, `id: "request"`, which is the
  spelling the broker already requires when a Request has no readable items.

## Phase 1: Design

- [data-model.md](./data-model.md) — the two payloads, what storage holds to, and what migration 011 does.
- [contracts/payloads.md](./contracts/payloads.md) — Request and Result on the wire and in the store, 0.6 beside 0.7.
- [contracts/surfaces.md](./contracts/surfaces.md) — every composer, what it asks for now, and what it asks for after.
- [quickstart.md](./quickstart.md) — the walk: a person's two-field request, an agent's two-argument request, a
  Clarify settling scope, and a 0.6 conversation still reading.

## Phase 2: Tasks

Not produced here. `/speckit-tasks` writes `tasks.md`.

## Risks

- **The one that costs a deployment**: migrating the owner's store. Mitigated by migration 011 touching no row —
  it replaces triggers only — and by a test that builds a schema-10 store holding 0.5 and 0.6 messages, migrates it,
  and reads every one back.
- **A reader that assumes items.** Six renderers walk `items`/`outcomes`. Each must show a 0.6 message whole and a
  0.7 message plainly. `Parley.Client.Tests` and the terminal's transcript tests cover both shapes.
- **Half a removal.** A field removed from the tool but left in an instruction, a placeholder or the plugin's prose
  teaches the old shape to every new agent. SC-004's search is the check, and it belongs in the tasks.
