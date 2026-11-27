-- Migration 005. Upgrades a schema 4 store in place and keeps every row (SPEC.md 9.2).
--
-- 1. A message may hold 512,000 characters, where it held 8,000 (rule 8.4; the owner's decision, 2026-11-02). A CHECK
--    cannot be altered, so messages and ownership_questions, which copies an agent's answer whole, are rebuilt the way
--    002 rebuilt proposals: built beside the old table, copied, the old one dropped, the new one taking its name.
--    Nothing references either table by key, so dropping cascades nowhere. Dropping takes their triggers and indexes,
--    which are recreated below from their latest text. They are created after the copy, so the copy fires none of them.
--
-- 2. Every message's length is recorded in message_lengths, with no words, in a table retention does not touch, so
--    the limits can be judged from what happens (SPEC.md 17 question 16).

CREATE TABLE messages_005 (
  id              INTEGER PRIMARY KEY AUTOINCREMENT,
  exchange_id     INTEGER NOT NULL REFERENCES "exchanges"(id),
  type            TEXT    NOT NULL CHECK (type IN ('request', 'clarify', 'result')),
  from_agent      TEXT    NOT NULL REFERENCES agents(name),
  to_agent        TEXT    NOT NULL REFERENCES agents(name),
  responder_model TEXT    NOT NULL,
  payload_json    TEXT    NOT NULL,
  content_hash    TEXT    NOT NULL,
  conversation_id TEXT    NOT NULL REFERENCES conversations(id),
  created_utc     TEXT    NOT NULL,
  dedup_bucket    INTEGER GENERATED ALWAYS AS (unixepoch(created_utc) / 60) STORED,
  delivered_utc   TEXT,

  CHECK (from_agent <> to_agent),
  CHECK (length(payload_json) <= 512000),
  CHECK (json_valid(payload_json)),
  CHECK (length(responder_model) BETWEEN 1 AND 64
         AND responder_model NOT GLOB '*[^a-zA-Z0-9._:/-]*'),
  CHECK (length(content_hash) = 64
         AND content_hash NOT GLOB '*[^0-9a-f]*'),
  CHECK (unixepoch(created_utc) IS NOT NULL),

  FOREIGN KEY (exchange_id, conversation_id)
    REFERENCES "exchanges"(id, conversation_id)
) STRICT;

INSERT INTO messages_005
      (id, exchange_id, type, from_agent, to_agent, responder_model, payload_json, content_hash, conversation_id,
       created_utc, delivered_utc)
SELECT id, exchange_id, type, from_agent, to_agent, responder_model, payload_json, content_hash, conversation_id,
       created_utc, delivered_utc
  FROM messages;

CREATE TABLE ownership_questions_005 (
  id               INTEGER PRIMARY KEY AUTOINCREMENT,
  agent            TEXT    NOT NULL REFERENCES agents(name) ON DELETE CASCADE,
  conversation_id  TEXT    NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
  exchange_id      INTEGER NOT NULL,
  state            TEXT    NOT NULL
                   CHECK (state IN ('asked', 'asked_back', 'answered', 'waiting', 'accepted', 'discarded')),
  -- The agent's words as written, and with runs of whitespace collapsed (§10.2). Bounded by rule 8.4's
  -- hard limit because they are copies of a message that already was.
  answer           TEXT    CHECK (answer IS NULL OR length(answer) <= 512000),
  flattened        TEXT    CHECK (flattened IS NULL OR length(flattened) <= 512000),
  -- Why an answer was not accepted automatically, for the person reviewing it.
  waiting_because  TEXT    CHECK (waiting_because IS NULL OR length(waiting_because) BETWEEN 1 AND 500),
  asked_utc        TEXT    NOT NULL,
  settled_utc      TEXT,
  -- Whether the agent's node was asked to refresh it once its first line was accepted automatically
  -- (§11.1). At most one row per agent ever says so, below.
  restart_sent     INTEGER NOT NULL DEFAULT 0 CHECK (restart_sent IN (0, 1)),

  FOREIGN KEY (exchange_id, conversation_id)
    REFERENCES exchanges(id, conversation_id),

  -- An answer exists from the moment the agent gave one, and not before. A discarded question may have none: a
  -- person who closed its conversation before the agent answered has settled it unanswered.
  CHECK (state NOT IN ('asked', 'asked_back') OR answer IS NULL),
  CHECK (state NOT IN ('answered', 'waiting', 'accepted') OR answer IS NOT NULL),
  CHECK ((answer IS NULL) = (flattened IS NULL)),
  -- A waiting answer says why, and nothing else carries a reason that is no longer true.
  CHECK ((state = 'waiting') = (waiting_because IS NOT NULL)),
  CHECK ((state IN ('accepted', 'discarded')) = (settled_utc IS NOT NULL)),
  CHECK (restart_sent = 0 OR state = 'accepted')
) STRICT;

INSERT INTO ownership_questions_005
      (id, agent, conversation_id, exchange_id, state, answer, flattened, waiting_because, asked_utc, settled_utc,
       restart_sent)
SELECT id, agent, conversation_id, exchange_id, state, answer, flattened, waiting_because, asked_utc, settled_utc,
       restart_sent
  FROM ownership_questions;

-- An id once used is never given again. The new tables' counters know only the highest id that survived, and retention
-- removes messages, so the old counters are carried across.
INSERT INTO sqlite_sequence (name, seq)
SELECT 'messages_005', 0 WHERE NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'messages_005');

UPDATE sqlite_sequence
   SET seq = max(seq, coalesce((SELECT seq FROM sqlite_sequence WHERE name = 'messages'), 0))
 WHERE name = 'messages_005';

INSERT INTO sqlite_sequence (name, seq)
SELECT 'ownership_questions_005', 0 WHERE NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'ownership_questions_005');

UPDATE sqlite_sequence
   SET seq = max(seq, coalesce((SELECT seq FROM sqlite_sequence WHERE name = 'ownership_questions'), 0))
 WHERE name = 'ownership_questions_005';

-- Renaming a table checks every trigger in the schema, and this one on exchanges names messages.
DROP TRIGGER tr_an_outcome_has_its_result;

DROP TABLE messages;
ALTER TABLE messages_005 RENAME TO messages;

DROP TABLE ownership_questions;
ALTER TABLE ownership_questions_005 RENAME TO ownership_questions;

CREATE UNIQUE INDEX ux_message_dedup
  ON messages(content_hash, dedup_bucket);

CREATE TRIGGER tr_an_outcome_has_its_result BEFORE UPDATE OF outcome ON exchanges
WHEN NEW.outcome IS NOT NULL AND OLD.outcome IS NULL
BEGIN
  SELECT RAISE(ABORT, 'an exchange closes on a result from its receiver')
   WHERE NEW.outcome NOT IN ('closed', 'settled')
     AND NOT EXISTS (SELECT 1 FROM messages
                      WHERE exchange_id = NEW.id AND type = 'result'
                        AND from_agent = NEW.receiver
                        AND responder_model <> 'parley:broker');

  SELECT RAISE(ABORT, 'a closed exchange carries the broker''s closed result')
   WHERE NEW.outcome = 'closed'
     AND NOT EXISTS (SELECT 1 FROM messages
                      WHERE exchange_id = NEW.id AND type = 'result'
                        AND responder_model = 'parley:broker');

  -- SPEC.md §5.6: settled only by the agent that asked, only a question to the person, and only beside a reply it
  -- wrote at that instant on a Request the person opened with it in the same conversation. The instant is what lets a
  -- surface show, beside that reply, what it settled.
  SELECT RAISE(ABORT, 'a question is settled only by its requester''s reply to the person')
   WHERE NEW.outcome = 'settled'
     AND NOT EXISTS (SELECT 1 FROM exchanges AS s
                       JOIN agents AS p ON p.name = NEW.receiver
                       JOIN messages AS m ON m.exchange_id = s.id
                      WHERE s.id = NEW.settled_by
                        AND s.conversation_id = NEW.conversation_id
                        AND s.requester = NEW.receiver
                        AND s.receiver = NEW.requester
                        AND p.adapter = 'parley-console'
                        AND m.from_agent = NEW.requester
                        AND m.type IN ('clarify', 'result')
                        AND m.created_utc = NEW.closed_utc);
END;

CREATE TRIGGER tr_result_is_well_formed BEFORE INSERT ON messages
WHEN NEW.type = 'result' AND json_valid(NEW.payload_json)
BEGIN
  SELECT RAISE(ABORT, 'a result carries an outcome for each item')
   WHERE json_type(NEW.payload_json, '$.outcomes') IS NOT 'array'
      OR json_array_length(NEW.payload_json, '$.outcomes') = 0;

  SELECT RAISE(ABORT, 'every outcome names its own item')
   WHERE (SELECT count(*) FROM json_each(NEW.payload_json, '$.outcomes'))
      <> (SELECT count(DISTINCT json_extract(value, '$.id'))
            FROM json_each(NEW.payload_json, '$.outcomes'));

  SELECT RAISE(ABORT, 'an outcome is done, answered, acknowledged, blocked or closed')
   WHERE EXISTS (SELECT 1 FROM json_each(NEW.payload_json, '$.outcomes')
                  WHERE json_extract(value, '$.outcome') IS NULL
                     OR json_extract(value, '$.outcome')
                        NOT IN ('done', 'answered', 'acknowledged', 'blocked', 'closed'));

  SELECT RAISE(ABORT, 'a blocked outcome names its blocker, and no other outcome does')
   WHERE EXISTS (SELECT 1 FROM json_each(NEW.payload_json, '$.outcomes')
                  WHERE (json_extract(value, '$.outcome') = 'blocked')
                     <> (json_extract(value, '$.blocker') IS NOT NULL
                         AND json_extract(value, '$.blocker')
                             IN ('missing_information', 'conflict', 'permission')));

  SELECT RAISE(ABORT, 'a source names a participant that is not registered')
   WHERE EXISTS (SELECT 1 FROM json_each(NEW.payload_json, '$.outcomes') o,
                               json_each(o.value, '$.sources') s
                  WHERE s.type <> 'text' OR s.value NOT IN (SELECT name FROM agents));

  SELECT RAISE(ABORT, 'a result does not name its own sender as a source')
   WHERE EXISTS (SELECT 1 FROM json_each(NEW.payload_json, '$.outcomes') o,
                               json_each(o.value, '$.sources') s
                  WHERE s.value = NEW.from_agent);

  SELECT RAISE(ABORT, 'only the broker records a closed outcome')
   WHERE NEW.responder_model <> 'parley:broker'
     AND EXISTS (SELECT 1 FROM json_each(NEW.payload_json, '$.outcomes')
                  WHERE json_extract(value, '$.outcome') = 'closed');

  SELECT RAISE(ABORT, 'the broker records only closed outcomes')
   WHERE NEW.responder_model = 'parley:broker'
     AND EXISTS (SELECT 1 FROM json_each(NEW.payload_json, '$.outcomes')
                  WHERE json_extract(value, '$.outcome') <> 'closed');
END;

CREATE TRIGGER tr_result_recorded AFTER INSERT ON messages
FOR EACH ROW WHEN NEW.type = 'result' AND json_valid(NEW.payload_json)
BEGIN
  INSERT INTO result_items
      (message_id, exchange_id, item, outcome, blocker, from_agent, to_agent, responder_model,
       created_utc)
  SELECT NEW.id, NEW.exchange_id, json_extract(o.value, '$.id'),
         json_extract(o.value, '$.outcome'), json_extract(o.value, '$.blocker'),
         NEW.from_agent, NEW.to_agent, NEW.responder_model, NEW.created_utc
    FROM json_each(NEW.payload_json, '$.outcomes') o;

  INSERT INTO result_sources (message_id, exchange_id, item, from_agent, source, created_utc)
  SELECT DISTINCT NEW.id, NEW.exchange_id, json_extract(o.value, '$.id'), NEW.from_agent,
         s.value, NEW.created_utc
    FROM json_each(NEW.payload_json, '$.outcomes') o,
         json_each(o.value, '$.sources') s;
END;

CREATE TRIGGER tr_request_is_well_formed BEFORE INSERT ON messages
WHEN NEW.type = 'request' AND json_valid(NEW.payload_json)
BEGIN
  SELECT RAISE(ABORT, 'a request carries an ask')
   WHERE json_type(NEW.payload_json, '$.ask') IS NOT 'text'
      OR length(trim(json_extract(NEW.payload_json, '$.ask'),
                     char(9, 10, 11, 12, 13, 32, 133, 160, 5760,
                          8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202,
                          8232, 8233, 8239, 8287, 12288))) = 0;
END;

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

CREATE UNIQUE INDEX ux_one_live_question ON ownership_questions(agent)
WHERE state IN ('asked', 'asked_back', 'answered', 'waiting');

CREATE UNIQUE INDEX ux_one_refresh ON ownership_questions(agent) WHERE restart_sent = 1;

CREATE TRIGGER tr_question_is_the_facilitators
BEFORE INSERT ON ownership_questions
WHEN NEW.state <> 'asked'
  OR NOT EXISTS (SELECT 1
                   FROM exchanges e JOIN agents f ON f.name = e.requester
                  WHERE e.id = NEW.exchange_id
                    AND e.receiver = NEW.agent
                    AND f.adapter = 'parley-facilitator')
  OR EXISTS (SELECT 1 FROM agents a
              WHERE a.name = NEW.agent
                AND a.adapter IN ('parley-facilitator', 'parley-console'))
BEGIN
  SELECT RAISE(ABORT, 'a question is the facilitator''s own Request to an agent, and starts asked');
END;

CREATE TRIGGER tr_question_accepted_only_with_a_line
BEFORE UPDATE OF state ON ownership_questions
WHEN NEW.state = 'accepted'
 AND NOT EXISTS (SELECT 1 FROM agent_ownership_accepted WHERE agent = NEW.agent)
BEGIN
  SELECT RAISE(ABORT, 'a question is accepted only once its line is recorded');
END;

CREATE TRIGGER tr_settled_question_stays_settled
BEFORE UPDATE OF state ON ownership_questions
WHEN OLD.state IN ('accepted', 'discarded') AND NEW.state <> OLD.state
BEGIN
  SELECT RAISE(ABORT, 'a settled question stays settled');
END;

CREATE TRIGGER tr_asking_marks_the_agent
AFTER INSERT ON ownership_questions
BEGIN
  UPDATE agents SET asked_utc = NEW.asked_utc WHERE name = NEW.agent AND asked_utc IS NULL;
END;

CREATE TRIGGER tr_refreshing_marks_the_agent
AFTER UPDATE OF restart_sent ON ownership_questions
WHEN NEW.restart_sent = 1
BEGIN
  UPDATE agents SET refreshed_utc = NEW.settled_utc WHERE name = NEW.agent AND refreshed_utc IS NULL;
END;

-- How long each message was, and never a word of it. No key to messages or conversations, because a record outlives
-- both: retention removes the message and this is what is left to measure. The agents are keyed, so a purge takes
-- their records and a rename follows them.
CREATE TABLE message_lengths (
  message_id      INTEGER PRIMARY KEY,
  conversation_id TEXT    NOT NULL,
  -- Its order in the conversation, counted from the records so that retention does not renumber it.
  position        INTEGER NOT NULL CHECK (position > 0),
  type            TEXT    NOT NULL CHECK (type IN ('request', 'clarify', 'result')),
  from_agent      TEXT    NOT NULL REFERENCES agents(name) ON DELETE CASCADE,
  to_agent        TEXT    NOT NULL REFERENCES agents(name) ON DELETE CASCADE,
  characters      INTEGER NOT NULL CHECK (characters >= 0),
  created_utc     TEXT    NOT NULL
) STRICT;

CREATE INDEX ix_message_lengths_conversation ON message_lengths(conversation_id, position);

INSERT INTO message_lengths
      (message_id, conversation_id, position, type, from_agent, to_agent, characters, created_utc)
SELECT id, conversation_id, row_number() OVER (PARTITION BY conversation_id ORDER BY id), type, from_agent, to_agent,
       length(payload_json), created_utc
  FROM messages;

-- In storage, so that no route that stores a message can forget to.
CREATE TRIGGER tr_message_length_recorded AFTER INSERT ON messages
BEGIN
  INSERT INTO message_lengths
      (message_id, conversation_id, position, type, from_agent, to_agent, characters, created_utc)
  VALUES (NEW.id, NEW.conversation_id,
          coalesce((SELECT max(position) FROM message_lengths WHERE conversation_id = NEW.conversation_id), 0) + 1,
          NEW.type, NEW.from_agent, NEW.to_agent, length(NEW.payload_json), NEW.created_utc);
END;
