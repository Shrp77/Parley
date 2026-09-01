-- Rule 8.5's message cap moves from 4,000 characters to 8,000.
--
-- The first time this number has been set from measurement rather than judgement. SPEC.md §17
-- question 16 asked for `refusals()` showing "the same agents turned away repeatedly at similar
-- sizes, rather than one agent once", and that is what it showed: six agents, forty-three
-- refusals, worst case 6,195 characters, means between 4,328 and 4,556. Every refusal ever
-- recorded fits under the new number.
--
-- WHY THIS IS A TABLE REBUILD. SQLite cannot alter a CHECK constraint, and the cap is a CHECK
-- because §9.3 puts invariants in storage where application code cannot route around them. That
-- decision is worth its cost here rather than being regretted: the alternative was a limit the
-- broker enforced and a bug could skip.
--
-- ORDER MATTERS, in three ways that are not obvious.
--
-- 1. `tr_ghost_needs_no_answer` lives on `exchanges` but reads `messages`, and SQLite re-parses
--    every trigger in the schema during ALTER TABLE ... RENAME. With `messages` dropped and that
--    trigger still present, the rename fails on a trigger that has nothing to do with it. It is
--    dropped first and recreated last.
--
-- 2. The copy goes into a table that carries no triggers yet. `tr_referrals_recorded` and
--    `tr_notify_volume_recorded` are AFTER INSERT triggers that write to other tables, so
--    copying into a table that already had them would double every referral and every notify
--    ever counted -- silently, into the two reports §17 question 1 is being decided from.
--
-- 3. `dedup_bucket` is GENERATED ALWAYS ... STORED and is therefore absent from both column
--    lists. Naming it would be rejected; omitting it lets SQLite recompute it, which is the same
--    value because it is derived from created_utc.
--
-- The table and trigger definitions below are copied verbatim from 001 and 002 with one number
-- changed in each, and were extracted mechanically rather than retyped.


-- 1. The trigger on another table that reads this one.
DROP TRIGGER tr_ghost_needs_no_answer;

-- 2. Messages, rebuilt with the new cap.
CREATE TABLE messages_new (
  id              INTEGER PRIMARY KEY AUTOINCREMENT,
  exchange_id     INTEGER REFERENCES exchanges(id),  -- NULL for Notify
  type            TEXT    NOT NULL
                  -- Four an agent may send, and one only the broker writes: 'ghost' is §14's
                  -- notice that no answer is coming. It needs a type of its own because none of
                  -- the other four can carry it — an 'answer' is refused on a ghosted exchange
                  -- by tr_ghost_needs_no_answer, and a 'notify' may not name an exchange.
                  CHECK (type IN ('question', 'clarify', 'answer', 'notify', 'ghost')),
  from_agent      TEXT    NOT NULL REFERENCES agents(name),
  to_agent        TEXT    NOT NULL REFERENCES agents(name),
  responder_model TEXT    NOT NULL,       -- see §11.3
  payload_json    TEXT    NOT NULL,
  content_hash    TEXT    NOT NULL,       -- see §13.1
  -- References conversations(id) directly, which the composite key below cannot do for a
  -- Notify: that key is satisfied by a NULL in either column, and a Notify always has a NULL
  -- exchange_id. Without it a notification could name a discussion that does not exist.
  conversation_id TEXT REFERENCES conversations(id),  -- denormalised from the exchange
  created_utc     TEXT    NOT NULL,
  dedup_bucket    INTEGER GENERATED ALWAYS AS (unixepoch(created_utc) / 60) STORED,
  delivered_utc   TEXT,

  CHECK (from_agent <> to_agent),
  CHECK ((type = 'notify') = (exchange_id IS NULL)),
  CHECK (length(payload_json) <= 8000),
  CHECK (json_valid(payload_json)),

  -- Rule 1: a Question states what the asker will do with each possible answer. §5.1 calls
  -- consequences the highest-leverage field in the protocol, and §4.2 gives it a second job as
  -- the asker's own note to itself when the answer arrives hours later, so a Question without
  -- it is not merely impolite: it is unanswerable well and unusable on return.
  --
  -- The length floor does real work. Requiring only that the key be present admits "n/a",
  -- which satisfies a check and states nothing. Forty characters is about the shortest honest
  -- statement of a consequence and is cheap to clear.
  CHECK (type <> 'question'
         OR length(coalesce(json_extract(payload_json, '$.consequences'), '')) >= 40),
  CHECK (length(responder_model) BETWEEN 1 AND 64
         AND responder_model NOT GLOB '*[^a-zA-Z0-9._:/-]*'),
  CHECK (length(content_hash) = 64
         AND content_hash NOT GLOB '*[^0-9a-f]*'),
  CHECK (unixepoch(created_utc) IS NOT NULL),
  CHECK (conversation_id IS NULL
         OR (length(conversation_id) BETWEEN 1 AND 64
             AND conversation_id NOT GLOB '*[^a-zA-Z0-9._-]*'
             AND conversation_id NOT GLOB '*..*')),

  -- The exchange holds the authoritative conversation (§5.0); this column repeats it, and
  -- the composite foreign key below is what stops the copy becoming a second opinion. A
  -- message may not name a conversation its exchange is not in, nor one that does not exist.
  --
  -- Kept deliberately, against the usual rule that one fact belongs in one column. The rule
  -- guards against silent drift, and there is none to guard against once the copy cannot
  -- differ. What it buys is that the message log answers "what happened in this
  -- conversation" on its own, at the moment someone is looking because something has gone
  -- wrong and the exchange rows are what they are trying to check.
  --
  -- One gap remains and is the acceptable direction. A composite foreign key is satisfied by
  -- a NULL in either column, so a message may carry NULL where its exchange carries an
  -- identifier. That loses the shortcut without misreporting anything: the value is still one
  -- join away. The copy can fail to help; it cannot lie.
  --
  -- It also makes an exchange's conversation immutable once any message exists in it, because
  -- the UPDATE that would move it is refused while children reference the old value. §5.0
  -- states that nobody may reassign an exchange, only add to a conversation. That was a
  -- convention until now.
  FOREIGN KEY (exchange_id, conversation_id)
    REFERENCES exchanges(id, conversation_id)
) STRICT;

INSERT INTO messages_new
  (id, exchange_id, type, from_agent, to_agent, responder_model, payload_json,
   content_hash, conversation_id, created_utc, delivered_utc)
SELECT
   id, exchange_id, type, from_agent, to_agent, responder_model, payload_json,
   content_hash, conversation_id, created_utc, delivered_utc
  FROM messages
 ORDER BY id;

DROP TABLE messages;
ALTER TABLE messages_new RENAME TO messages;

-- 3. What the drop took with it.
CREATE UNIQUE INDEX ux_message_dedup
  ON messages(content_hash, dedup_bucket);

CREATE TRIGGER tr_gates_are_syntactic BEFORE INSERT ON messages
WHEN NEW.type = 'question' AND json_valid(NEW.payload_json)
BEGIN
  SELECT RAISE(ABORT, 'sub_question ids are not unique')
   WHERE (SELECT count(*) FROM json_each(NEW.payload_json, '$.sub_questions'))
      <> (SELECT count(DISTINCT json_extract(value, '$.id'))
            FROM json_each(NEW.payload_json, '$.sub_questions'));

  SELECT RAISE(ABORT, 'a gate must reference an earlier sub_question')
   WHERE EXISTS (
     SELECT 1 FROM json_each(NEW.payload_json, '$.sub_questions') q
      WHERE json_extract(q.value, '$.gate.on') IS NOT NULL
        AND NOT EXISTS (
          SELECT 1 FROM json_each(NEW.payload_json, '$.sub_questions') p
           WHERE p.key < q.key
             AND json_extract(p.value, '$.id') = json_extract(q.value, '$.gate.on')));
END;

CREATE TRIGGER tr_ghost_is_the_brokers BEFORE INSERT ON messages
WHEN NEW.type = 'ghost'
BEGIN
  SELECT RAISE(ABORT, 'a ghost message must record parley:broker as its responder')
   WHERE NEW.responder_model <> 'parley:broker';
END;

CREATE TRIGGER tr_referral_targets_exist BEFORE INSERT ON messages
WHEN json_valid(NEW.payload_json)
BEGIN
  SELECT RAISE(ABORT, 'redirect_to names an agent that is not registered')
   WHERE EXISTS (
     SELECT 1 FROM json_tree(NEW.payload_json) t
      WHERE (t.path LIKE '%redirect_to%' OR t.key = 'redirect_to')
        AND t.type = 'text'
        AND t.value NOT IN (SELECT name FROM agents));

  -- A sender referring to itself asserts both halves of a contradiction in one message:
  -- out_of_scope says the boundary is not mine, and the referral says it is. Checked against
  -- every entry rather than the first, so one cannot hide among valid targets.
  SELECT RAISE(ABORT, 'redirect_to names the sender')
   WHERE EXISTS (
     SELECT 1 FROM json_tree(NEW.payload_json) t
      WHERE (t.path LIKE '%redirect_to%' OR t.key = 'redirect_to')
        AND t.type = 'text' AND t.value = NEW.from_agent);
END;

CREATE TRIGGER tr_referrals_recorded AFTER INSERT ON messages
FOR EACH ROW WHEN NEW.type = 'answer' AND json_valid(NEW.payload_json)
BEGIN
  INSERT INTO message_referrals
      (message_id, exchange_id, sub_question, from_agent, to_agent, redirected_to, created_utc)
  SELECT DISTINCT NEW.id, NEW.exchange_id, json_extract(r.value, '$.id'),
         NEW.from_agent, NEW.to_agent, t.value, NEW.created_utc
    FROM json_each(NEW.payload_json, '$.resolutions') r
    JOIN json_tree(r.value, '$.redirect_to') t
   WHERE t.type = 'text' AND json_extract(r.value, '$.id') IS NOT NULL;
END;

CREATE TRIGGER tr_notify_volume_recorded AFTER INSERT ON messages
FOR EACH ROW WHEN NEW.type = 'notify'
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

-- 4. Instructions carry the same bound, deliberately the same number.
CREATE TABLE instructions_new (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  agent        TEXT    NOT NULL REFERENCES agents(name) ON DELETE CASCADE,
  text         TEXT    NOT NULL,
  outcome      TEXT    NOT NULL
               CHECK (outcome IN ('pending', 'taken', 'declined', 'undeliverable', 'failed')),
  reply        TEXT,
  issued_utc   TEXT    NOT NULL,
  settled_utc  TEXT,

  -- The same bound as a message (rule 8.5), and deliberately the same number rather than a
  -- second one. SpecConsistencyTests holds the two together, because SQLite cannot say "the same
  -- as that one" and they would otherwise drift at the worst moment -- when evidence moves the
  -- message cap and this is left behind at a number nobody argued for.
  CHECK (length(text) <= 8000),

  -- Settled and unsettled are one fact, so they move together or the row is lying about one of
  -- them. A reply belongs to a settled instruction: an agent that has not been given the turn
  -- yet cannot have said anything, and a row claiming otherwise would be read as an answer.
  CHECK ((outcome = 'pending') = (settled_utc IS NULL)),
  CHECK (outcome <> 'pending' OR reply IS NULL)
) STRICT;

INSERT INTO instructions_new
  (id, agent, text, outcome, reply, issued_utc, settled_utc)
SELECT
   id, agent, text, outcome, reply, issued_utc, settled_utc
  FROM instructions
 ORDER BY id;

DROP TABLE instructions;
ALTER TABLE instructions_new RENAME TO instructions;

CREATE INDEX ix_instructions_agent ON instructions(agent, issued_utc);

-- 5. And the guard dropped in step 1, restored unchanged.
CREATE TRIGGER tr_ghost_needs_no_answer BEFORE UPDATE ON exchanges
WHEN NEW.resolution = 'ghosted'
BEGIN
  SELECT RAISE(ABORT, 'ghosted requires that no answer was given')
   WHERE EXISTS (SELECT 1 FROM messages WHERE exchange_id = NEW.id AND type = 'answer');
END;
