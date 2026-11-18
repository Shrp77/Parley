-- Migration 009. Upgrades a schema 8 store in place and keeps every row (SPEC.md 9.2).
--
-- What the person sees of an agent reading the project's past (SPEC.md 5.8, 10.1). An agent may read an exchange by its
-- number or search the history for words; each call leaves one row here, written in the transaction of the read, and the
-- person reads them where they already read the exchange. Never read by an agent. It holds no message text.
--
-- `words` is bounded and has none of the control or line-separating characters. This check is a floor: the broker refuses a
-- wider set (every control, format, invisible, line-separating and malformed character) before anything is stored.

CREATE TABLE history_reads (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  reader       TEXT    NOT NULL REFERENCES agents(name) ON DELETE CASCADE,
  kind         TEXT    NOT NULL CHECK (kind IN ('exchange', 'search')),
  exchange_id  INTEGER,
  words        TEXT    CHECK (words IS NULL OR (length(words) BETWEEN 1 AND 200
                         AND words NOT GLOB '*[' ||
                         char(1) || '-' || char(31) || char(127) || '-' || char(159) || char(173) || '-' || char(173) ||
                         char(8203) || '-' || char(8207) || char(8232) || '-' || char(8238) ||
                         char(8288) || '-' || char(8292) || char(8294) || '-' || char(8303) ||
                         char(65279) || '-' || char(65279) || ']*')),
  matches      INTEGER CHECK (matches IS NULL OR matches >= 0),
  created_utc  TEXT    NOT NULL CHECK (unixepoch(created_utc) IS NOT NULL),
  CHECK ((kind = 'exchange' AND exchange_id IS NOT NULL AND words IS NULL AND matches IS NULL)
      OR (kind = 'search'   AND exchange_id IS NULL     AND words IS NOT NULL AND matches IS NOT NULL))
) STRICT;

CREATE INDEX ix_history_reads_exchange ON history_reads(exchange_id);
CREATE INDEX ix_history_reads_reader ON history_reads(reader, id);
