-- Parley schema, migration 008: protocol 0.5, work across repositories.
-- Applied inside a transaction by SchemaMigrator; do not add BEGIN/COMMIT here.
--
-- SPEC.md was rewritten because it defined Parley as a question-and-answer channel. The messages
-- of 0.5 are Request, Clarify and Result (section 5); nothing is orphaned or ghosted (section 14);
-- every exchange belongs to a conversation (section 5.0); a cycle is recorded rather than refused
-- (section 7.2). Section 9.2 is the schema this produces, and SchemaConformanceTests compares them.
--
-- **exchanges and messages are rebuilt, children first.** 006 recorded why a rebuild of exchanges
-- failed inside this migrator: SchemaMigrator runs each migration in a transaction, where
-- PRAGMA foreign_keys is a no-op, and DROP TABLE on a parent under enforced keys fails on the
-- first child row. That is true when the children are still there. Here every table referencing
-- exchanges -- messages and message_referrals -- is copied out and dropped before exchanges is, so
-- the parent has nothing referencing it when it goes. The replacements are created under new
-- names and renamed into place, and SQLite rewrites the references inside the renamed children.
-- Every trigger that names either table is dropped first, because a rename re-parses the whole
-- schema and refuses one that names a column that is about to stop existing.
--
-- **Rows written under 0.4 are copied as they were.** A 0.4 question has no done_when and would
-- fail a CHECK on the new shape during the copy, so the shape of a new Request or Result is held
-- by triggers created after the copy. Those rows were valid under the protocol they were sent in.
--
-- **0.4 notifications and ghost notices are not carried forward.** Neither belonged to an
-- exchange, 0.5 has no message without one, and giving each an exchange would record a Result
-- nobody sent. An exchange 0.4 left standing on its own is given a conversation of its own.

DROP TRIGGER tr_ghost_needs_no_answer;
DROP TRIGGER tr_ghost_is_the_brokers;
DROP TRIGGER tr_gates_are_syntactic;
DROP TRIGGER tr_referral_targets_exist;
DROP TRIGGER tr_referrals_recorded;
DROP TRIGGER tr_notify_volume_recorded;
DROP TRIGGER tr_open_pair_cap;
DROP TRIGGER tr_open_pair_cap_resume;
DROP TRIGGER tr_clarify_cap_bounded_on_insert;
DROP TRIGGER tr_clarify_cap_bounded_on_update;
DROP TRIGGER tr_closed_conversation_stays_closed;

-- Who closed a conversation, because the two ways it can end are opposite facts about how the
-- work went (section 15). NULL on a conversation 0.4 closed, where nothing recorded which.
ALTER TABLE conversations ADD COLUMN closed_by TEXT
  CHECK (closed_by IS NULL OR closed_by IN ('consensus', 'person'));

INSERT INTO conversations (id, state, opened_utc, closed_utc)
SELECT 'standalone-exchange-' || id,
       CASE WHEN state = 'CLOSED' THEN 'CLOSED' ELSE 'OPEN' END,
       opened_utc,
       closed_utc
  FROM exchanges
 WHERE conversation_id IS NULL;

INSERT INTO conversation_participants (conversation_id, agent, joined_utc)
SELECT 'standalone-exchange-' || id, asker, opened_utc FROM exchanges WHERE conversation_id IS NULL
UNION
SELECT 'standalone-exchange-' || id, answerer, opened_utc FROM exchanges WHERE conversation_id IS NULL;

CREATE TABLE exchanges_new (
  id                  INTEGER PRIMARY KEY AUTOINCREMENT,
  pair_key            TEXT    NOT NULL,
  requester           TEXT    NOT NULL REFERENCES agents(name),
  receiver            TEXT    NOT NULL REFERENCES agents(name),
  owed_by             TEXT    NOT NULL REFERENCES agents(name),
  state               TEXT    NOT NULL CHECK (state IN ('OPEN', 'CLOSED')),
  request_group       TEXT
                      CHECK (request_group IS NULL
                             OR (length(request_group) BETWEEN 1 AND 64
                                 AND request_group NOT GLOB '*[^a-zA-Z0-9._-]*')),
  clarify_depth       INTEGER NOT NULL DEFAULT 0,
  clarify_depth_cap   INTEGER NOT NULL DEFAULT 2147483647,
  open_pair_cap       INTEGER NOT NULL DEFAULT 50
                      CHECK (open_pair_cap BETWEEN 1 AND 50),
  uncollected_flag_ms INTEGER NOT NULL,
  stalled_flag_ms     INTEGER NOT NULL,
  opened_utc          TEXT    NOT NULL,
  delivered_utc       TEXT,
  closed_utc          TEXT,
  request_hash        TEXT    NOT NULL,
  outcome             TEXT    CHECK (outcome IN ('complete', 'blocked', 'closed')),
  conversation_id     TEXT    NOT NULL REFERENCES conversations(id),
  version             INTEGER NOT NULL DEFAULT 0,

  CHECK (requester <> receiver),
  CHECK (owed_by IN (requester, receiver)),
  CHECK (clarify_depth >= 0 AND clarify_depth <= clarify_depth_cap),
  CHECK (clarify_depth_cap BETWEEN 0 AND 1000 OR clarify_depth_cap = 2147483647),
  CHECK (uncollected_flag_ms > 0 AND stalled_flag_ms > 0),
  CHECK ((state = 'CLOSED') = (closed_utc IS NOT NULL)),
  CHECK ((state = 'CLOSED') = (outcome IS NOT NULL)),
  CHECK (pair_key = min(requester, receiver) || char(31) || max(requester, receiver))
) STRICT;

INSERT INTO exchanges_new
  (id, pair_key, requester, receiver, owed_by, state, request_group, clarify_depth,
   clarify_depth_cap, open_pair_cap, uncollected_flag_ms, stalled_flag_ms, opened_utc,
   delivered_utc, closed_utc, request_hash, outcome, conversation_id, version)
-- **Work 0.4 left open in a conversation a person had closed is closed with it.** 0.4's force_conclude
-- closed the conversation row and nothing else, and its exchanges stayed open or orphaned until the ghost
-- sweep reached them. Carried across open, they would sit in a closed conversation -- a state 0.5 neither
-- produces nor clears: the receiver would hold that conversation, so every Request it sent would be
-- assigned there and refused, and force_conclude, which ends only a conversation not yet closed, would
-- close nothing. Closed as the broker closes work when a conversation ends first, dated when it ended.
SELECT e.id, e.pair_key, e.asker, e.answerer, e.owed_by,
       CASE WHEN e.state = 'CLOSED' OR c.state = 'CLOSED' THEN 'CLOSED' ELSE 'OPEN' END,
       e.ask_group, e.clarify_depth, e.clarify_depth_cap, e.open_pair_cap, e.delivery_window_ms,
       e.working_window_ms, e.opened_utc, e.delivered_utc,
       CASE
         WHEN e.state = 'CLOSED' THEN e.closed_utc
         WHEN c.state = 'CLOSED' THEN coalesce(c.closed_utc, e.opened_utc)
       END,
       e.question_hash,
       CASE
         WHEN e.state <> 'CLOSED' AND c.state = 'CLOSED' THEN 'closed'
         WHEN e.resolution = 'answered' THEN 'complete'
         WHEN e.resolution = 'out_of_scope' THEN 'blocked'
         WHEN e.resolution = 'unknown' THEN 'blocked'
         WHEN e.resolution = 'ghosted' THEN 'closed'
       END,
       coalesce(e.conversation_id, 'standalone-exchange-' || e.id),
       e.version
  FROM exchanges e
  LEFT JOIN conversations c ON c.id = e.conversation_id
 ORDER BY e.id;

-- An orphan whose question was asked again while it was orphaned becomes open beside the re-ask,
-- which the distinctness index would refuse. 0.4 settled that the re-ask wins (its question 9), so
-- one of each such set survives and the rest are closed rather than the migration failing on a real
-- store: the one 0.4 held open if there is one, otherwise the newest. Ranked on both, so exactly one
-- row in a set outranks every other and none is closed by mistake.
UPDATE exchanges_new
   SET state = 'CLOSED',
       outcome = 'closed',
       closed_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
 WHERE state = 'OPEN'
   AND EXISTS (SELECT 1
                 FROM exchanges_new other
                 JOIN exchanges was_other ON was_other.id = other.id
                 JOIN exchanges was_this ON was_this.id = exchanges_new.id
                WHERE other.state = 'OPEN'
                  AND other.id <> exchanges_new.id
                  AND other.requester = exchanges_new.requester
                  AND other.receiver = exchanges_new.receiver
                  AND other.request_hash = exchanges_new.request_hash
                  AND ((was_other.state = 'OPEN') > (was_this.state = 'OPEN')
                       OR ((was_other.state = 'OPEN') = (was_this.state = 'OPEN')
                           AND other.id > exchanges_new.id)));

CREATE TABLE messages_new (
  id              INTEGER PRIMARY KEY AUTOINCREMENT,
  exchange_id     INTEGER NOT NULL REFERENCES exchanges_new(id),
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
  CHECK (length(payload_json) <= 8000),
  CHECK (json_valid(payload_json)),
  CHECK (length(responder_model) BETWEEN 1 AND 64
         AND responder_model NOT GLOB '*[^a-zA-Z0-9._:/-]*'),
  CHECK (length(content_hash) = 64
         AND content_hash NOT GLOB '*[^0-9a-f]*'),
  CHECK (unixepoch(created_utc) IS NOT NULL),

  FOREIGN KEY (exchange_id, conversation_id)
    REFERENCES exchanges_new(id, conversation_id)
) STRICT;

CREATE UNIQUE INDEX ux_exchange_conversation_new ON exchanges_new(id, conversation_id);

INSERT INTO messages_new
  (id, exchange_id, type, from_agent, to_agent, responder_model, payload_json, content_hash,
   conversation_id, created_utc, delivered_utc)
SELECT m.id, m.exchange_id,
       CASE m.type WHEN 'question' THEN 'request' WHEN 'answer' THEN 'result' ELSE 'clarify' END,
       m.from_agent, m.to_agent, m.responder_model, m.payload_json, m.content_hash,
       e.conversation_id, m.created_utc, m.delivered_utc
  FROM messages m
  JOIN exchanges_new e ON e.id = m.exchange_id
 WHERE m.type IN ('question', 'clarify', 'answer')
 ORDER BY m.id;

CREATE TABLE size_refusals_new (
  agent       TEXT    NOT NULL REFERENCES agents(name) ON DELETE CASCADE,
  type        TEXT    NOT NULL CHECK (type IN ('request', 'clarify', 'result')),
  refusals    INTEGER NOT NULL CHECK (refusals > 0),
  worst       INTEGER NOT NULL CHECK (worst > 0),
  total       INTEGER NOT NULL CHECK (total > 0),
  cap         INTEGER NOT NULL CHECK (cap > 0),
  last_utc    TEXT    NOT NULL,

  PRIMARY KEY (agent, type),

  CHECK (worst > cap),
  CHECK (total >= worst)
) STRICT;

-- A question and a notification were both what a Request is now, so their counts are one row.
INSERT INTO size_refusals_new (agent, type, refusals, worst, total, cap, last_utc)
SELECT agent,
       CASE type WHEN 'answer' THEN 'result' WHEN 'clarify' THEN 'clarify' ELSE 'request' END,
       sum(refusals), max(worst), sum(total), max(cap), max(last_utc)
  FROM size_refusals
 GROUP BY 1, 2;

-- A message identifier is never reused -- the pruner deletes by identifiers it read outside any
-- transaction -- and the rows not carried forward may have held the highest ones. So the new table's
-- sequence starts where the old one stopped, not at the largest identifier copied. Dropping the old
-- table removes its sequence row, so this is read before the drop.
INSERT INTO sqlite_sequence (name, seq)
SELECT 'messages_new', seq FROM sqlite_sequence
 WHERE name = 'messages'
   AND NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'messages_new');

UPDATE sqlite_sequence
   SET seq = (SELECT seq FROM sqlite_sequence WHERE name = 'messages')
 WHERE name = 'messages_new'
   AND (SELECT seq FROM sqlite_sequence WHERE name = 'messages') > seq;

DROP TABLE message_referrals;
DROP TABLE notify_volume;
DROP TABLE messages;
DROP TABLE exchanges;
DROP TABLE size_refusals;

ALTER TABLE exchanges_new RENAME TO exchanges;
ALTER TABLE messages_new RENAME TO messages;
ALTER TABLE size_refusals_new RENAME TO size_refusals;

DROP INDEX ux_exchange_conversation_new;
CREATE UNIQUE INDEX ux_exchange_conversation ON exchanges(id, conversation_id);

CREATE UNIQUE INDEX ux_open_request
  ON exchanges(requester, receiver, request_hash) WHERE state = 'OPEN';

CREATE UNIQUE INDEX ux_message_dedup
  ON messages(content_hash, dedup_bucket);

CREATE TABLE conversation_cycles (
  exchange_id     INTEGER PRIMARY KEY REFERENCES exchanges(id),
  conversation_id TEXT    NOT NULL REFERENCES conversations(id),
  path            TEXT    NOT NULL CHECK (length(path) BETWEEN 1 AND 4000),
  recorded_utc    TEXT    NOT NULL
) STRICT;

CREATE TABLE result_items (
  message_id      INTEGER NOT NULL,
  exchange_id     INTEGER NOT NULL REFERENCES exchanges(id),
  item            TEXT    NOT NULL CHECK (length(item) BETWEEN 1 AND 64),
  outcome         TEXT    NOT NULL
                  CHECK (outcome IN ('done', 'answered', 'acknowledged', 'blocked', 'closed')),
  blocker         TEXT    CHECK (blocker IN ('missing_information', 'conflict', 'permission')),
  from_agent      TEXT    NOT NULL REFERENCES agents(name),
  to_agent        TEXT    NOT NULL REFERENCES agents(name),
  responder_model TEXT    NOT NULL,
  created_utc     TEXT    NOT NULL,

  PRIMARY KEY (message_id, item),

  CHECK ((outcome = 'blocked') = (blocker IS NOT NULL))
) STRICT;

CREATE TABLE result_sources (
  message_id      INTEGER NOT NULL,
  exchange_id     INTEGER NOT NULL REFERENCES exchanges(id),
  item            TEXT    NOT NULL CHECK (length(item) BETWEEN 1 AND 64),
  from_agent      TEXT    NOT NULL REFERENCES agents(name),
  source          TEXT    NOT NULL REFERENCES agents(name),
  created_utc     TEXT    NOT NULL,

  PRIMARY KEY (message_id, item, source),

  CHECK (source <> from_agent)
) STRICT;

CREATE INDEX ix_result_sources_source ON result_sources(from_agent, source);

CREATE TRIGGER tr_open_pair_cap BEFORE INSERT ON exchanges
WHEN NEW.state = 'OPEN' AND NEW.open_pair_cap < 50
BEGIN
  SELECT RAISE(ABORT, 'open_pair_cap reached')
   WHERE (SELECT count(*) FROM exchanges
           WHERE state = 'OPEN' AND requester = NEW.requester AND receiver = NEW.receiver)
         >= NEW.open_pair_cap;
END;

CREATE TRIGGER tr_closed_exchange_stays_closed BEFORE UPDATE OF state ON exchanges
WHEN OLD.state = 'CLOSED' AND NEW.state <> 'CLOSED'
BEGIN
  SELECT RAISE(ABORT, 'a closed exchange stays closed');
END;

CREATE TRIGGER tr_an_outcome_has_its_result BEFORE UPDATE OF outcome ON exchanges
WHEN NEW.outcome IS NOT NULL AND OLD.outcome IS NULL
BEGIN
  SELECT RAISE(ABORT, 'an exchange closes on a result from its receiver')
   WHERE NEW.outcome <> 'closed'
     AND NOT EXISTS (SELECT 1 FROM messages
                      WHERE exchange_id = NEW.id AND type = 'result'
                        AND from_agent = NEW.receiver
                        AND responder_model <> 'parley:broker');

  SELECT RAISE(ABORT, 'a closed exchange carries the broker''s closed result')
   WHERE NEW.outcome = 'closed'
     AND NOT EXISTS (SELECT 1 FROM messages
                      WHERE exchange_id = NEW.id AND type = 'result'
                        AND responder_model = 'parley:broker');
END;

CREATE TRIGGER tr_closed_conversation_stays_closed BEFORE INSERT ON exchanges
BEGIN
  SELECT RAISE(ABORT, 'conversation is closed')
   WHERE EXISTS (SELECT 1 FROM conversations
                  WHERE id = NEW.conversation_id AND state = 'CLOSED');
END;

CREATE TRIGGER tr_request_is_well_formed BEFORE INSERT ON messages
WHEN NEW.type = 'request' AND json_valid(NEW.payload_json)
BEGIN
  SELECT RAISE(ABORT, 'a request must say what done looks like')
   WHERE json_type(NEW.payload_json, '$.done_when') IS NOT 'text'
      OR length(trim(json_extract(NEW.payload_json, '$.done_when'))) < 20;

  SELECT RAISE(ABORT, 'a request carries at least one item')
   WHERE json_type(NEW.payload_json, '$.items') IS NOT 'array'
      OR json_array_length(NEW.payload_json, '$.items') = 0;

  SELECT RAISE(ABORT, 'every item is work or a question')
   WHERE EXISTS (SELECT 1 FROM json_each(NEW.payload_json, '$.items')
                  WHERE json_extract(value, '$.kind') IS NULL
                     OR json_extract(value, '$.kind') NOT IN ('work', 'question'));

  -- The bound result_items holds an item to. Without it a Request was admitted that its receiver could
  -- never answer: every Result must echo the ids exactly, and the Result's own rows refuse a longer one.
  SELECT RAISE(ABORT, 'every item id is text of 1 to 64 characters')
   WHERE EXISTS (SELECT 1 FROM json_each(NEW.payload_json, '$.items')
                  WHERE json_type(value, '$.id') IS NOT 'text'
                     OR length(json_extract(value, '$.id')) NOT BETWEEN 1 AND 64);

  SELECT RAISE(ABORT, 'every item has its own id')
   WHERE (SELECT count(*) FROM json_each(NEW.payload_json, '$.items'))
      <> (SELECT count(DISTINCT json_extract(value, '$.id'))
            FROM json_each(NEW.payload_json, '$.items'));

  SELECT RAISE(ABORT, 'a gate must reference an earlier item')
   WHERE EXISTS (
     SELECT 1 FROM json_each(NEW.payload_json, '$.items') q
      WHERE json_extract(q.value, '$.gate.on') IS NOT NULL
        AND NOT EXISTS (
          SELECT 1 FROM json_each(NEW.payload_json, '$.items') p
           WHERE p.key < q.key
             AND json_extract(p.value, '$.id') = json_extract(q.value, '$.gate.on')));
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
