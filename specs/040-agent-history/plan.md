# Implementation Plan: Agents read the exchange history

**Branch**: built on `035-material-3-web-ui` | **Date**: 2026-11-17 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `specs/040-agent-history/spec.md`

## Summary

Two new agent operations, `read_exchange(exchange_id, after?)` and `search_history(words, ...)`, on the Agents service, the
`Broker`, the MCP adapter and the plugin's prose. They read the project's messages (a store is one project, so no read can
cross a project), return them inside a **history envelope** built from the §13 envelope, change nothing a participant can
observe, and keep a **tally of reads** (one row for each agent and exchange, and one for each agent's searches) that only the person sees, on the exchange page of the web console and
in the terminal. A schema migration adds the record. SPEC.md §10.1, principle 2.2, §17, §13, the constitution's boundary
paragraph, the docs and the tests that held the old rule change in the same commit.

## Technical Context

**Language/Version**: C# on .NET 10, as the rest of the repository
**Primary Dependencies**: none new. SQLite through Dapper; gRPC from `proto/parley.proto`; ModelContextProtocol in `Parley.Mcp`
**Storage**: SQLite, one store per project. New migration `009_history_reads.sql` (see Decisions 7)
**Testing**: xUnit v3 on Microsoft.Testing.Platform; `scripts/test.ps1`; unit tests in Store, Broker, Core, Mcp; whole-exchange
tests in Server; page tests in Web; row and dialog tests in Terminal
**Target Platform**: Windows, Linux and macOS, as CI
**Project Type**: multi-project .NET solution with a protocol specification
**Performance Goals**: a search over a store of 100,000 messages returns in under 2 seconds on the maintainer's machine; a read of one
exchange in under 100 ms
**Constraints**: a reply bounded like an inbox collection (about 64,000 characters, never fewer than one message); a search
page at most 20 matches; a snippet at most 240 characters; a rate and total size per caller (Decisions 6); no read changes delivery,
turns, debts, participants or state
**Scale/Scope**: a live store holds on the order of thousands of messages today (the largest conversation has 209 messages)

## Constitution Check

The constitution says a change that contradicts a premise is a design change and must be argued. This one contradicts two
statements and is argued here and in the spec.

| Premise | Effect | Handling |
|---|---|---|
| I. The specification is the product | A protocol change | Spec first: §10.1, §13, §2.2, §17 are edited in the same change as the code, and `Parley.Spec.Tests` hold them |
| II. No agent has authority over another | None. A read gives no power over anybody | Stated in §10.1: history is read-only and addresses nobody |
| II. A roadmap is the person's | Untouched | A roadmap is not an exchange; history returns messages only (FR-011) |
| III. Identity fixed by launch | None | No sender argument; the reader is the attached caller |
| IV. Terseness is a security control | New text crosses | Bounds on a reply, a page, a snippet, a search term, and a rate (Decisions 3 and 6). **A recorded reduction**: an agent may now read text it was never sent, so its context can hold more of other agents' words than before; the bounds are the control, and reads are recorded to the person (FR-016) |
| "Boundaries That Do Not Move": never conversation history | **Contradicted** | The constitution paragraph is amended by the owner's decision of 2026-11-17: history crosses only as a bounded, framed, recorded read, never pushed and never poured into another agent's context by Parley |
| Context poisoning | The old rule gave this for free | Replaced by the history envelope (Decisions 2): provenance from the registry, a delimiter token per delivery, marked as history, addressing nobody |

Re-checked after design: no further violation. The amendment to the constitution is a task, not an assumption.

## Decisions

Detail and alternatives are in [research.md](research.md).

1. **Two operations, no more.** `read_exchange` and `search_history`. No list of exchanges, no read by conversation (an
   exchange's reply names its conversation, and a search may be narrowed to one).
2. **A history envelope.** `TrustEnvelope.WrapHistory`: the same sender attribution as `Wrap` (participant, broker notice,
   facilitator, person's reply), a fresh token per delivery, and a first line saying this is a record of a past message, not mail
   and not a request. No field addresses the reader.
3. **Bounds.** A read reply carries messages in order until about 64,000 characters, never fewer than one, then says how many
   remain and the id to continue after. A search page is at most 20 matches, newest first, snippet at most 240 characters around
   the first match, with the count of matches not shown and a continue token. A search term is at most 200 characters.
4. **Search is literal and word-wise.** The term is split on whitespace; every word must occur, case-insensitively, in the
   message's readable text, which is the string values in its payload read with `json_tree`. Each word is matched with `instr`,
   never as `LIKE` or `MATCH` syntax, so a term made of operators is plain text. No stemming, ranking or fuzzy.
5. **Filters** on a search: `agent` (sender or recipient), `conversation`, `type`, `since`, `until`.
6. **Rate and size.** Per agent name, held for the store in memory (not stored, and not reset by attaching again): at most 30 history
   calls and 400,000 characters per rolling minute, a reply counted by its envelopes and a search by the time it took (100 characters a
   millisecond, never less than 20,000); a call over either is refused with how long to wait. This bounds copying the store without a table.
7. **Schema.** Migration **009** `history_reads`; schema version 9. The unbuilt feature 027 had been told to use 009; it moved to
   010 and then to 011, because 010 became the message indexes (CLAUDE.md is changed). A store already at 9 would otherwise skip 027's migration, because a migration at or below the
   recorded version is skipped.
8. **The record, a tally.** One row for each agent and each exchange it read and one for each agent's searches: total, first and last
   time, and for searches the newest words and matches. Upserted in the transaction of the read, only for a search that ran and an
   exchange that exists (ids are never reused, so a row for one that does not exist would later be shown against another exchange).
   Bounded by agents and exchanges, so there is no cap to evict evidence or understate a count. Never returned to any agent. Removed
   by retention when an agent has not added to it for the age cap, and by the reader's cascade.
9. **Shown to the person.** Beneath an exchange's first message the web console, the terminal and the text export say "Read by `x`
   at t (n times)", one entry for each reader so none can crowd another out; one line for each reader that searched (how many
   searches, the newest words) is shown beside each conversation, since a search belongs to no one exchange. No new operator
   operation: both ride on the existing operator read of a conversation (§10.2), which grows a `reads` field.
10. **Delivery is untouched.** Neither operation calls `MarkDelivered`, wakes, ticks or appends. A test compares the store and
    every inbox before and after (SC-003).
11. **Destroyed and pruned.** Destroying a conversation deletes its exchanges, and nothing records which numbers they were, so an
    exchange that is gone reads as no such exchange, the same as one that never existed. An exchange with some messages pruned reads
    with the messages that remain.

## Project Structure

### Documentation (this feature)

```text
specs/040-agent-history/
├── spec.md
├── plan.md              # this file
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── agents-history.proto.md   # the two rpcs and their messages
│   └── tools.md                  # the two MCP tools' arguments and replies
├── checklists/requirements.md
└── tasks.md             # /speckit-tasks
```

### Source Code (repository root)

```text
proto/parley.proto                          Agents: ReadExchange, SearchHistory; Operators: reads in the conversation view
SPEC.md                                     §2.2, §5 (history), §10.1, §13, §17, §9.2 schema, §16; protocol 0.19
.specify/memory/constitution.md             the "never conversation history" paragraph; version
src/Parley.Core/TrustEnvelope.cs            WrapHistory
src/Parley.Core/HistoryLimits.cs            the numbers, once
src/Parley.Store/Migrations/009_history_reads.sql
src/Parley.Store/HistoryRepository.cs       ReadExchangeAsync, SearchAsync, RecordAsync (a tally upsert), ReadsInConversationAsync
src/Parley.Broker/Broker.cs                 ReadExchangeAsync, SearchHistoryAsync (agent operations); rate window
src/Parley.Broker/HistoryRate.cs
src/Parley.Server/AgentsService.cs          the two rpcs
src/Parley.Mcp/ParleyTools.cs               parley_read_exchange, parley_search_history
src/Parley.Client + Web + Terminal          show reads to the person
plugins/parley                              prose: how an agent resumes from a number
docs/                                       operating.md, agents guide
tests/                                      Store, Broker, Core, Server, Mcp, Web, Terminal, Spec
```

**Structure Decision**: the existing layout; the only new files are a repository, a rate window, the limits, the envelope function and
the migration. Nothing crosses the dependency rules: the Store gains a repository, the Broker an agent operation, no client gains a route
to SQLite.

## Complexity Tracking

| Departure | Why | Simpler option rejected |
|---|---|---|
| A table the person reads, for a read | FR-016, the owner's decision | A counter: rejected by the owner |
| An in-memory rate window | Bounds copying without a row per call | A stored limit: a table and a clean-up for no extra safety |
| Scanning `json_tree` for search | No new dependency and exact semantics | FTS5: an index to keep in step with destroy, prune and move; recorded as the follow-up if a store grows past the goal |
