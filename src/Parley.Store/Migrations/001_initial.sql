-- Parley schema, migration 001.
-- Applied inside a transaction by SchemaMigrator; do not add BEGIN/COMMIT here.
--
-- Requires SQLite 3.38 or later: STRICT tables (3.37), generated columns (3.31), and
-- unixepoch() (3.38). An older engine fails at the first CREATE TABLE with an error that
-- does not say which feature it objected to. See SPEC.md section 9.2.
--
-- This file is generated from SPEC.md section 9.2 and is deliberately not hand-edited.
-- SchemaConformanceTests compares the two structurally, and the way that check used to fail
-- was somebody changing one of them; generating the migration from the document removes the
-- opportunity. Change the schema in SPEC.md and regenerate.
--
-- It replaces eight migrations. The store was rebuilt from empty when the broker stopped being
-- a file that every adapter opened (SPEC.md section 4), and carrying forward the accumulated
-- ALTER statements of a schema nobody would be upgrading was cost with no reader. The history
-- is in git.

CREATE TABLE nodes (
  -- Detected, never configured, per §4.1.1. The primary key rather than a surrogate, because
  -- the machine's identity is the thing agents are named after.
  machine        TEXT    PRIMARY KEY
                 CHECK (length(machine) BETWEEN 1 AND 64
                        AND machine GLOB '[a-z0-9]*'
                        AND machine NOT GLOB '*[^a-z0-9.-]*'),
  -- Reported for the operator, and used for nothing. A machine is reached through the
  -- connection its node already holds, never by an address the broker dials, so none of this
  -- is a route in -- it is what somebody reading a fleet listing needs in order to know which
  -- box they are looking at. Recording an address the broker then connected to would invert
  -- §13's trust direction, letting a registration tell the broker where to send traffic.
  os             TEXT,
  os_version     TEXT,
  address        TEXT,
  domain         TEXT,
  -- The same observation agents.connected records, one level up, and the one that decides
  -- whether an agent here can be started at all. An agent on a disconnected node is not
  -- unreachable in some derived sense; there is simply nothing left that could start it.
  connected      INTEGER NOT NULL DEFAULT 0 CHECK (connected IN (0, 1)),
  changed_utc    TEXT,
  first_seen_utc TEXT    NOT NULL
) STRICT;

CREATE TABLE agents (
  -- role@machine, per §4.1.1. Stored whole because it is what every other table references
  -- and what every operator types, and split as well because a role question (§5.5) has to
  -- find every agent sharing a role, which a substring match on the whole name cannot do
  -- safely: 'infra' is a prefix of 'infrastructure'.
  name            TEXT    PRIMARY KEY,     -- logical name, NOT a session id
  -- Generated rather than supplied, so the halves cannot disagree with the name they came from.
  -- They exist as columns at all because a role question (§5.5) has to find every agent sharing
  -- a role, and a substring match on the whole name cannot do that safely -- 'infra' is a
  -- prefix of 'infrastructure'. Storing them as ordinary columns would make that indexable and
  -- make it possible for a writer to set role='infra' on an agent named 'billing@server-b'.
  --
  -- A name with no '@' produces an empty role, which the CHECK below refuses. That is the
  -- malformed case failing at the point of insertion rather than becoming a row nothing can
  -- address.
  role            TEXT    NOT NULL
                  GENERATED ALWAYS AS (substr(name, 1, instr(name, '@') - 1)) STORED,
  -- The node that reported this agent. Detected there and never configured, so two machines
  -- cannot both claim to be server-b -- see §4.1.1.
  machine         TEXT    NOT NULL
                  GENERATED ALWAYS AS (substr(name, instr(name, '@') + 1)) STORED
                  REFERENCES nodes(machine),
  adapter         TEXT    NOT NULL,
  tier            TEXT    NOT NULL CHECK (tier IN ('full', 'pull', 'headless')),
  workspace       TEXT,
  session_ref     TEXT,                    -- adapter-specific resume handle
  adapter_config  TEXT,                    -- opaque to the broker (see §12)
  owns            TEXT,                    -- one line, read by peers (see §17 q10)
  -- How many of this agent's adapters are connected right now, raised on connect and lowered
  -- on disconnect, and written by nothing else. Not derived from a timestamp: §4.0 gives the
  -- broker a held connection, so this is an observation rather than the guess a last-seen
  -- column forces on every reader -- and the guess is what let a session killed on Tuesday
  -- read as healthy on Friday.
  --
  -- A count rather than a flag, because an agent may have two sessions open. With a flag the
  -- second one closing says the agent is gone while the first is still mid-answer, and the
  -- next attach releases work somebody is doing -- which is exactly the failure the timestamp
  -- lease this replaced was invented to avoid. Counting keeps that property without needing an
  -- expiry to guess at.
  connected       INTEGER NOT NULL DEFAULT 0 CHECK (connected >= 0),
  -- When that last changed. For reports, never for decisions; nothing routes on it.
  changed_utc     TEXT,
  -- Two values, because there are only two judgements a person makes about a registration:
  -- it stands, or it is finished. Everything else that used to live here was a description
  -- of reachability, which is now observed rather than stored -- see §14.
  status          TEXT    NOT NULL CHECK (status IN ('active', 'retired')),

  CHECK (adapter_config IS NULL
         OR (json_valid(adapter_config) AND length(adapter_config) <= 4000)),

  -- What this agent owns, in one line, written by whoever configured its launch. Bounded
  -- here because terseness is a security control (principle 2.2) and this text is charged
  -- against every agent's working memory once per peer, every time the roster is listed. The
  -- number is measured: a roster entry costs about 75 characters of structure before the
  -- statement, so ten peers at 100 cost roughly what one message is allowed to.
  --
  -- It must also render as what it says, on one line, because it is put into a list
  -- of peers that every agent reads: anything able to break the line, hide itself, or
  -- reorder what follows lets one entry imitate the entries around it.
  --
  -- The class below is every code point .NET reports as Control, Format, LineSeparator or
  -- ParagraphSeparator, collapsed into ranges. It is exact rather than approximate because
  -- it was derived by sweeping the whole of Unicode and comparing against OwnershipStatement,
  -- and OwnershipTests re-runs that sweep so the two cannot drift. Testing C0 alone did not
  -- hold and neither did guessing at blocks: probing found U+2028 and U+2029, which are line
  -- breaks wherever this is rendered; U+202E and U+061C, which reorder the display of what
  -- follows; U+00AD and U+FEFF, which are invisible; and U+E0020-U+E007F, the tag characters
  -- used to smuggle text past a reader entirely. Every one of them was accepted before.
  --
  -- The last clause catches an embedded NUL, which no range can: char(0) is the empty string
  -- in SQLite, so a class built from it matches everything and would refuse every statement.
  -- length() stops at a NUL while concatenation does not, so a value whose length is
  -- unchanged by appending a character is hiding bytes behind a terminator.
  CHECK (owns IS NULL
         OR (length(owns) <= 100
             AND owns NOT GLOB '*[' ||
             char(1) || '-' || char(31) || char(127) || '-' || char(159) ||
             char(173) || '-' || char(173) || char(1536) || '-' || char(1541) ||
             char(1564) || '-' || char(1564) || char(1757) || '-' || char(1757) ||
             char(1807) || '-' || char(1807) || char(2192) || '-' || char(2193) ||
             char(2274) || '-' || char(2274) || char(6158) || '-' || char(6158) ||
             char(8203) || '-' || char(8207) || char(8232) || '-' || char(8238) ||
             char(8288) || '-' || char(8292) || char(8294) || '-' || char(8303) ||
             char(65279) || '-' || char(65279) || char(65529) || '-' || char(65531) ||
             char(69821) || '-' || char(69821) || char(69837) || '-' || char(69837) ||
             char(78896) || '-' || char(78911) || char(113824) || '-' || char(113827) ||
             char(119155) || '-' || char(119162) || char(917505) || '-' || char(917505) ||
             char(917536) || '-' || char(917631)
             || ']*'
             AND length(owns) = length(owns || '.') - 1)),

  -- The grammar of §4.1.1, enforced here rather than in the server for the reason §9.3
  -- gives: an invariant the server holds is an invariant only while the server is the only
  -- writer. There is no clause tying the name to its parts, because generation does that: the
  -- parts are computed from the name and cannot be set independently of it.
  --
  -- What is left bounds each part and refuses anything outside its class, which is what stops a
  -- name carrying a space, a control character, or a second '@' that would make "which part is
  -- the machine" a question with two answers.
  CHECK (length(role) BETWEEN 1 AND 32
         AND role GLOB '[a-z]*'
         AND role NOT GLOB '*[^a-z0-9-]*'),
  CHECK (length(machine) BETWEEN 1 AND 64
         AND machine GLOB '[a-z0-9]*'
         AND machine NOT GLOB '*[^a-z0-9.-]*')
) STRICT;

CREATE TABLE agent_credentials (
  agent     TEXT NOT NULL REFERENCES agents(name) ON DELETE CASCADE,
  name      TEXT NOT NULL,                -- the adapter's name for it: api_key, org_token
  reference TEXT NOT NULL,

  PRIMARY KEY (agent, name),
  CHECK (length(name) BETWEEN 1 AND 64 AND name NOT GLOB '*[^a-z0-9_]*'),
  CHECK (reference LIKE 'env:_%'
         OR reference LIKE 'keychain:_%'
         OR reference LIKE 'file:_%')
) STRICT;

-- What giving an agent a turn has cost. Unbounded on purpose, like Notify (§17 question 1): the
-- decision about whether to bound it waits for evidence, and the evidence must outlive the
-- messages that produced it. An agent that answers without being asked is an agent that bills
-- without being asked, so what is recorded is chosen to make a runaway visible early -- not only
-- how many, but how long they took and how often they achieved nothing.
--
-- No output is kept. What the agent said travels as messages under §9.4's retention; capturing
-- it here would make a second, unpruned copy of everything an agent ever wrote, outside every
-- guard that governs the first.
CREATE TABLE invocations (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  agent         TEXT    NOT NULL REFERENCES agents(name),
  -- Before and after, so the record can say whether the turn changed anything. An agent that
  -- ran and is still owed as much as it was is the one worth noticing.
  --
  -- "Waiting" rather than "owed", because the two stopped being the same thing: an answer to a
  -- question this agent asked leaves no debt at all, and an uncollected message is a reason to
  -- give an agent a turn that no count of debts can see. That distinction is what let an agent
  -- sit connected beside an uncollected message for twenty minutes with nothing wrong anywhere.
  waiting_before INTEGER NOT NULL CHECK (waiting_before >= 0),
  waiting_after  INTEGER NOT NULL CHECK (waiting_after >= 0),
  started_utc   TEXT    NOT NULL,
  elapsed_ms    INTEGER NOT NULL CHECK (elapsed_ms >= 0),
  -- The turn's verdict: zero when it ran to a clean finish. Named for a process exit code
  -- because it once was one, back when a turn and a process were the same thing.
  exit_code     INTEGER NOT NULL,
  -- A timeout is not a failure of the agent: it is a turn that took longer than the supervisor
  -- was willing to wait for, and the two need different responses. It does not end the agent --
  -- see §11.1.
  timed_out     INTEGER NOT NULL DEFAULT 0 CHECK (timed_out IN (0, 1)),
  -- What the runner charged, as the runner reported it.
  cost_usd      REAL    NOT NULL DEFAULT 0.0 CHECK (cost_usd >= 0.0),
  -- How many model turns went into one Parley turn. The count separates an agent that answered
  -- a question from one that went and did an afternoon's work on the strength of it.
  model_turns   INTEGER NOT NULL DEFAULT 0 CHECK (model_turns >= 0),
  -- Tool calls refused for want of permission. A resident agent runs with nobody at the
  -- keyboard, so a call needing approval is not paused for a human -- it is refused, and the
  -- agent answers anyway and worse. Nothing else in the record distinguishes that from an agent
  -- that has simply become unhelpful.
  permission_denials INTEGER NOT NULL DEFAULT 0 CHECK (permission_denials >= 0)
) STRICT;

CREATE INDEX ix_invocations_agent ON invocations(agent, started_utc);

-- A machine appears in `nodes` the first time it registers an agent, which is what that table
-- means: one row per machine that has ever had agents on it. Written as a trigger rather than
-- left to the caller because `agents.machine` references it, so every writer would otherwise
-- have to insert the parent first and in the right order -- an obligation nothing states, that
-- fails as a foreign key error naming neither the machine nor the agent, and that a second
-- implementation would have to rediscover.
--
-- The machine is recomputed here from NEW.name rather than read from NEW.machine, because a
-- generated column is not guaranteed to be populated in a BEFORE trigger. The expression is the
-- same one the column is generated by, and it has to stay that way: the two disagreeing would
-- create a node row nothing referenced while the insert failed anyway.
--
-- `connected` starts at zero. Registering an agent says a machine exists, never that anything
-- on it is running -- and a node that reported itself up merely by being mentioned would be the
-- stored-liveness mistake of section 14 arriving through a side door.
CREATE TRIGGER tr_agent_declares_its_machine
BEFORE INSERT ON agents
BEGIN
  INSERT INTO nodes (machine, connected, first_seen_utc)
  VALUES (substr(NEW.name, instr(NEW.name, '@') + 1), 0, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
  ON CONFLICT(machine) DO NOTHING;
END;

-- Two trigger pairs stood here and both are gone, which is worth recording rather than
-- silently dropping.
--
-- The first refused a row that consented to unattended invocation without recording how to
-- start it. Neither column exists now: how to start an agent is the node's local
-- configuration and never leaves the machine it applies to (§4), so the broker holds no
-- executable command line at all -- a smaller trusted surface than the trigger was
-- protecting.
--
-- The second refused a status that was really a reading of a timestamp. There are now two
-- statuses and both are judgements a person made, so the CHECK on the column says everything
-- the trigger used to. Reachability is observed on a connection (§4.0) instead of inferred,
-- and lives in `connected` where it cannot be mistaken for a decision somebody recorded.

CREATE TABLE exchanges (
  id                INTEGER PRIMARY KEY AUTOINCREMENT,
  pair_key          TEXT    NOT NULL,
  asker             TEXT    NOT NULL REFERENCES agents(name),
  answerer          TEXT    NOT NULL REFERENCES agents(name),
  owed_by           TEXT    NOT NULL REFERENCES agents(name),
  state             TEXT    NOT NULL CHECK (state IN ('OPEN', 'CLOSED', 'ORPHANED')),
  -- Set when this exchange was opened by a question addressed to a role (§5.5), and null
  -- otherwise. It binds the exchanges of one fan-out together so an asker can tell which
  -- answers belong to the same question; it does nothing else, and in particular no rule in §6
  -- or §7 reads it. That is deliberate -- a group question is N ordinary exchanges, and giving
  -- the group any authority over their states would make it the plural exchange §5.5 declines
  -- to introduce.
  --
  -- Opaque and generated by the broker rather than supplied. An asker choosing its own would be
  -- able to bind together exchanges that were never one question.
  ask_group         TEXT
                    CHECK (ask_group IS NULL
                           OR (length(ask_group) BETWEEN 1 AND 64
                               AND ask_group NOT GLOB '*[^a-zA-Z0-9._-]*')),
  clarify_depth     INTEGER NOT NULL DEFAULT 0,
  -- The column default is not the protocol default, which is 200 (17 question 4). The
  -- broker resolves a policy before admission and always writes this column explicitly,
  -- so this value is reached only by a row written by hand. Left at 3 deliberately: a
  -- hand-written row is not a measurement, and the bound that matters is the trigger
  -- below rather than this.
  clarify_depth_cap INTEGER NOT NULL DEFAULT 3,
  open_pair_cap     INTEGER NOT NULL DEFAULT 5
                    CHECK (open_pair_cap BETWEEN 1 AND 50),
  delivery_window_ms INTEGER NOT NULL,
  working_window_ms  INTEGER NOT NULL,
  -- Carried per exchange for the same reason the windows are, and it is the same argument:
  -- an exchange must be able to reproduce its own deadlines from its own row. Held globally,
  -- an operator lengthening the grace period would retroactively revive orphans already past
  -- the old one, and shortening it would ghost a batch of live orphans at once.
  grace_period_ms    INTEGER NOT NULL,
  opened_utc        TEXT    NOT NULL,
  expires_utc       TEXT    NOT NULL,
  closed_utc        TEXT,
  -- Which of the two windows in §6 is running. Derivable from the question message, and
  -- denormalised here anyway: the reaper scans exchanges and would otherwise join to messages
  -- on every sweep to answer a question the exchange already knows. It also makes the window
  -- transition legible in the table rather than implied by an opaque expires_utc.
  delivered_utc     TEXT,
  is_decision       INTEGER NOT NULL DEFAULT 0,
  decision_ref      TEXT,
  question_hash     TEXT    NOT NULL,      -- content identity, §13.1
  -- NULL while open. Set when the exchange closes, and the only column that distinguishes a
  -- ghost from an answer once the message text has been pruned (§9.4). §15's health signals
  -- are computed from this, so it outlives what it describes.
  --
  -- A rollup, not the whole story. §5.3 resolves each sub-question separately and that detail
  -- lives in the answer payload, which retention eventually removes; this column is what
  -- remains. It records the least resolved outcome among the sub-questions, ordered
  -- ghosted < unknown < out_of_scope < answered, so an exchange counts as answered only when
  -- every part of it was. Rolling up the other way would let one settled sub-question hide
  -- four unsettled ones, and the resulting telemetry would report a healthier deployment the
  -- more the agents failed to answer.
  resolution        TEXT    CHECK (resolution IN ('answered','out_of_scope','unknown','ghosted')),
  conversation_id   TEXT    REFERENCES conversations(id),  -- authoritative; see messages
  version           INTEGER NOT NULL DEFAULT 0,

  CHECK (asker <> answerer),
  CHECK (owed_by IN (asker, answerer)),
  CHECK (clarify_depth >= 0 AND clarify_depth <= clarify_depth_cap),
  CHECK (delivery_window_ms > 0 AND working_window_ms > 0),
  -- Bounded in both directions, because both ends defeat the rule rather than merely tuning
  -- it. At zero an orphan is ghosted the instant it is created, so the resume policy in §14
  -- never gets a chance to run and `always` becomes indistinguishable from `never`. At the
  -- top end the exchange effectively never closes, which restores the unbounded retention
  -- §9.4 relies on this to remove. One second to thirty days.
  CHECK (grace_period_ms BETWEEN 1000 AND 2592000000),
  CHECK ((state = 'CLOSED') = (closed_utc IS NOT NULL)),
  -- A closed exchange says why it closed; an open one cannot, since it has not.
  CHECK ((state = 'CLOSED') = (resolution IS NOT NULL)),
  CHECK (pair_key = min(asker, answerer) || char(31) || max(asker, answerer))
) STRICT;

-- Composite parent key for the denormalised copy on messages. Redundant for uniqueness,
-- since id is already the primary key, and required because SQLite resolves a composite
-- foreign key only against indexed parent columns.
CREATE UNIQUE INDEX ux_exchange_conversation ON exchanges(id, conversation_id);

-- Created by the broker on first reference, inside the same transaction that admits the
-- Question naming it, and idempotently: two agents may name the same new identifier at once
-- and neither should fail. Agents never create conversations explicitly, because a
-- conversation is not a thing to be set up before use — it is whatever its participants said
-- belonged together.
--
-- A conversation exists as a row so that it can carry a state. While it was only a string
-- on messages, no rule could ask whether a discussion was over without reconstructing it
-- from the traffic, and §9.4's idle rule could not be evaluated at all: it reasons about the
-- exchanges in a conversation, and exchanges did not carry the identifier.
CREATE TABLE conversations (
  id          TEXT PRIMARY KEY,
  state       TEXT NOT NULL CHECK (state IN ('OPEN', 'CLOSING', 'CLOSED')),
  opened_utc  TEXT NOT NULL,
  closed_utc  TEXT,

  CHECK ((state = 'CLOSED') = (closed_utc IS NOT NULL)),
  -- A conversation is named by an agent and exported by name (SPEC.md 10.2), so this value
  -- reaches a filename, a report heading or a console. No '/' and no ':', because both are
  -- path syntax, and no '..' anywhere, because a run of dots is path syntax without needing a
  -- separator. This is the authoritative name: exchanges reference it and messages carry a copy
  -- held in agreement by a composite foreign key, so constraining it here is what constrains it
  -- everywhere.
  --
  -- It was previously responder_model's character set, which permits '/'. That set is right for
  -- a model identifier -- 'some-org/some-model-9' is a real name -- and wrong for this, and the
  -- comment claiming to keep path separators out sat directly above a GLOB that admitted them.
  -- '../../etc/passwd' was a legal conversation id.
  CHECK (length(id) BETWEEN 1 AND 64
         AND id NOT GLOB '*[^a-zA-Z0-9._-]*'
         AND id NOT GLOB '*..*')
) STRICT;

-- Membership is derived, not declared: a row appears when an agent first becomes party to
-- an exchange carrying the identifier. The vote column is the closure poll of §5.0. NULL
-- means not yet asked or not yet answered, which are the same thing to the rule that reads
-- it, since silence assents.
CREATE TABLE conversation_participants (
  conversation_id TEXT NOT NULL REFERENCES conversations(id),
  agent           TEXT NOT NULL REFERENCES agents(name),
  joined_utc      TEXT NOT NULL,
  vote            TEXT CHECK (vote IN ('done', 'not_done')),
  polled_utc      TEXT,
  voted_utc       TEXT,

  PRIMARY KEY (conversation_id, agent),

  -- A vote without a poll is an agent answering a question nobody asked.
  CHECK (vote IS NULL OR polled_utc IS NOT NULL),
  CHECK ((vote IS NULL) = (voted_utc IS NULL))
) STRICT;

CREATE TRIGGER tr_open_pair_cap BEFORE INSERT ON exchanges WHEN NEW.state = 'OPEN'
BEGIN
  SELECT RAISE(ABORT, 'open_pair_cap reached')
   WHERE (SELECT count(*) FROM exchanges
           WHERE state = 'OPEN' AND asker = NEW.asker AND answerer = NEW.answerer)
         >= NEW.open_pair_cap;
END;

-- The clarify cap is itself bounded (§9.3), for the reason open_pair_cap is: a value written
-- outside the band would disable the rule while satisfying every constraint. A trigger rather
-- than a CHECK because the bound arrived after the table existed, and SQLite cannot add a CHECK
-- to a table that messages already references by foreign key.
CREATE TRIGGER tr_clarify_cap_bounded_on_insert BEFORE INSERT ON exchanges
FOR EACH ROW WHEN NEW.clarify_depth_cap NOT BETWEEN 0 AND 1000
BEGIN
  SELECT RAISE(ABORT, 'clarify_depth_cap must be between 0 and 1000');
END;

-- Guards a path no operation takes today. The cap is written once at admission, and that is a
-- fact about the current broker rather than about the schema: raising it on a live exchange
-- would grant depth retroactively that was refused when it was asked for.
CREATE TRIGGER tr_clarify_cap_bounded_on_update BEFORE UPDATE OF clarify_depth_cap ON exchanges
FOR EACH ROW WHEN NEW.clarify_depth_cap NOT BETWEEN 0 AND 1000
BEGIN
  SELECT RAISE(ABORT, 'clarify_depth_cap must be between 0 and 1000');
END;

-- Distinctness (§7.1). An index rather than a trigger: it is declarative, and it covers
-- every path that can produce an OPEN row, including the UPDATE that resumes an orphan.
CREATE UNIQUE INDEX ux_open_question
  ON exchanges(asker, answerer, question_hash) WHERE state = 'OPEN';



-- A ghost asserts that nobody answered (§14), so an exchange carrying an answer cannot be
-- one. Structural rather than textual: it does not inspect the answer, only whether one
-- exists, so there is nothing to word around.
CREATE TRIGGER tr_ghost_needs_no_answer BEFORE UPDATE ON exchanges
WHEN NEW.resolution = 'ghosted'
BEGIN
  SELECT RAISE(ABORT, 'ghosted requires that no answer was given')
   WHERE EXISTS (SELECT 1 FROM messages WHERE exchange_id = NEW.id AND type = 'answer');
END;

-- Section 5.0: closure is terminal, so a vote cast against a concluded discussion is an
-- agent answering a question that has already been settled. It changes no state -- nothing
-- reopens a closed conversation -- but it is stored, and the row then reads as a participant
-- declining a closure that had already happened. The log is what an operator reads when
-- something has gone wrong, and a stored contradiction is worse there than a refused write.
-- A CHECK cannot express this: it has to look at another table.
CREATE TRIGGER tr_no_vote_after_closure BEFORE UPDATE ON conversation_participants
WHEN NEW.vote IS NOT NULL
BEGIN
  SELECT RAISE(ABORT, 'conversation is closed')
   WHERE EXISTS (SELECT 1 FROM conversations
                  WHERE id = NEW.conversation_id AND state = 'CLOSED');
END;

-- Section 5.0: a closed conversation stays closed. Closure is by consensus, and a single
-- agent naming a concluded identifier on a new Question would reopen the discussion
-- unilaterally -- defeating the poll, the decline veto and the silence rule all at once. In
-- storage rather than in the broker because the exchange is what carries the membership: an
-- exchange in a closed conversation is the state that must not exist, so the place to refuse
-- it is where it would be written. Reopening is not a special case because it is not a real
-- one; a discussion worth resuming is a new one, and identifiers are free.
CREATE TRIGGER tr_closed_conversation_stays_closed BEFORE INSERT ON exchanges
WHEN NEW.conversation_id IS NOT NULL
BEGIN
  SELECT RAISE(ABORT, 'conversation is closed')
   WHERE EXISTS (SELECT 1 FROM conversations
                  WHERE id = NEW.conversation_id AND state = 'CLOSED');
END;

-- Resuming an orphan is an UPDATE, which the trigger above never sees. Both are required.
CREATE TRIGGER tr_open_pair_cap_resume BEFORE UPDATE ON exchanges
WHEN NEW.state = 'OPEN' AND OLD.state <> 'OPEN'
BEGIN
  SELECT RAISE(ABORT, 'open_pair_cap reached on resume')
   WHERE (SELECT count(*) FROM exchanges
           WHERE state = 'OPEN' AND asker = NEW.asker AND answerer = NEW.answerer
                 AND id <> NEW.id)
         >= NEW.open_pair_cap;
END;

CREATE TABLE messages (
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
  CHECK (length(payload_json) <= 4000),
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

-- What the size cap actually refused, so the next value for it is measured rather than
-- judged. Rule 8.5 fixes a number that no evidence has ever been offered for; this is the
-- evidence, and §17 says the same thing about the clarify cap without ever collecting it.
--
-- **No payload is stored, ever.** Only how long it was. Recording the text that was refused
-- for being too long would keep the exact material the cap exists to keep out, put it beyond
-- the retention policy that governs messages, and make instrumenting a poisoning control a
-- poisoning vector. The measurement is the whole point and the text is none of it.
--
-- One row per agent and message type rather than one per event, so the table is bounded by
-- the registry rather than by how often an agent misbehaves: an agent looping on an oversized
-- answer updates a row instead of filling a disk. What is lost is the distribution; what is
-- kept is how often, how far over at worst, and how far over on average, which is what
-- choosing a number needs.
--
-- Deliberately outside the retention policy of §9.4, which is the one surprise here and so is
-- said rather than left to be noticed. That policy forgets message text on a schedule because
-- an unbounded log recreates the shared store the protocol replaces; these rows carry no text
-- at all, and their whole value is being readable years after the traffic they describe is
-- gone. A count pruned on the same schedule as the thing it counts answers nothing.
--
-- total is 64-bit because it accumulates: fifty thousand refusals at forty thousand characters
-- passes what 32 bits holds, which is one looping agent for an afternoon.
CREATE TABLE size_refusals (
  agent       TEXT    NOT NULL REFERENCES agents(name) ON DELETE CASCADE,
  type        TEXT    NOT NULL CHECK (type IN ('question', 'clarify', 'answer', 'notify')),
  refusals    INTEGER NOT NULL CHECK (refusals > 0),
  worst       INTEGER NOT NULL CHECK (worst > 0),
  total       INTEGER NOT NULL CHECK (total > 0),
  -- The cap in force at the most recent refusal. Without it a row recorded under one cap is
  -- unreadable after the cap moves, which is precisely when this table is consulted.
  cap         INTEGER NOT NULL CHECK (cap > 0),
  last_utc    TEXT    NOT NULL,

  PRIMARY KEY (agent, type),

  CHECK (worst > cap),
  CHECK (total >= worst)
) STRICT;

-- The two triggers below sit after the table they guard rather than beside the rules they
-- enforce. SQLite compiles a trigger against a table that must already exist, so the
-- document's order is the order a database is built in, not a reading order.

-- Rule 2: gates are syntactic, unambiguous and acyclic. A trigger because reaching into an
-- array needs json_each, which is a subquery, and CHECK forbids those.
--
-- The second clause is the whole rule and does more than it appears to. Requiring a gate to
-- name an *earlier* sub-question makes the dependency graph a DAG by construction, so a cycle
-- cannot be expressed rather than being detected and rejected: no recursive walk, and nothing
-- to get subtly wrong. It subsumes three failures at once — a gate on a missing id, a gate on
-- itself, and a mutual pair each waiting on the other forever. Verified against all three plus
-- a plain forward reference.
--
-- Uniqueness is checked first because without it "the earlier sub-question named q1" is not a
-- single thing, and the second clause would be satisfied by whichever duplicate came first.
--
-- Scoped to questions, unlike the referral trigger below, and the difference is deliberate.
-- redirect_to is a name whose leak potential does not depend on which message carries it, so
-- validating it only where it is currently read would leave it unguarded the day something
-- else reads it. sub_questions are defined only for a Question and mean nothing elsewhere, so
-- there is no later reader to be caught out by. The scope also matters here: Notify is
-- expected to be the highest-volume traffic (§5.4), and it would otherwise pay for two
-- json_each scans over a path it never has.
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

-- Every referral names a registered agent. A trigger rather than a CHECK because reaching
-- inside a JSON array needs json_tree, which is a subquery: CHECK forbids those, triggers
-- permit them.
--
-- The key test is deliberately two-sided. json_tree reports an array element under a path
-- containing redirect_to, but a bare string under the parent's path with redirect_to as its
-- key, so matching on path alone silently ignores a malformed scalar — and a malformed value
-- that disables a guard is exactly how a rule becomes decorative. Verified against both forms.
--
-- Not restricted to answers, although only an answer carries a referral today. A redirect_to
-- in any other message type is inert, and inert data is how a guard lapses: the day something
-- reads it, the check that was scoped to the old reader is silently absent. The same mistake
-- as scoping the cap trigger to INSERT and meeting the resume path an UPDATE later.
-- Guarded on json_valid because SQLite runs BEFORE triggers ahead of CHECK constraints, and
-- json_tree raises 'malformed JSON' on a payload that is not JSON at all. Without the guard
-- this trigger preempts the json_valid CHECK below and reports a referral problem for a
-- message that has no referral, only broken syntax. A trigger should not answer for a
-- constraint that exists to answer for itself.
-- A ghost is the broker's, and this is the one place authorship can be enforced rather than
-- trusted. SPEC.md 11.3 is explicit that responder_model cannot distinguish the broker writing
-- its own name from the broker faithfully recording an adapter that supplied it, because every
-- row is written by the broker either way. That argument is about the value; it does not apply
-- to the type, which no agent operation can set. So the type gates the write and the check ties
-- the two together: a 'ghost' must carry the broker's attribution, and nothing else may.
CREATE TRIGGER tr_ghost_is_the_brokers BEFORE INSERT ON messages
WHEN NEW.type = 'ghost'
BEGIN
  SELECT RAISE(ABORT, 'a ghost message must record parley:broker as its responder')
   WHERE NEW.responder_model <> 'parley:broker';
END;

-- The converse does not hold and must not be added. 'parley:broker' is also what a forced
-- 'unknown' records when the clarify cap breaks (SPEC.md 7, 11.3, 15), which is an 'answer' the
-- broker writes -- so a trigger reserving the attribution for ghosts would refuse the other
-- broker-authored message the protocol already has. That an adapter cannot claim the namespace
-- is enforced where 11.3 says it must be, at the point adapter-supplied values enter the broker,
-- because every row here is written by the broker either way and the column cannot see which.

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

-- Referrals kept where retention cannot reach them. §15 wants the `out_of_scope` rate per pair
-- and calls it the signal that the ownership map is wrong somewhere; `redirect_to` lives only in
-- the payload, and §9.4 prunes by deleting the message, so that rate could never see further back
-- than the retention window. The same move `exchanges.resolution` already makes, and no prose:
-- three names, a sub-question identifier and a timestamp. §16 says this is a channel and not a
-- store, and names are not the log §9.4 is bounding.
CREATE TABLE message_referrals (
  -- Deliberately not a foreign key. A reference would make the prune fail once a referral
  -- pointed at the row; ON DELETE CASCADE would delete the referral with the message, which is
  -- what this table exists to prevent. It names a message that may no longer exist.
  message_id      INTEGER NOT NULL,
  -- This one is a real reference: nothing deletes an exchange, so the join stays sound.
  exchange_id     INTEGER REFERENCES exchanges(id),
  sub_question    TEXT    NOT NULL CHECK (length(sub_question) BETWEEN 1 AND 64),
  from_agent      TEXT    NOT NULL REFERENCES agents(name),
  to_agent        TEXT    NOT NULL REFERENCES agents(name),
  redirected_to   TEXT    NOT NULL REFERENCES agents(name),
  created_utc     TEXT    NOT NULL,

  PRIMARY KEY (message_id, sub_question, redirected_to),

  -- Both halves of the contradiction a referral can carry: out_of_scope says the boundary is not
  -- the sender's, so naming the sender denies itself, and naming the asker answers "not mine,
  -- ask yourself", which is not a referral.
  CHECK (redirected_to <> from_agent),
  CHECK (redirected_to <> to_agent),
  CHECK (from_agent <> to_agent)
) STRICT;

CREATE INDEX ix_referrals_pair ON message_referrals(from_agent, redirected_to);

-- Populated by the schema on §9.3's argument: an invariant application code maintains is one it
-- can route around, and a referral made but not recorded is invisible in the way a missing
-- telemetry signal is. Anchored at $.resolutions[*].redirect_to rather than searched for anywhere
-- in the payload, so what is recorded is a subset of what tr_referral_targets_exist validated.
-- json_tree over the sub-object takes both shapes §5.3 admits: a bare name is one text node, a
-- list is one per element.
--
-- DISTINCT rather than OR IGNORE. OR IGNORE discards a row failing any constraint, not only a
-- duplicate key, so it silently swallowed the CHECKs above -- an answer referring the asker back
-- to itself was accepted and recorded as nothing. The duplicate it absorbed is a repeated name in
-- one list, which is identical across every column, so DISTINCT absorbs it while a contradiction
-- still aborts.
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

-- What Notify costs, kept past the retention that removes the messages. §17 question 1 leaves
-- the type unbounded on purpose and says the decision comes from evidence; this is the evidence.
-- A rollup rather than a row per message, unlike message_referrals: a referral names a third
-- agent and a counter cannot hold which, while a notification's value is prose that is pruned
-- with the message and should be. Bounded by the roster, not by traffic.
CREATE TABLE notify_volume (
  from_agent  TEXT    NOT NULL REFERENCES agents(name),
  to_agent    TEXT    NOT NULL REFERENCES agents(name),
  sent        INTEGER NOT NULL CHECK (sent > 0),
  characters  INTEGER NOT NULL CHECK (characters > 0),
  -- A mean hides the shape: twenty short notices and one enormous one average out to something
  -- unremarkable and are completely different problems.
  largest     INTEGER NOT NULL CHECK (largest > 0),
  -- Both ends, because a rate needs a span. Twenty in an hour and twenty in a month are the same
  -- count and opposite findings.
  first_utc   TEXT    NOT NULL,
  last_utc    TEXT    NOT NULL,

  PRIMARY KEY (from_agent, to_agent),

  CHECK (from_agent <> to_agent),
  -- A total smaller than its own largest member, or than one character per message, means the
  -- accumulation went wrong -- which is invisible in a number that only ever grows.
  CHECK (characters >= largest),
  CHECK (characters >= sent)
) STRICT;

-- Length is measured on payload_json, which is what rule 8.5 caps, so these sizes are directly
-- comparable to the limit they are weighed against. The §13 envelope is broker-authored and the
-- same for every message, so counting it would add a constant and change no comparison.
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

-- Whatever the message log still holds, counted when the table arrives. Without it the counter
-- starts at zero on a deployment that has been running for weeks, and its first reading
-- understates the volume by exactly what was already sent. A floor rather than a complete
-- history: the messages retention has already removed cannot be counted.
INSERT INTO notify_volume (from_agent, to_agent, sent, characters, largest, first_utc, last_utc)
SELECT from_agent, to_agent, count(*), sum(length(payload_json)), max(length(payload_json)),
       min(created_utc), max(created_utc)
  FROM messages
 WHERE type = 'notify'
 GROUP BY from_agent, to_agent;

CREATE UNIQUE INDEX ux_message_dedup
  ON messages(content_hash, dedup_bucket);
