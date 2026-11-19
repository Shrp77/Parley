# Data model: Agents read the exchange history

One table. Nothing else in the store changes; a message, an exchange and a conversation are read as they are.

## `history_reads` (migration 009)

A tally and not a log: one row for each agent and each exchange it read, and one row for each agent's searches.

| Column | Type | Rule |
|---|---|---|
| `reader` | TEXT NOT NULL | an agent's full name, a foreign key to `agents(name)` with `ON DELETE CASCADE`; never the person seat or the facilitator |
| `kind` | TEXT NOT NULL | `exchange` or `search` |
| `exchange_id` | INTEGER NOT NULL | the exchange for `exchange` (greater than 0); 0 for `search`; not a foreign key |
| `words` | TEXT | the newest search's words, for `search` only; 1 to 200 characters with no control or line-separating character (a floor: the broker refuses a wider set first) |
| `matches` | INTEGER | the newest search's total matches, for `search` only |
| `total` | INTEGER NOT NULL | how many times, at least 1 |
| `first_utc`, `last_utc` | TEXT NOT NULL | when the first and the newest was, round-trip |

Primary key `(reader, kind, exchange_id)`. Indexes `(exchange_id)` and `(last_utc)`.

Written by an upsert in the same transaction as the read, only for an exchange that exists and a search that was run, so a number that
names nothing is counted nowhere. Its size is bounded by the agents and the exchanges however much one agent reads, so nothing is capped,
a count is the count, and the first read cannot be pushed out. It has no trigger. Destroying a conversation removes the rows of its exchanges in the same act (the person's erase: a record of what
was read of an exchange that is gone would outlive it). A row is
removed by retention when the agent has not added to it for the age cap, and by the reader's cascade on a purge.

## Values returned (not stored)

- **HistoryMessage**: a message's id, exchange id, type, from, to, conversation, created time, and its envelope (`WrapHistory`).
- **ExchangeHistory**: the exchange id, its conversation, its state and outcome, its messages in order, `after` for the next call and
  `messages_remaining`; or no such exchange.
- **HistoryMatch**: exchange id, message id, conversation, from, to, type, created time, snippet.
- **HistorySearchPage**: up to 20 matches newest first, `not_shown`, `continue_after`.

## State transitions

None. A read moves nothing.
