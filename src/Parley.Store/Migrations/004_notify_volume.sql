-- What Notify actually costs, kept where retention cannot reach it.
--
-- SPEC.md §17 question 1 records that nothing bounds Notify and that the deliberate answer is to
-- measure before deciding whether anything should. This is the measurement.
--
-- It is a rollup rather than a row per message, and that is the difference between this and
-- message_referrals. A referral names a third agent, so which one it named is the fact worth
-- keeping and a counter cannot hold it. A notification's value is in its prose, which is pruned
-- with the message and should be -- so what survives is volume and size, and those roll up into
-- one row per pair without losing anything a decision needs.
--
-- Bounded by the roster rather than by traffic: nineteen agents is at most a few hundred rows,
-- forever. §16 says Parley is a channel and not a store, and a counter is not a log.

CREATE TABLE notify_volume (
    from_agent  TEXT    NOT NULL REFERENCES agents(name),
    to_agent    TEXT    NOT NULL REFERENCES agents(name),

    -- The two numbers the question actually turns on: how many, and how big.
    sent        INTEGER NOT NULL CHECK (sent > 0),
    characters  INTEGER NOT NULL CHECK (characters > 0),

    -- Kept because a mean hides the shape. Twenty short notices and one enormous one average out
    -- to something unremarkable, and they are completely different problems.
    largest     INTEGER NOT NULL CHECK (largest > 0),

    -- Both ends, because a rate needs a span. Twenty notifications in an hour and twenty over a
    -- month are the same count and opposite findings, and last_utc alone cannot tell them apart.
    first_utc   TEXT    NOT NULL,
    last_utc    TEXT    NOT NULL,

    PRIMARY KEY (from_agent, to_agent),

    CHECK (from_agent <> to_agent),

    -- The total cannot be smaller than its own largest member, nor smaller than one character
    -- per message. Both would mean the accumulation went wrong, which is invisible in a number
    -- that only ever grows.
    CHECK (characters >= largest),
    CHECK (characters >= sent)
) STRICT;

-- Accumulated by the schema, on §9.3's argument and for a second reason specific to this table:
-- the count is what a survey threshold reads, so a notification that was delivered but not
-- counted would move an agent's total without moving the number anyone acts on.
--
-- Length is measured on payload_json, which is what rule 8.5 caps, so the sizes here are directly
-- comparable to the limit they are being weighed against. The envelope §13 wraps around a
-- delivered message is broker-authored and identical for every message, so counting it would add
-- a constant to every row and change no comparison.
CREATE TRIGGER tr_notify_volume_recorded
AFTER INSERT ON messages
FOR EACH ROW
WHEN NEW.type = 'notify'
BEGIN
  INSERT INTO notify_volume
      (from_agent, to_agent, sent, characters, largest, first_utc, last_utc)
  VALUES (NEW.from_agent, NEW.to_agent, 1, length(NEW.payload_json),
          length(NEW.payload_json), NEW.created_utc, NEW.created_utc)
  ON CONFLICT(from_agent, to_agent) DO UPDATE SET
      sent       = sent + 1,
      characters = characters + excluded.characters,
      largest    = max(largest, excluded.largest),
      last_utc   = excluded.last_utc;
END;

-- Whatever history the message log still holds, counted at migration time.
--
-- Without this the counter starts at zero on a database that has been running for weeks, and its
-- first reading understates the volume by exactly the amount already sent -- which is the reading
-- somebody would take a decision from. It captures only what retention has not yet removed, so it
-- is a floor rather than a complete history, and that is the best available: the messages it
-- cannot see are already gone.
--
-- min and max over created_utc rather than first and last seen, because rows carry no order
-- of their own: id ascends with insertion, and a backfill reads a table whose contents were
-- written in whatever order the deployment produced them.
INSERT INTO notify_volume (from_agent, to_agent, sent, characters, largest, first_utc, last_utc)
SELECT from_agent,
       to_agent,
       count(*),
       sum(length(payload_json)),
       max(length(payload_json)),
       min(created_utc),
       max(created_utc)
  FROM messages
 WHERE type = 'notify'
 GROUP BY from_agent, to_agent;
