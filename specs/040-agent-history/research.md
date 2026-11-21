# Research: Agents read the exchange history

No NEEDS CLARIFICATION remained after the spec. These are the choices the plan rests on, with what was weighed.

## 1. How an agent's reads are framed

- **Decision**: a new `TrustEnvelope.WrapHistory`, built on the existing sender attribution, with a first line that says it is a
  record and not mail.
- **Rationale**: the whole defence of §13 is that provenance comes from the registry and a per-delivery token cannot be forged by a
  payload written earlier. A read delivers older payloads, so the same token rule applies: a fresh token each call, and the
  payload emitted as the JSON string it is.
- **Alternatives**: reuse `Wrap` unchanged (an agent could not tell a record from mail that asks something of it; rejected);
  strip the payload to plain text (loses the structure of an ask or a result and is no safer than JSON).

## 2. Search mechanism

- **Decision**: literal, case-insensitive, all-words match over the string values of the payload (`json_tree`), `instr` for each
  word. Scan, no index.
- **Rationale**: exact and predictable; no operator syntax to be injected into (`MATCH` and `LIKE` both have one); no extra
  structure to keep in step with moves, prune and destroy. At the sizes in use a scan is milliseconds to a few hundred ms.
- **Alternatives**: SQLite FTS5 (faster and ranked, but an index and triggers to maintain across `destroy`, `prune`, `move`, and a
  second migration; held back until a store is large); LIKE over `payload_json` (matches inside JSON escapes such as `\n` and `’`
  and finds nothing for a quoted phrase).
- **Snippet**: cut in C# from the first string value that matched, with the match inside it, at word-safe bounds, control
  characters neutralised one for one.

## 3. Reading one exchange

- **Decision**: all messages of the exchange in id order, from `after` (a message id) when continuing, to about 64,000 characters.
- **Rationale**: the same bound as the inbox, so a large exchange cannot flood a context, and `after` is a stable cursor since ids
  are given in the order messages are handled.
- **Alternatives**: page by count (a count says nothing about size); return only the latest message (does not recover the thread).

## 4. Whose exchanges

- **Decision**: every exchange in the store, which is the project.
- **Rationale**: the owner's decision. A store is one project, so crossing projects is impossible by construction, and an unknown
  number answers the same as one in a project the caller is not attached to.

## 5. A record the person sees

- **Decision**: table `history_reads`, a tally with one row for each agent and each exchange it read and one for each agent's searches,
  upserted with the read, shown to the person only.
- **Rationale**: the owner chose each read shown on the exchange. A row for each call let an agent evict its own earlier reads by making
  many, grew with every call and made counts understate once capped, so review replaced it with a tally: the reader, the kind, the exchange
  (or the newest words and matches), how many times and the first and newest times, never a message's text.
- **Alternatives**: a log with a cap (rejected in review); a counter per agent; nothing. The first two were offered and not chosen.

## 6. Rate limit

- **Decision**: in memory, held for the store and keyed by agent name, 30 calls and 400,000 characters per rolling minute; a search is
  charged by the time it took (100 characters a millisecond, never less than 20,000) because it scans the store under the broker's gate.
- **Rationale**: the aim is that the tool cannot copy a store in one sitting, not accounting. Held for the store because every attach
  makes a new broker object, and a rate kept there was reset by attaching again. A restart resets it, which is harmless. Numbers are the owner's to change and live once in `HistoryLimits`.

## 7. Migration number

- **Decision**: 009.
- **Rationale**: the migrator skips any migration at or below the recorded version. Feature 027 had reserved 009 but is not
  built; if this took 010 and a store reached 10, 027 at 009 would never run. So this takes 009 and 027 moves on (to 010, then to 011 when 010 became the message indexes).

## 8. What stays unreadable

Roadmaps and their text (principle 2.12), files' bytes (§9.5), turn information (`AgentsCannotSeeTurnsTests`), the registry's
workspace paths, and the history reads themselves.
