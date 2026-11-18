-- Migration 009. Upgrades a schema 8 store in place and keeps every row (SPEC.md 9.2).
--
-- What the person sees of an agent reading the project's past (SPEC.md 5.8, 10.1). An agent may read an exchange by its
-- number or search the history for words; each call is counted here, in the transaction of the read, and the person reads
-- the tally where they already read the exchange. Never read by an agent. It holds no message text.
--
-- One row for each agent and each exchange it read, and one for each agent's searches, with how many times and when the first
-- and the newest was. A tally and not a log: its size is bounded by the agents and the exchanges whatever one agent reads, so no
-- cap is needed, no agent can push its own reads out of it by making many, and the count shown is the count.
--
-- `words` is the newest search's words. It is bounded and has none of the control or line-separating characters. This check is a
-- floor: the broker refuses a wider set (every control, format, invisible, line-separating and malformed character) before
-- anything is stored.

CREATE TABLE history_reads (
  reader       TEXT    NOT NULL REFERENCES agents(name) ON DELETE CASCADE,
  kind         TEXT    NOT NULL CHECK (kind IN ('exchange', 'search')),
  exchange_id  INTEGER NOT NULL CHECK (exchange_id >= 0),
  words        TEXT    CHECK (words IS NULL OR (length(words) BETWEEN 1 AND 200
                         AND words NOT GLOB '*[' ||
                         char(1) || '-' || char(31) || char(127) || '-' || char(159) || char(173) || '-' || char(173) ||
                         char(8203) || '-' || char(8207) || char(8232) || '-' || char(8238) ||
                         char(8288) || '-' || char(8292) || char(8294) || '-' || char(8303) ||
                         char(65279) || '-' || char(65279) || ']*')),
  matches      INTEGER CHECK (matches IS NULL OR matches >= 0),
  total        INTEGER NOT NULL CHECK (total >= 1),
  first_utc    TEXT    NOT NULL CHECK (unixepoch(first_utc) IS NOT NULL),
  last_utc     TEXT    NOT NULL CHECK (unixepoch(last_utc) IS NOT NULL),
  PRIMARY KEY (reader, kind, exchange_id),
  CHECK ((kind = 'exchange' AND exchange_id > 0 AND words IS NULL AND matches IS NULL)
      OR (kind = 'search'   AND exchange_id = 0 AND words IS NOT NULL AND matches IS NOT NULL))
) STRICT;

CREATE INDEX ix_history_reads_exchange ON history_reads(exchange_id);
CREATE INDEX ix_history_reads_last ON history_reads(last_utc);
