# Data model: Agents read the exchange history

One table. Nothing else in the store changes; a message, an exchange and a conversation are read as they are.

## `history_reads` (migration 009)

| Column | Type | Rule |
|---|---|---|
| `id` | INTEGER PRIMARY KEY | in the order reads happened |
| `reader` | TEXT NOT NULL | an agent's full name, `role@machine`, a foreign key to `agents(name)` with `ON DELETE CASCADE`; never the person seat or the facilitator |
| `kind` | TEXT NOT NULL | `exchange` or `search` |
| `exchange_id` | INTEGER | set for `exchange`, NULL for `search`; not a foreign key, so an exchange destroyed later keeps its line |
| `words` | TEXT | set for `search`, NULL for `exchange`; at most 200 characters, no line-breaking characters (the character class of a conversation title) |
| `matches` | INTEGER | set for `search` (the total, not the page), NULL for `exchange` |
| `created_utc` | TEXT NOT NULL | round-trip, as every timestamp |

Checks: `kind` is one of the two; `exchange_id` is not null exactly when `kind = 'exchange'`; `words` and `matches` are not null exactly
when `kind = 'search'`; `length(words) <= 200`; `words` has none of the line-breaking characters the conversation-name class forbids.
Indexes: `(exchange_id)` for the exchange page, `(reader, id)` for an agent's page.

It is written in the same transaction as the read. A read that cannot record is refused, so a read never goes unseen.
It has no trigger. Destroying a conversation does not touch it. It is removed by retention at the age cap, by the reader's cascade on a purge, and an agent keeps only its newest 2,000 rows. The person's view groups it: one entry for each reader of each exchange with a count, and one line for each agent that searched.

## Values returned (not stored)

- **HistoryMessage**: a message's id, exchange id, type, from, to, conversation, created time, and its envelope (`WrapHistory`).
- **ExchangeHistory**: the exchange id, its conversation, its state and outcome, its messages in order, `after` for the next call and
  `messages_remaining`; or no such exchange.
- **HistoryMatch**: exchange id, message id, conversation, from, to, type, created time, snippet.
- **HistorySearchPage**: up to 20 matches newest first, `not_shown`, `continue_after`.

## State transitions

None. A read moves nothing.
