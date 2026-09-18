# Data model: A Request is the ask

Nothing is added to the schema. One migration replaces two triggers; every table, index and column stays as it is,
and no stored row changes.

## The stored payloads

| Message | 0.6 (still in the store, still read) | 0.7 (written from now on) |
|---|---|---|
| Request | `type`, `to`, `intent`, `items[]` (each `id`, `kind`, `text`, optional `gate`), `done_when` | `type`, `to`, `ask` |
| Result | `type`, `exchange_id`, `outcomes[]` (one per item: `id`, `outcome`, `text`, `sources[]`, `blocker`) | `type`, `exchange_id`, `outcomes[]` with exactly one entry, `id` = `request` |
| Clarify | unchanged | unchanged |

**Why the Result keeps its array** (research R3): `result_items` is keyed `(message_id, item)`, and the exchange's
rolled-up outcome, `parley results`, the flags and both health screens read those rows. `request` is already the id
the broker asks for when a Request has no readable items, and already what the broker writes when a person closes a
conversation.

## What storage holds a new message to

**Kept, and still in storage (Principle V):**

- The payload is valid JSON and within rule 8.4's cap (`messages` CHECK, migration 003).
- A Result belongs to an open exchange its sender received, and closes it (`tr_result_closes_its_own_exchange` and
  the exchange triggers).
- Every outcome is one of `done`, `answered`, `acknowledged`, `blocked`, `closed`; `blocked` carries a blocker and
  nothing else does (`result_items` CHECKs).
- Only the broker writes `closed` (`tr_result_is_well_formed`'s responder clause).
- Every source names a registered agent (`result_sources`).
- An item recorded against a Result is 1 to 64 characters (`result_items.item`), which `request` satisfies.

**Removed, from storage as well as from the protocol:**

- A `done_when` of at least twenty characters.
- An `items` array with at least one element.
- `kind` on every item, and its two permitted values.
- An `id` on every item, distinct, 1 to 64 characters.
- A `gate` naming an earlier item.

**Added:**

- A Request carries `ask`: text, non-empty after trimming. One clause, in the same trigger that held the five above.

## Migration 011 — `011_request_is_the_ask.sql`

```sql
DROP TRIGGER tr_request_is_well_formed;

CREATE TRIGGER tr_request_is_well_formed BEFORE INSERT ON messages
WHEN NEW.type = 'request' AND json_valid(NEW.payload_json)
BEGIN
  SELECT RAISE(ABORT, 'a request carries an ask')
   WHERE json_type(NEW.payload_json, '$.ask') IS NOT 'text'
      OR length(trim(json_extract(NEW.payload_json, '$.ask'))) = 0;
END;
```

`tr_result_is_well_formed` keeps its outcome clauses and loses nothing: it never named an item id. The `AFTER INSERT`
trigger `tr_result_recorded` is unchanged — it reads `$.outcomes`, which a 0.7 Result still has.

**No row is touched.** A message is never updated (§9), so a `BEFORE INSERT` trigger decides only what may be written
next. That is what keeps 0.5 and 0.6 conversations readable: they are not migrated, they are simply left alone.

## Reading both shapes

| Reader | 0.6 message | 0.7 message |
|---|---|---|
| `PayloadShape.ItemIds` | returns the ids (also `sub_questions`, for 0.4 rows carried forward) | returns nothing |
| `Broker` deciding what a Result must answer | the ids | one item, `request` |
| `Delivered.Items` (both surfaces) | the ids | one item, `request` |
| Transcript and export renderers | intent, each item with its kind and gate, `done_when` | the ask |
| Inbox renderers | as today | the ask |

A renderer that meets an unfamiliar field shows it as the sender's text, which is what they do now. Nothing infers a
missing field: a 0.7 Request has no intent, and a renderer must not write one.

## Entities

- **Request**: one ask, one conversation, one recipient. Opens an exchange. Immutable once written.
- **Result**: one outcome for the exchange, its text, its sources, and a blocker when blocked. Closes the exchange.
- **Clarify**: unchanged. Hands the turn back, and is how scope is settled.
