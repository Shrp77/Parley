# Implementation Plan: Sub-conversations and moving exchanges

**Branch**: `032-sub-conversations-and-moves` | **Date**: 2026-11-03 | **Spec**: [spec.md](spec.md)

## Summary

Three operator operations, all on the `Operators` service: `spin_off` records a parent link for a new conversation, `preview_move` says what a move would take, and `move_exchanges` does it in one write transaction. Agents get one new thing: a move notice in their next collection. Storage gets migration 006. The terminal and the web console both get the two acts, and every conversation list says what is a spin-off.

Version 0.52.0, protocol 0.12, schema 6.

## Technical context

C# on .NET 10, SQLite through Dapper, gRPC, Razor Pages, Terminal.Gui v2, xUnit v3. No new package. Nothing about a move scans more than the exchanges moved and the two conversations.

## Constitution check

- **I. The spec is the product**: `SPEC.md` changes in the same work (T001). The rule that nothing can move an exchange is replaced, not excepted.
- **II. No agent has authority over another**: only the person moves or links. The operations are on `Operators`. `SurfaceBoundaryTests` keeps them off `Broker`, and `BrokerV1Tests.An_agent_cannot_reach_the_operator_surface` off `Agents`.
- **III. Identity from the launch**: `moved_by` is the person's seat on the project, which the broker fills. No operation takes a name for who acted.
- **IV. Terseness**: a move notice holds an exchange number and two conversation names, each already bounded at 64 characters. A note about files is a fixed sentence the adapter writes. Nothing an agent wrote crosses in a notice.
- **Nothing times out, no network bind, credentials**: untouched.
- **Message payloads are inert**: a quote is the person's words by the person's act, sent through the ordinary Request. What opens in the box comes from the record of a named message, never from a link's text (FR-004).

## Decisions

See [research.md](research.md). The ones that shape the work:

1. **The link is recorded before the send, not after.** `spin_off` creates the conversation and its link in one transaction, and only then is the Request sent. A Request therefore can never be delivered unlinked. If the Request reaches nobody, `abandon_spin_off` takes the link and the empty conversation back, so no record says something was started when nothing was said, and the name stays free.
2. **The move is one transaction with foreign keys deferred.** Update `exchanges`, `messages`, `conversation_cycles` and `message_lengths`, reconcile participants, void polls, write records and notices. A miss fails at commit and rolls everything back.
3. **Refusals live in the schema where they can.** A new trigger on `UPDATE OF conversation_id ON exchanges` refuses closed destinations, roadmap conversations in either direction, `owns-` names in either direction, and any exchange an ownership question hangs from. The broker's checks exist to give a sentence; the trigger is what holds.
4. **Whole groups are enforced at the broker, not just drawn.** `move_exchanges` refuses a list that leaves part of a `request_group` behind. `preview_move` adds the rest and says so. A send to several agents has no group, because it is only drawn as one entry, so a surface passes every exchange of what was picked.
5. **Move records and links hold names and numbers, and outlive either side.** No foreign keys to `conversations`. Retention and destroy leave them, as `destroyed_conversations` is left.
6. **A moved exchange counts as activity for retention.** `moved_utc` joins the idle rule, so entries moved into a new conversation are not pruned on the next pass. `created_utc` itself never changes.
7. **Move notices follow `roadmap_notices`**: their own table and `delivered_utc`, one row per agent per exchange, collected and acknowledged as the other notices are. They do not count toward waiting and never reach a wake.
8. **`Spin off` and `Move` are separate pages on the web, and separate files on the terminal.** Each is the one place its operation is called, with its door. The web's per-message tick boxes are an enhancement over a page that works without script.
9. **The list's wording is a display.** `ConversationSummary` gains `Parent`; `row.Id` stays exact because `Closing` and `Destroying` compare typed names to it. A conversation name cannot hold a space (§9.2), so ` - spinoff from ` cannot be forged by a name, on either surface.

## Project structure

```
proto/parley.proto                         SpinOff, PreviewMove, MoveExchanges; parent, links, moves; move notices
src/Parley.Store/Migrations/006_conversation_moves.sql
src/Parley.Store/ConversationMoveRepository.cs   the move and the link, in one place
src/Parley.Store/ConversationRepository.cs       list and export read parent, moves; list keeps an emptied source
src/Parley.Store/Pruner.cs                       the idle rule reads moves
src/Parley.Core/ConversationMove.cs              MoveRecord, ConversationLinks, MovePreview, MoveOutcome, MoveNotice
src/Parley.Broker/OperatorConsole.cs             delegates
src/Parley.Broker/Broker.cs, Inbox.cs, InboxHolds.cs    notices at collection
src/Parley.Server/OperatorsService.cs, AgentsService.cs
src/Parley.Mcp/ParleyTools.cs                    move_notices in parley_inbox
src/Parley.Client/BrokerClient.cs, Quoting.cs, Rendering/Reading.cs, Views/ConversationsView.cs
src/Parley.Web/Pages/Conversations/SpinOff.cshtml(.cs), Move.cshtml(.cs), _Side, _Conversation, Detail
src/Parley.Web/Pages/Projects/Agents.cshtml      the list's wording
src/Parley.Terminal/SpinningOff.cs, Moving.cs, ShellCommands.cs, Shell.cs, ConversationsRegion.cs, FilesPlacing.cs
src/Parley.Cli/Program.cs                        refuses both verbs, naming the terminal
SPEC.md, docs/operating.md, CLAUDE.md, plugins/parley prose
```

## Risks to hold with tests

- A source emptied of everything still lists (`ListAsync`), and reads as holding only its notes.
- `conversation_cycles` follows the exchange, or a cycle flag names the wrong conversation.
- A split settled pair still says what settled what, on both sides, naming the other conversation.
- An agent sending during a move, from another connection: the message lands in one conversation, never lost.
- A move starts no turn: `NudgeAsync` and channel nudges see nothing.
- The FR-002 tick: browser memory of a conversation must not tick others on the spin-off page. A page of its own avoids that memory.
- `SeatHold.Delivered` keeps a moved message's old conversation name in memory. It is not forgotten, because the seat would then lose the Result it owes; a surface draws the old name until the next read (recorded, low).
