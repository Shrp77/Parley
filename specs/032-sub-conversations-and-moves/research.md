# Research: Sub-conversations and moving exchanges

## What storage does today

**Decision**: update in place with `PRAGMA defer_foreign_keys = ON`, no rebuild of `exchanges` or `messages`.
**Rationale**: the only thing refusing a move is the composite foreign key. No trigger fires on `UPDATE OF conversation_id`. Deferring is already how destroy and purge work, it resets at the end of the transaction, and a miss fails at commit. `content_hash` excludes the conversation, so deduplication is untouched, and `AppendAsync` reads the conversation from the exchange at write time, so replies already land right.
**Alternatives**: rebuilding `exchanges` with `ON UPDATE CASCADE`. Refused: several tables reference it, and cascading would make a move easy to do by accident in every code path, where the point is one door.

## Refusing a closed destination

**Decision**: `tr_a_moved_exchange_may_go_there`, `BEFORE UPDATE OF conversation_id ON exchanges`.
**Rationale**: `tr_closed_conversation_stays_closed` is `BEFORE INSERT` only, so nothing in storage would have stopped it, and §9.3 puts rules code could route around in the schema.

## Participants

**Decision**: reconcile rows in the move. Join every party to the destination. Remove a source row when the agent has no exchange left there and placed no file still there.
**Rationale**: participants are stored, and assignment reads the table, so §5.0's derived membership has to be made true here. `JoinAsync` already voids a destination poll; the source needs `VoidPollAsync`.
**Noted**: an agent whose placed file was since removed leaves no evidence, so it stops being a participant. Nothing it could do depended on that row but continuing by name, and §5.0's rule lets it start anew.

## Length records

**Decision**: move the rows of messages still held, giving them the destination's next positions in order. Rows of already-pruned messages have no exchange to trace, so stay under the source.
**Rationale**: `message_lengths` has no `exchange_id`, and the numbers exist to judge limits (§17 question 16). Position is "how far into a conversation", which a moved message now is. No row is lost.

## Retention

**Decision**: `activity` in the idle rule takes the latest of a conversation's messages and of any move in or out.
**Rationale**: FR-011 keeps `created_utc`, so old finished exchanges moved to a new conversation would otherwise be released at the next pass, before the person had read them there. The age cap stays per message and unchanged.

## Roadmap and ownership conversations

**Decision**: a `roadmap_item_conversations` row refuses, removed item or not. A name beginning `owns-` refuses, and so does an exchange an `ownership_questions` row hangs from.
**Rationale**: the row outlives removal, and reading it either way is simpler than asking whether the item is live. Prefix recognition can be fooled only into refusing, which is the safe direction, and the question row is the firm one.

## Settled pairs

**Decision**: export carries `settled_elsewhere`: each exchange settled by, or settling, one in this conversation that is now in another, with that conversation's name. `Reading` uses it, so neither side's line disappears.
**Rationale**: `Reading.Settled` searches one export, so a split pair would silently lose the line. A new settle stays same-conversation: `GetSettleableAsync` and the trigger clause are left alone. An agent asked to settle across a split is refused as it would be for any unrelated exchange.

## Spin-off and the existing send path

**Decision**: `spin_off(parent, child)` then the ordinary `Request` naming `child`, on a page of its own.
**Rationale**: the person may name any open conversation at assignment, so the send path needs no change. Recording first means no send can be unlinked (see plan, decision 1). A new page means the conversation page's browser memory of ticks cannot apply, and the form's posted fields are the whole of what survives a refusal.
**Alternatives**: a `parent` field on `OpenRequest`. Refused: that is the `Agents` service, and an agent must not be able to set a link.

## Previews and the drift test

**Decision**: `PreviewMove`, `MoveExchanges` and `SpinOff` are public on `BrokerClient`, each bound on both surfaces, because `DriftTests` reflects them.

## Terminal picking

**Decision**: a `TickDialog` of the open conversation's exchanges, with the one under the cursor ticked.
**Rationale**: there is no multi-select in a transcript and the tick list already exists, with its keys in the table.
