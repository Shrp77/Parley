# Data model: Work comes back when the session that took it is gone

Schema 1, edited in place (nothing is released). Every fragment here goes into SPEC.md §9 first and is
executed by `Parley.Spec.Tests` in the order written.

## New table: `sessions`

One row per session, open or closed (research R2). Inserted when an Attach stream opens, marked closed
when it ends, and deleted once no message refers to it.

```sql
CREATE TABLE sessions (
  -- Order of opening, never reused (AUTOINCREMENT), so "opened after" survives a broker restart.
  seq         INTEGER PRIMARY KEY AUTOINCREMENT,
  id          TEXT NOT NULL UNIQUE,
  agent       TEXT NOT NULL REFERENCES agents(name),
  -- Its place in line: the place of the session it reclaimed, or NULL for its own seq (R9, R10).
  place       INTEGER,
  opened_utc  TEXT NOT NULL,
  closed_utc  TEXT,

  CHECK (length(id) = 32 AND id NOT GLOB '*[^0-9a-f]*'),
  CHECK (place IS NULL OR place < seq),
  CHECK (unixepoch(opened_utc) IS NOT NULL),
  CHECK (closed_utc IS NULL OR unixepoch(closed_utc) IS NOT NULL)
) STRICT;

CREATE INDEX ix_sessions_agent_open ON sessions(agent) WHERE closed_utc IS NULL;
```

**`agents.connected` is derived in storage** from these rows, so the count and the rows cannot
disagree:

```sql
CREATE TRIGGER sessions_opened AFTER INSERT ON sessions
BEGIN
  UPDATE agents SET connected = connected + 1, changed_utc = NEW.opened_utc WHERE name = NEW.agent;
END;

CREATE TRIGGER sessions_closed AFTER UPDATE OF closed_utc ON sessions
  WHEN OLD.closed_utc IS NULL AND NEW.closed_utc IS NOT NULL
BEGIN
  UPDATE agents SET connected = connected - 1, changed_utc = NEW.closed_utc WHERE name = NEW.agent;
END;

-- A session is closed once. Reopening one would raise connected with no stream behind it.
CREATE TRIGGER sessions_stay_closed BEFORE UPDATE OF closed_utc ON sessions
  WHEN OLD.closed_utc IS NOT NULL
BEGIN
  SELECT RAISE(ABORT, 'a closed session stays closed');
END;

-- Deleting an open session would leave connected counting it.
CREATE TRIGGER sessions_delete_closed_only BEFORE DELETE ON sessions
  WHEN OLD.closed_utc IS NULL
BEGIN
  SELECT RAISE(ABORT, 'close a session before deleting it');
END;
```

- **Retired agents get no session.** The insert is `INSERT ... SELECT ... WHERE status = 'active'`,
  keeping today's rule that marking live declines a retired agent.
- **Broker start** sets `closed_utc` on every open row (the triggers bring `connected` to 0), then
  deletes closed rows no message refers to. Nothing else writes `connected`.
- **Closing a session** also deletes it if no message refers to it, in the same transaction.

## Changed table: `messages`

```sql
  delivered_utc      TEXT,
  -- Which session collected it (R1).
  delivered_session  TEXT REFERENCES sessions(id),
  -- The highest sessions.seq when it was delivered. A session whose place is above it opened
  -- after this delivery (R10).
  delivered_horizon  INTEGER,

  CHECK ((delivered_utc IS NULL) = (delivered_session IS NULL)),
  CHECK ((delivered_session IS NULL) = (delivered_horizon IS NULL)),
```

A message is never delivered without a session and a horizon, and giving it back clears all three.
Pruning a message is what lets its session row go.

`delivered_horizon` at delivery is `(SELECT max(seq) FROM sessions)`. A session open at delivery has a
`seq` no higher than that; any session opened later has a higher one, because `AUTOINCREMENT` never
reuses a number even when the highest row was deleted.

## The latest owed message

Every rule below acts on the same set: for each open exchange the agent owes a move on, the newest
message to the agent. It is today's `GetLatestOwedAsync`, so messages already dealt with and the
agent's own are never handed back.

```sql
-- latest_owed(@Agent)
SELECT (SELECT m.id FROM messages m
         WHERE m.exchange_id = e.id AND m.to_agent = @Agent
         ORDER BY m.id DESC LIMIT 1)
  FROM exchanges e
 WHERE e.state = 'OPEN' AND e.owed_by = @Agent
```

## Controlled end: `HandBack` (R8)

```sql
UPDATE messages
   SET delivered_utc = NULL, delivered_session = NULL, delivered_horizon = NULL
 WHERE id IN (/* latest_owed(@Agent) */)
   AND delivered_session = @Session;
```

Checked first: `@Session` exists and belongs to `@Agent` (open or closed).

## Reclaim on attach (R9)

In the transaction that inserts the new session N, when `previous_session` P is given:

```sql
-- Only if P belongs to the attaching agent; otherwise N opens fresh.
UPDATE sessions SET place = (SELECT coalesce(place, seq) FROM sessions WHERE id = @P)
 WHERE id = @N;

UPDATE messages SET delivered_session = @N
 WHERE delivered_session = @P AND to_agent = @Agent;
```

The horizon is left as it was: N now holds the work, so the newer-session rule never applies to it
while N is open.

## Give-back at collection (R10)

At the start of each collection by session C, for C's agent:

```sql
UPDATE messages
   SET delivered_utc = NULL, delivered_session = NULL, delivered_horizon = NULL
 WHERE id IN (/* latest_owed(@Agent) */)
   AND delivered_session IN (SELECT id FROM sessions WHERE closed_utc IS NOT NULL)
   AND delivered_horizon < (SELECT coalesce(place, seq) FROM sessions WHERE id = @C);
```

- **Only deliveries to a closed session.** An open session, the collector included, keeps its work
  (FR-003).
- **Only to a newer collector.** A window open when the work was collected never takes it (FR-011).
- **Re-delivery, not re-sending** (§14): the row is reused; nothing is written to the log.

## Removed

- `ExchangeRepository.GetAbandonedOwedByAsync` and the redelivery loop in `Broker.StartAsync`.
- `StartResult.ExchangesRedelivered` and `Attached.exchanges_redelivered`.
- `agents.session_ref` and `AttachRequest.session_ref` if nothing else reads them (checked in tasks);
  two "session" ideas in one schema invite the wrong one being used.

## One message, through each kind of end

| Event | Columns after |
|---|---|
| Written | all three delivery columns null |
| Collected by session S | `delivered_utc`, `delivered_session = S`, `delivered_horizon = max(seq)` |
| S hands back (controlled end) | all three null: next collection anywhere receives it |
| S's stream drops, process reconnects as S′ naming S | `delivered_session = S′`; not delivered again |
| S's stream drops, a session opened earlier than the collection collects | unchanged |
| S's stream drops, a session opened later than the collection collects | cleared, then delivered to that session |
| Broker restarts | S closed at start; then as the two rows above, by who reconnects and how |
