-- Migration 002. Upgrades a schema 1 store in place and keeps every row (SPEC.md 9.2).
--
-- 1. A roadmap proposal's id is never used twice. It was INTEGER PRIMARY KEY, which takes the highest id plus one,
--    so purging an agent whose proposal held the highest id gave the next proposal that same id, and a person who
--    had the purged one open confirmed or declined a different agent's plan. SQLite cannot add AUTOINCREMENT to a
--    table, so the table is rebuilt. Foreign keys stay on in a migration's transaction, so renaming the old table
--    aside would rewrite every reference to follow it, and dropping it deletes its rows first, cascading to every
--    item and need. So the new table is built beside it, the items and needs are copied out, the old table is
--    dropped (taking them with it), the new one takes its name, and the items and needs are put back.
--
-- 2. An item's conversation is opened by the person, judged by its exchanges. The trigger asked whether the
--    conversation held a message, and retention removes messages, so an agent answering in an item's conversation
--    after retention was refused with storage's own text. Exchanges are not removed by retention.

CREATE TABLE roadmap_proposals_002 (
  -- AUTOINCREMENT, so an id once used is never given again, even after its proposal was purged: a person
  -- holding a purged proposal open would otherwise decide whichever proposal took its id.
  id                  INTEGER PRIMARY KEY AUTOINCREMENT,
  proposer            TEXT    NOT NULL REFERENCES agents(name) ON DELETE CASCADE,
  source_conversation TEXT    REFERENCES conversations(id) ON DELETE SET NULL,
  -- A hint naming the roadmap this would revise. The person resolves it (§5.7); nothing joins on it.
  target_title        TEXT,
  title               TEXT    NOT NULL
                      CHECK (length(trim(title, ' ' || char(9) || char(10) || char(13))) > 0
                             AND instr(title, char(10)) = 0 AND instr(title, char(13)) = 0),
  -- Characters in the whole proposal, kept so a bound can later be chosen from evidence.
  size                INTEGER NOT NULL CHECK (size >= 0),
  state               TEXT    NOT NULL DEFAULT 'waiting'
                      CHECK (state IN ('waiting', 'confirmed', 'declined', 'superseded')),
  created_utc         TEXT    NOT NULL CHECK (unixepoch(created_utc) IS NOT NULL),
  decided_utc         TEXT    CHECK (decided_utc IS NULL OR unixepoch(decided_utc) IS NOT NULL),

  -- IS rather than =, because a NULL decided_utc makes = yield NULL and a CHECK admits NULL.
  CHECK ((state = 'waiting') = (decided_utc IS NULL))
) STRICT;

INSERT INTO roadmap_proposals_002
       (id, proposer, source_conversation, target_title, title, size, state, created_utc, decided_utc)
SELECT id, proposer, source_conversation, target_title, title, size, state, created_utc, decided_utc
  FROM roadmap_proposals;

CREATE TEMP TABLE kept_proposal_items AS
SELECT proposal_id, position, text, intended_agent FROM roadmap_proposal_items;

CREATE TEMP TABLE kept_proposal_needs AS
SELECT proposal_id, position, needs_position FROM roadmap_proposal_needs;

DROP TABLE roadmap_proposals;

ALTER TABLE roadmap_proposals_002 RENAME TO roadmap_proposals;

INSERT INTO roadmap_proposal_items (proposal_id, position, text, intended_agent)
SELECT proposal_id, position, text, intended_agent FROM temp.kept_proposal_items;

INSERT INTO roadmap_proposal_needs (proposal_id, position, needs_position)
SELECT proposal_id, position, needs_position FROM temp.kept_proposal_needs;

DROP TABLE temp.kept_proposal_items;
DROP TABLE temp.kept_proposal_needs;

CREATE INDEX ix_roadmap_proposals_state ON roadmap_proposals(state, created_utc);

-- The reserved roles are not agents and propose nothing (principle 2.11, §12.3).
CREATE TRIGGER tr_a_proposer_is_an_agent
BEFORE INSERT ON roadmap_proposals
WHEN EXISTS (SELECT 1 FROM agents a
              WHERE a.name = NEW.proposer
                AND a.adapter IN ('parley-facilitator', 'parley-console'))
BEGIN
  SELECT RAISE(ABORT, 'a roadmap is proposed by an agent, never by the person seat or Parley');
END;

-- The next id starts above every id still named anywhere: an undelivered notice and a roadmap keep a proposal's id
-- after the proposal is gone, and a new proposal under that id would read as the one they name. An id used and
-- named nowhere before this migration cannot be known, and is the one reuse it cannot prevent.
INSERT INTO sqlite_sequence (name, seq)
SELECT 'roadmap_proposals', 0
 WHERE NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'roadmap_proposals');

UPDATE sqlite_sequence
   SET seq = max(seq,
                 coalesce((SELECT max(id) FROM roadmap_proposals), 0),
                 coalesce((SELECT max(proposal_id) FROM roadmap_notices), 0),
                 coalesce((SELECT max(origin_proposal) FROM roadmaps), 0))
 WHERE name = 'roadmap_proposals';

DROP TRIGGER tr_a_reserved_conversation_opens_with_the_person;

-- Without this an agent could open a reserved name first, and the person pressing start would then speak
-- into a conversation the agent made. Judged by exchanges, which retention keeps: until the person has opened
-- an exchange in the conversation, nobody else may write in it.
CREATE TRIGGER tr_a_reserved_conversation_opens_with_the_person
BEFORE INSERT ON messages
WHEN EXISTS (SELECT 1 FROM roadmap_item_conversations c WHERE c.conversation_id = NEW.conversation_id)
 AND NOT EXISTS (SELECT 1 FROM exchanges x
                   JOIN agents p ON p.name = x.requester AND p.adapter = 'parley-console'
                  WHERE x.conversation_id = NEW.conversation_id)
 AND NOT EXISTS (SELECT 1 FROM agents a
                  WHERE a.name = NEW.from_agent AND a.adapter = 'parley-console')
BEGIN
  SELECT RAISE(ABORT, 'a roadmap item''s conversation is opened by the person');
END;
