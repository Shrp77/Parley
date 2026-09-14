-- §7.1's open-question cap becomes off by default, and the top of its band is what "off" is
-- spelled as: at open_pair_cap = 50 neither trigger fires, so an agent may hold as many open
-- exchanges to one peer as it opens.
--
-- WHY THE CAP GOES. It was never a technical bound -- §7.1 says outright that it "rations a
-- peer's attention rather than any resource of the broker's" -- and what it rationed was the
-- wrong thing. A conversation runs on one exchange plus clarifies, a Clarify takes no slot
-- (§5.2), and a Notify takes none either (§5.4), so the cap never bounded the two channels that
-- actually carry text and never shortened a conversation. What it did bound was how many
-- *separate* lines of enquiry one agent could have with one peer at once, at a number chosen
-- before any deployment existed. The owner's decision, on the reasoning already recorded for the
-- clarify cap in ExchangePolicy: a cap censors the evidence that would justify it.
--
-- WHY 50 RATHER THAN 0. 0 is the natural spelling of unbounded and cannot be stored. The bound
-- is `CHECK (open_pair_cap BETWEEN 1 AND 50)`, a column constraint, and SQLite cannot alter one
-- -- so admitting 0 means rebuilding `exchanges` the way 003 rebuilt `messages`.
--
-- THAT REBUILD IS NOT AVAILABLE HERE, AND IT WAS PROBED RATHER THAN REASONED ABOUT. 003 could
-- rebuild `messages` because nothing references it. `exchanges` is a parent: `messages` holds
-- both a foreign key to `exchanges(id)` and a composite one to `(id, conversation_id)`, and
-- `message_referrals` holds another. With foreign_keys = ON, DROP TABLE performs an implicit
-- DELETE and fails on the first child row -- and SchemaMigrator runs every migration inside a
-- transaction, where PRAGMA foreign_keys is a no-op, so it cannot be turned off for the length
-- of the rebuild. A probe under exactly those conditions, with one question message referencing
-- one exchange, returned:
--
--   SqliteException: SQLite Error 19: 'FOREIGN KEY constraint failed'
--
-- Note which databases that spares. An empty one has no child rows, so the rebuild succeeds in
-- every test fixture and fails on every store that has ever carried traffic. A migration with
-- that shape is worse than no migration at all.
--
-- WHAT SURVIVES. The storage-level guarantee of §9.3, for anybody who wants a bound: a
-- deployment setting 1..49 still has it held by these triggers rather than by broker code that
-- a bug can route around. What is given up is the evidence a fired cap would have produced,
-- which is the same trade the clarify cap made at 200 and is recorded in both places.
--
-- The two trigger bodies below are 001's, verbatim, with one clause added to each.

DROP TRIGGER tr_open_pair_cap;
DROP TRIGGER tr_open_pair_cap_resume;

-- 50 is the ceiling of the CHECK on the column, so this is "the cap is at its maximum" read as
-- "there is no cap". Written as a literal rather than derived, because a trigger cannot read a
-- constant and the same literal is already in the column's own CHECK.
CREATE TRIGGER tr_open_pair_cap BEFORE INSERT ON exchanges
WHEN NEW.state = 'OPEN' AND NEW.open_pair_cap < 50
BEGIN
  SELECT RAISE(ABORT, 'open_pair_cap reached')
   WHERE (SELECT count(*) FROM exchanges
           WHERE state = 'OPEN' AND asker = NEW.asker AND answerer = NEW.answerer)
         >= NEW.open_pair_cap;
END;

-- Resuming an orphan is an UPDATE, which the trigger above never sees. Both are required.
CREATE TRIGGER tr_open_pair_cap_resume BEFORE UPDATE ON exchanges
WHEN NEW.state = 'OPEN' AND OLD.state <> 'OPEN' AND NEW.open_pair_cap < 50
BEGIN
  SELECT RAISE(ABORT, 'open_pair_cap reached on resume')
   WHERE (SELECT count(*) FROM exchanges
           WHERE state = 'OPEN' AND asker = NEW.asker AND answerer = NEW.answerer
                 AND id <> NEW.id)
         >= NEW.open_pair_cap;
END;
