-- Parley schema, migration 001: the whole of it.
-- Applied inside a transaction by SchemaMigrator; do not add BEGIN/COMMIT here.
--
-- SPEC.md §9 is the authority for everything below, and tests/Parley.Spec.Tests executes that
-- document's fragments against SQLite in the order written. The two must agree; the document is
-- the specification and this is the implementation of it.
--
-- Present rules, each held here rather than in code:
--   * The message cap (8,000 characters) is a CHECK, so no code path can route around it (§9.3).
--   * The two caps that are off by default (§7.1, §5.2) are spelled 2147483647; the triggers still
--     hold a real cap to its band, so a bug writing a large number is refused, not a silent "off".
--   * Nothing referencing conversations(id) cascades on delete: a destroy deletes in order inside one
--     transaction with foreign keys deferred, so a table nobody remembered fails the whole destroy.
--   * Message rows are never rewritten; §9.4 prunes text, it does not edit it.
--   * A store written by another build is not upgraded: reset.ps1 and reset.sh clear a developer's stores.

CREATE TABLE nodes (
  -- Detected by the node, per §4.1.1. The primary key rather than a surrogate, because
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
  -- The repository's own identifier (`parleyAgentId`, §13), which is what an agent really is: the name is only what
  -- is shown for it. NULL for the two participants the broker provides, and for a row a repository without
  -- one registered. An attach that presents an identity already held under another name renames that row, and every
  -- table referencing it is moved with it (§4.1.1). Lower-case hyphenated hex, so two spellings of one
  -- identifier cannot be two agents. The identifier belongs to a repository and a committed `.mcp.json` carries it to
  -- every machine, so it is unique per machine (ux_agent_identity), never alone.
  identity        TEXT
                  CHECK (identity IS NULL
                         OR (length(identity) = 36 AND identity NOT GLOB '*[^0-9a-f-]*')),
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
  -- The node that reported this agent. Detected there, so two machines
  -- cannot both claim to be server-b by accident -- see §4.1.1.
  --
  -- NULL for the two participants the broker provides, the person seat and the facilitator
  -- (§12.3). Neither is hosted on a machine: the part after '@' in their names says what this
  -- store serves (§4.1.1), not where anything runs. A machine entry could not simply be left out:
  -- the key below would refuse the row. A NULL machine is allowed by the key and says what is
  -- true. The checks after the columns hold that only these two may have none, and hold their
  -- second half to the grammar §4.1.1 gives it.
  machine         TEXT
                  GENERATED ALWAYS AS (
                    CASE WHEN substr(name, 1, instr(name, '@') - 1)
                              IN ('human-user', 'parley-facilitator')
                         THEN NULL
                         ELSE substr(name, instr(name, '@') + 1)
                    END) STORED
                  REFERENCES nodes(machine),
  adapter         TEXT    NOT NULL,
  tier            TEXT    NOT NULL CHECK (tier IN ('full', 'pull', 'headless')),
  workspace       TEXT,
  session_ref     TEXT,                    -- adapter-specific resume handle
  adapter_config  TEXT,                    -- opaque to the broker (see §12)
  owns            TEXT,                    -- one line, read by peers (see §17 q10)
  -- How many of this agent's adapters are connected right now, raised on connect and lowered
  -- on disconnect, and written by nothing else. Not derived from a timestamp: §4.0 gives the
  -- broker a held connection, so this is an observation rather than a last-seen guess.
  --
  -- A count rather than a flag, because an agent may have two sessions open. With a flag the
  -- second one closing says the agent is gone while the first is still mid-answer, and the
  -- next attach releases work somebody is doing.
  connected       INTEGER NOT NULL DEFAULT 0 CHECK (connected >= 0),
  -- When that last changed. For reports, never for decisions; nothing routes on it.
  changed_utc     TEXT,
  -- Two values, because there are only two judgements a person makes about a registration:
  -- it stands, or it is finished. Reachability is observed, not stored -- see §14.
  status          TEXT    NOT NULL CHECK (status IN ('active', 'retired')),

  CHECK (adapter_config IS NULL
         OR (json_valid(adapter_config) AND length(adapter_config) <= 4000)),

  -- Only the two participants the broker provides have no machine (§4.1.1).
  CHECK (machine IS NOT NULL OR role IN ('human-user', 'parley-facilitator')),

  -- Their second half is not a machine, so it is held to the grammar §4.1.1 gives it instead. The
  -- machine CHECK passes on NULL, so without this the part after '@' would be checked by nothing.
  CHECK (machine IS NOT NULL
         OR (length(substr(name, instr(name, '@') + 1)) BETWEEN 1 AND 32
             AND substr(name, instr(name, '@') + 1) GLOB '[a-z]*'
             AND substr(name, instr(name, '@') + 1) NOT GLOB '*[^a-z0-9-]*')),

  -- Neither owns a repository, so neither has an ownership line. Here rather than in code
  -- because every attach refreshes `owns` from the launch, and something attaching under the
  -- person's name would otherwise put its own line in every agent's roster as the person's
  -- (§12.3). This one holds whoever attaches.
  CHECK (owns IS NULL OR role NOT IN ('human-user', 'parley-facilitator')),

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
         OR (length(owns) <= 2147483647
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

CREATE TABLE agent_ownership_accepted (
  -- What a person agreed this agent owns, and the evidence of how they arrived at it.
  --
  -- SEPARATE FROM agents.owns ON PURPOSE. This is what somebody accepted; agents.owns is what
  -- peers actually read, and it is filled from the launch environment when the agent attaches
  -- (SPEC.md §12.3). Until the agent restarts the two differ, and a surface says so by comparing
  -- them rather than by keeping a flag somebody has to remember to clear.
  agent           TEXT    NOT NULL PRIMARY KEY
                           REFERENCES agents(name) ON DELETE CASCADE,
  statement        TEXT    NOT NULL,
  -- Which route the line came by. A person's characters are never attributed to a model, so
  -- nothing here feeds size_refusals, which §17 question 16 reads.
  source           TEXT    NOT NULL CHECK (source IN ('proposed', 'edited', 'composed', 'automatic')),
  -- The length of the AGENT'S OWN message, or NULL where there was no proposal. It is the
  -- evidence a bound on agents.owns would be chosen from, as the message cap was chosen from
  -- `parley refusals`.
  proposed_length  INTEGER          CHECK (proposed_length IS NULL OR proposed_length >= 0),
  -- The discussion, so how a line was reached is answerable from the agent.
  -- Cleared, not refused, when that conversation is destroyed: the line is what peers route by and the
  -- conversation is evidence of how it was reached.
  conversation_id  TEXT             REFERENCES conversations(id) ON DELETE SET NULL,
  accepted_utc     TEXT    NOT NULL,

  CHECK (length(statement) >= 1),
  -- A composed line had no proposal; a line accepted automatically always had one, and its length is the
  -- evidence a bound would be chosen from. That every route but a composed one names its discussion is
  -- tr_accepted_line_names_its_discussion, below, rather than a CHECK here.
  CHECK (source <> 'composed' OR proposed_length IS NULL),
  CHECK (source <> 'automatic' OR proposed_length IS NOT NULL)
) STRICT;

CREATE TABLE invocations (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  agent         TEXT    NOT NULL REFERENCES agents(name),
  -- Before and after, so the record can say whether the turn changed anything. An agent that
  -- ran and is still owed as much as it was is the one worth noticing.
  --
  -- "Waiting" rather than "owed": an answer to a question this agent asked leaves no debt at
  -- all, and an uncollected message is a reason to give an agent a turn that no count of debts
  -- can see.
  waiting_before INTEGER NOT NULL CHECK (waiting_before >= 0),
  waiting_after  INTEGER NOT NULL CHECK (waiting_after >= 0),
  started_utc   TEXT    NOT NULL,
  elapsed_ms    INTEGER NOT NULL CHECK (elapsed_ms >= 0),
  -- The turn's verdict: zero when it ran to a clean finish.
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

CREATE TRIGGER tr_agent_declares_its_machine
BEFORE INSERT ON agents
WHEN substr(NEW.name, 1, instr(NEW.name, '@') - 1) NOT IN ('human-user', 'parley-facilitator')
BEGIN
  INSERT INTO nodes (machine, connected, first_seen_utc)
  VALUES (substr(NEW.name, instr(NEW.name, '@') + 1), 0, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
  ON CONFLICT(machine) DO NOTHING;
END;

CREATE TABLE conversations (
  id          TEXT PRIMARY KEY,
  state       TEXT NOT NULL CHECK (state IN ('OPEN', 'CLOSING', 'CLOSED')),
  opened_utc  TEXT NOT NULL,
  closed_utc  TEXT, archived_utc TEXT, closed_by TEXT
  CHECK (closed_by IS NULL OR closed_by IN ('consensus', 'person')),

  CHECK ((state = 'CLOSED') = (closed_utc IS NOT NULL)),
  -- A conversation is named by an agent and exported by name (SPEC.md 10.2), so this value
  -- reaches a filename, a report heading or a console. No '/' and no ':', because both are
  -- path syntax, and no '..' anywhere, because a run of dots is path syntax without needing a
  -- separator. This is the authoritative name: exchanges reference it and messages carry a copy
  -- held in agreement by a composite foreign key, so constraining it here is what constrains it
  -- everywhere.
  CHECK (length(id) BETWEEN 1 AND 64
         AND id NOT GLOB '*[^a-zA-Z0-9._-]*'
         AND id NOT GLOB '*..*')
) STRICT;

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

-- Refuses a vote cast or changed after the close. Moving a participant's row to its renamed agent (§4.1.1) changes
-- neither, so it is not a vote.
CREATE TRIGGER tr_no_vote_after_closure BEFORE UPDATE ON conversation_participants
WHEN NEW.vote IS NOT NULL
  AND (NEW.vote IS NOT OLD.vote OR NEW.voted_utc IS NOT OLD.voted_utc)
BEGIN
  SELECT RAISE(ABORT, 'conversation is closed')
   WHERE EXISTS (SELECT 1 FROM conversations
                  WHERE id = NEW.conversation_id AND state = 'CLOSED');
END;

CREATE TABLE destroyed_conversations (
  id            TEXT PRIMARY KEY,
  destroyed_utc TEXT NOT NULL,
  messages      INTEGER NOT NULL CHECK (messages >= 0),
  participants  TEXT NOT NULL
) STRICT;

CREATE TABLE "exchanges" (
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
  outcome             TEXT    CHECK (outcome IN ('complete', 'blocked', 'closed', 'settled')),
  settled_by          INTEGER REFERENCES exchanges(id),
  conversation_id     TEXT    NOT NULL REFERENCES conversations(id),
  version             INTEGER NOT NULL DEFAULT 0,

  CHECK (requester <> receiver),
  CHECK (owed_by IN (requester, receiver)),
  CHECK (clarify_depth >= 0 AND clarify_depth <= clarify_depth_cap),
  CHECK (clarify_depth_cap BETWEEN 0 AND 1000 OR clarify_depth_cap = 2147483647),
  CHECK (uncollected_flag_ms > 0 AND stalled_flag_ms > 0),
  CHECK ((state = 'CLOSED') = (closed_utc IS NOT NULL)),
  CHECK ((state = 'CLOSED') = (outcome IS NOT NULL)),
  CHECK ((outcome IS 'settled') = (settled_by IS NOT NULL)),
  CHECK (settled_by IS NULL OR settled_by <> id),
  CHECK (pair_key = min(requester, receiver) || char(31) || max(requester, receiver))
) STRICT;

CREATE TABLE "messages" (
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
  CHECK (length(payload_json) <= 8000),
  CHECK (json_valid(payload_json)),
  CHECK (length(responder_model) BETWEEN 1 AND 64
         AND responder_model NOT GLOB '*[^a-zA-Z0-9._:/-]*'),
  CHECK (length(content_hash) = 64
         AND content_hash NOT GLOB '*[^0-9a-f]*'),
  CHECK (unixepoch(created_utc) IS NOT NULL),

  FOREIGN KEY (exchange_id, conversation_id)
    REFERENCES "exchanges"(id, conversation_id)
) STRICT;

CREATE TABLE "size_refusals" (
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

CREATE TRIGGER tr_closed_conversation_stays_closed BEFORE INSERT ON exchanges
BEGIN
  SELECT RAISE(ABORT, 'conversation is closed')
   WHERE EXISTS (SELECT 1 FROM conversations
                  WHERE id = NEW.conversation_id AND state = 'CLOSED');
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

CREATE TABLE conversation_folders (
  conversation_id TEXT PRIMARY KEY REFERENCES conversations(id),
  -- Unique ignoring case, because Windows and macOS hold 'Pricing' and 'pricing' as one folder.
  -- NOCASE folds only ASCII, which is enough here: an identifier and its suffix are ASCII.
  folder          TEXT NOT NULL COLLATE NOCASE UNIQUE
                  CHECK (length(folder) BETWEEN 1 AND 72
                         AND folder NOT GLOB '*[^a-zA-Z0-9._~-]*'),
  created_utc     TEXT NOT NULL,

  -- The identifier itself, or the identifier and a suffix of 2 or more. '~' cannot occur in an
  -- identifier, so a suffixed folder can never be another conversation's own name.
  CHECK (folder = conversation_id COLLATE BINARY
         OR (substr(folder, 1, length(conversation_id) + 1) = conversation_id || '~'
             AND substr(folder, length(conversation_id) + 2) NOT GLOB '*[^0-9]*'
             AND substr(folder, length(conversation_id) + 2) NOT GLOB '0*'
             AND CAST(substr(folder, length(conversation_id) + 2) AS INTEGER) >= 2))
) STRICT;

CREATE TRIGGER tr_conversation_folder_is_fixed BEFORE UPDATE ON conversation_folders
BEGIN
  SELECT RAISE(ABORT, 'a conversation''s folder keeps its name');
END;

CREATE TABLE file_fetches (
  agent           TEXT NOT NULL REFERENCES agents(name) ON DELETE CASCADE,
  conversation_id TEXT NOT NULL REFERENCES conversations(id),
  name_key        TEXT NOT NULL CHECK (length(name_key) >= 1 AND name_key = upper(name_key)),
  name            TEXT NOT NULL,
  sha256          TEXT NOT NULL CHECK (length(sha256) = 64 AND sha256 NOT GLOB '*[^0-9a-f]*'),
  fetched_utc     TEXT NOT NULL,

  PRIMARY KEY (agent, conversation_id, name_key)
) STRICT;

CREATE TABLE file_notices (
  agent           TEXT    NOT NULL REFERENCES agents(name) ON DELETE CASCADE,
  conversation_id TEXT    NOT NULL,
  name_key        TEXT    NOT NULL CHECK (length(name_key) >= 1 AND name_key = upper(name_key)),
  name            TEXT    NOT NULL,
  sha256_had      TEXT    NOT NULL
                  CHECK (length(sha256_had) = 64 AND sha256_had NOT GLOB '*[^0-9a-f]*'),
  -- NULL when the file is gone, and then so is its size.
  sha256_now      TEXT    CHECK (sha256_now IS NULL
                                 OR (length(sha256_now) = 64 AND sha256_now NOT GLOB '*[^0-9a-f]*')),
  size_bytes      INTEGER CHECK (size_bytes IS NULL OR size_bytes >= 0),
  changed_utc     TEXT    NOT NULL,

  -- At most one per agent per file, carrying the latest state.
  PRIMARY KEY (agent, conversation_id, name_key),

  -- A notice that nothing changed cannot exist: a change back to what was fetched deletes the row.
  CHECK (sha256_now IS NULL OR sha256_now <> sha256_had),
  CHECK ((sha256_now IS NULL) = (size_bytes IS NULL))
) STRICT;

CREATE INDEX ix_file_notices_collection ON file_notices(agent, changed_utc);

CREATE TABLE file_placements (
  agent      TEXT    NOT NULL PRIMARY KEY,
  placements INTEGER NOT NULL CHECK (placements > 0),
  bytes      INTEGER NOT NULL CHECK (bytes >= 0),
  last_utc   TEXT    NOT NULL
) STRICT;

CREATE TABLE "shared_files" (
  conversation_id TEXT    NOT NULL REFERENCES conversation_folders(conversation_id),
  -- The name in upper case. Every name is ASCII, so SQLite's upper() is the key, and every key on a
  -- file is this: 'Readme.md' and 'README.md' are one row, as they are one file on the disks of
  -- Windows and macOS.
  name_key        TEXT    NOT NULL CHECK (name_key = upper(name)),
  -- As placed, after renaming. Made of letters A to Z, digits, '.', '_' and '-', which every disk on
  -- the three platforms accepts and which carries no text of the placer's beyond a word, ending .md
  -- or .txt so that nothing placed has a handler that runs a program (§13). The folder, a separator
  -- and the name together are bounded by tr_shared_file_path_fits.
  name            TEXT    NOT NULL
                  CHECK (length(name) BETWEEN 4 AND 254
                         AND (lower(name) GLOB '*.md' OR lower(name) GLOB '*.txt')
                         AND name NOT GLOB '*[^A-Za-z0-9._-]*'
                         AND name NOT GLOB '.*'
                         AND upper(substr(name, 1, instr(name, '.') - 1)) NOT IN
                             ('CON', 'PRN', 'AUX', 'NUL',
                              'COM1', 'COM2', 'COM3', 'COM4', 'COM5', 'COM6', 'COM7', 'COM8', 'COM9',
                              'LPT1', 'LPT2', 'LPT3', 'LPT4', 'LPT5', 'LPT6', 'LPT7', 'LPT8', 'LPT9')),
  size_bytes      INTEGER NOT NULL CHECK (size_bytes >= 0),
  -- The broker's SHA-256 of the stored bytes, never the placer's.
  sha256          TEXT    NOT NULL CHECK (length(sha256) = 64 AND sha256 NOT GLOB '*[^0-9a-f]*'),
  -- An agent, or NULL for a file the person put there. Text rather than a key: purge deletes these
  -- rows by name and reports them (§14), where a key would cascade silently or refuse the purge.
  placed_by       TEXT,
  placed_utc      TEXT    NOT NULL,
  changed_utc     TEXT    NOT NULL,
  -- The file's last-write time when it was last reconciled, so a change made outside Parley is
  -- noticed on the next list or fetch without hashing every file again.
  modified_ticks  INTEGER NOT NULL,

  PRIMARY KEY (conversation_id, name_key)
) STRICT;

CREATE TRIGGER tr_placer_is_registered BEFORE INSERT ON shared_files
WHEN NEW.placed_by IS NOT NULL
BEGIN
  SELECT RAISE(ABORT, 'a file is placed by a registered agent or by the person')
   WHERE NOT EXISTS (SELECT 1 FROM agents WHERE name = NEW.placed_by);
END;

CREATE TRIGGER tr_shared_file_stays_put
BEFORE UPDATE OF conversation_id, placed_by, placed_utc ON shared_files
WHEN NEW.conversation_id IS NOT OLD.conversation_id
  OR NEW.placed_utc IS NOT OLD.placed_utc
  OR (NEW.placed_by IS NOT OLD.placed_by
      AND NOT (NEW.placed_by IS NOT NULL AND OLD.placed_by IS NOT NULL
               AND EXISTS (SELECT 1 FROM agents WHERE name = NEW.placed_by)
               AND NOT EXISTS (SELECT 1 FROM agents WHERE name = OLD.placed_by)))
BEGIN
  SELECT RAISE(ABORT, 'a file keeps its conversation, its placer and when it was placed');
END;

CREATE TRIGGER tr_shared_file_path_fits BEFORE INSERT ON shared_files
BEGIN
  SELECT RAISE(ABORT, 'a file''s path inside the location is at most 255 characters')
   WHERE (SELECT length(folder) FROM conversation_folders
           WHERE conversation_id = NEW.conversation_id) + 1 + length(NEW.name) > 255;
END;

CREATE TRIGGER tr_shared_file_path_fits_on_rename BEFORE UPDATE OF name ON shared_files
BEGIN
  SELECT RAISE(ABORT, 'a file''s path inside the location is at most 255 characters')
   WHERE (SELECT length(folder) FROM conversation_folders
           WHERE conversation_id = NEW.conversation_id) + 1 + length(NEW.name) > 255;
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

-- The facilitator (principle 2.11) is a registry row the broker writes itself (§12.3), because every
-- sender is a row: messages, exchanges and participants all reference agents(name). Its role is
-- reserved, and the reservation is held here rather than at the doors that refuse it first, because a
-- door written next year would pass -- and what it would forge is the one sender every agent is asked
-- by without having asked for anything.
--
-- The role and the adapter are tied in both directions. A row with the role and another adapter is an
-- agent claiming to be Parley; a row with the adapter and another role is something every surface would
-- show as Parley. `parley` alone is NOT reserved: it is an ordinary role, which a repository called
-- Parley is enrolled under.
--
-- The role is recomputed from NEW.name because a generated column is not guaranteed in a BEFORE trigger
-- (see tr_agent_declares_its_machine). Update is covered as well as insert, because registration
-- updates an agent's configuration on every launch.
CREATE TRIGGER tr_facilitator_role_is_reserved
BEFORE INSERT ON agents
WHEN (substr(NEW.name, 1, instr(NEW.name, '@') - 1) = 'parley-facilitator')
     <> (NEW.adapter = 'parley-facilitator')
BEGIN
  SELECT RAISE(ABORT, 'the role parley-facilitator and its adapter belong to the facilitator alone');
END;

CREATE TRIGGER tr_facilitator_role_is_reserved_on_update
BEFORE UPDATE OF name, adapter ON agents
WHEN (substr(NEW.name, 1, instr(NEW.name, '@') - 1) = 'parley-facilitator')
     <> (NEW.adapter = 'parley-facilitator')
BEGIN
  SELECT RAISE(ABORT, 'the role parley-facilitator and its adapter belong to the facilitator alone');
END;

-- Never retired and never deleted (§14). A facilitator that could be withdrawn would leave every
-- question it asked with a sender that no longer exists, and a project nobody is asking.
CREATE TRIGGER tr_facilitator_is_never_retired
BEFORE UPDATE OF status ON agents
WHEN NEW.adapter = 'parley-facilitator' AND NEW.status <> 'active'
BEGIN
  SELECT RAISE(ABORT, 'the facilitator cannot be retired');
END;

CREATE TRIGGER tr_facilitator_is_never_purged
BEFORE DELETE ON agents
WHEN OLD.adapter = 'parley-facilitator'
BEGIN
  SELECT RAISE(ABORT, 'the facilitator cannot be purged');
END;

-- The person seat (§12.3): `human-user@...`, which the broker writes when it opens a store and
-- never releases. Keyed on the role rather than the seat adapter, because a seat taken under a
-- typed name carried that adapter too; the role is the one fact only the person seat has.
CREATE TRIGGER tr_person_is_never_retired
BEFORE UPDATE OF status ON agents
WHEN substr(NEW.name, 1, instr(NEW.name, '@') - 1) = 'human-user' AND NEW.status <> 'active'
BEGIN
  SELECT RAISE(ABORT, 'the person seat cannot be retired');
END;

CREATE TRIGGER tr_person_is_never_purged
BEFORE DELETE ON agents
WHEN substr(OLD.name, 1, instr(OLD.name, '@') - 1) = 'human-user'
BEGIN
  SELECT RAISE(ABORT, 'the person seat cannot be purged');
END;

-- One person per store. However the row came to be written, a second is refused.
CREATE UNIQUE INDEX ux_one_person ON agents(role) WHERE role = 'human-user';

-- One row per repository per machine. Two names for one identity on one machine would be two agents answering to one
-- checkout, which is what a rename exists to prevent (§4.1.1).
CREATE UNIQUE INDEX ux_agent_identity ON agents(identity, machine) WHERE identity IS NOT NULL;

-- The role and the seat's adapter are tied in both directions (§12.3), as the facilitator's are. A row with the role and
-- another adapter is an agent claiming to be the person; a row with the adapter and another role is an agent the broker
-- would treat as a person, who may always name a conversation (principle 2.10). The door refuses both first, with a sentence.
CREATE TRIGGER tr_person_role_is_reserved
BEFORE INSERT ON agents
WHEN (substr(NEW.name, 1, instr(NEW.name, '@') - 1) = 'human-user')
     <> (NEW.adapter = 'parley-console')
BEGIN
  SELECT RAISE(ABORT, 'the role human-user and its adapter belong to the person seat alone');
END;

CREATE TRIGGER tr_person_role_is_reserved_on_update
BEFORE UPDATE OF name, adapter ON agents
WHEN (substr(NEW.name, 1, instr(NEW.name, '@') - 1) = 'human-user')
     <> (NEW.adapter = 'parley-console')
BEGIN
  SELECT RAISE(ABORT, 'the role human-user and its adapter belong to the person seat alone');
END;

-- Each question the facilitator asked (§10.2 `facilitation`). The latest row for an agent is its
-- current question; earlier rows are history, kept so asking again does not erase what was said.
--
-- The states nothing else records: an answer WAITING for a person, a question ASKED BACK that a
-- person must reply to, an answer DISCARDED. The exchange alone cannot say any of those.
--
-- It goes with its conversation, on a destroy or a purge (§14): a question about a discussion that no
-- longer exists is a row pointing at nothing. The exchange is keyed with its conversation, as a message's
-- is, so the two cannot disagree -- and with no action on delete, so a path that removed the exchange
-- alone would fail at its commit rather than leave a question about nothing.
CREATE TABLE ownership_questions (
  id               INTEGER PRIMARY KEY AUTOINCREMENT,
  agent            TEXT    NOT NULL REFERENCES agents(name) ON DELETE CASCADE,
  conversation_id  TEXT    NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
  exchange_id      INTEGER NOT NULL,
  state            TEXT    NOT NULL
                   CHECK (state IN ('asked', 'asked_back', 'answered', 'waiting', 'accepted', 'discarded')),
  -- The agent's words as written, and with runs of whitespace collapsed (§10.2). Bounded by rule 8.4's
  -- cap because they are copies of a message that already was.
  answer           TEXT    CHECK (answer IS NULL OR length(answer) <= 8000),
  flattened        TEXT    CHECK (flattened IS NULL OR length(flattened) <= 8000),
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

-- Never asked twice (§10.2): at most one live question per agent. A check in the code that asks would be
-- right until a broker restarted between its read and its write; this is right regardless.
CREATE UNIQUE INDEX ux_one_live_question ON ownership_questions(agent)
WHERE state IN ('asked', 'asked_back', 'answered', 'waiting');

-- Refreshed once, ever (§11.1).
CREATE UNIQUE INDEX ux_one_refresh ON ownership_questions(agent) WHERE restart_sent = 1;

-- A question is the facilitator's own Request to the agent it names, and starts asked. An agent can open an
-- `owns-` conversation itself, so recognising a question by its conversation's
-- name would let an agent answer itself and have the answer accepted with nobody looking. Neither the
-- facilitator nor a seat is asked: one owns nothing and the other is a person.
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

-- Accepted only once a line is recorded, so a question can never claim an acceptance nothing holds.
CREATE TRIGGER tr_question_accepted_only_with_a_line
BEFORE UPDATE OF state ON ownership_questions
WHEN NEW.state = 'accepted'
 AND NOT EXISTS (SELECT 1 FROM agent_ownership_accepted WHERE agent = NEW.agent)
BEGIN
  SELECT RAISE(ABORT, 'a question is accepted only once its line is recorded');
END;

-- Settled stays settled. Reopening a discarded answer would put it in front of a person, or make it a
-- line, with nobody having asked.
CREATE TRIGGER tr_settled_question_stays_settled
BEFORE UPDATE OF state ON ownership_questions
WHEN OLD.state IN ('accepted', 'discarded') AND NEW.state <> OLD.state
BEGIN
  SELECT RAISE(ABORT, 'a settled question stays settled');
END;

-- The project's Auto Accept switch (§10.2). One row, in the project's own store, because the store is
-- the one thing both surfaces reach and the broker reads without a second file to watch. A store with no
-- row reads as on: Auto Accept is on by default.
CREATE TABLE facilitation (
  id           INTEGER PRIMARY KEY CHECK (id = 1),
  auto_accept  INTEGER NOT NULL DEFAULT 1 CHECK (auto_accept IN (0, 1)),
  changed_utc  TEXT    NOT NULL
) STRICT;

-- Every route but a composed line names the discussion it came from. A trigger rather than a CHECK,
-- because that column is cleared when the conversation is destroyed and a CHECK would refuse the destroy. A
-- line agreed in a conversation that was later destroyed keeps the line and loses only the link.
--
-- Checked on insert, and on an update that changes the route. Clearing the link alone is not a new route.
CREATE TRIGGER tr_accepted_line_names_its_discussion
BEFORE INSERT ON agent_ownership_accepted
WHEN NEW.source <> 'composed' AND NEW.conversation_id IS NULL
BEGIN
  SELECT RAISE(ABORT, 'an accepted line names the conversation it was agreed in');
END;

CREATE TRIGGER tr_accepted_line_names_its_discussion_on_update
BEFORE UPDATE OF source ON agent_ownership_accepted
WHEN NEW.source <> 'composed' AND NEW.conversation_id IS NULL
BEGIN
  SELECT RAISE(ABORT, 'an accepted line names the conversation it was agreed in');
END;

-- Roadmaps (§5.7). A proposal is what an agent puts forward; nothing in it is a roadmap until a person
-- confirms it. Titles and items are single-line text with no length bound (principle 2.2 and §16): the
-- one thing stored here that a line break would break is the line, so a line break is refused.

-- What an agent proposed, apart from messages: it opens no exchange and is owed no answer. The proposer
-- is an agent that attached, never the person seat or Parley. A purged proposer takes its proposals
-- with it; a roadmap that came of one keeps the proposer's name as text.
CREATE TABLE roadmap_proposals (
  id                  INTEGER PRIMARY KEY,
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

CREATE TABLE roadmap_proposal_items (
  proposal_id    INTEGER NOT NULL REFERENCES roadmap_proposals(id) ON DELETE CASCADE,
  position       INTEGER NOT NULL CHECK (position >= 1),
  text           TEXT    NOT NULL
                 CHECK (length(trim(text, ' ' || char(9) || char(10) || char(13))) > 0
                        AND instr(text, char(10)) = 0 AND instr(text, char(13)) = 0),
  -- Who the item is meant for, where the proposer knew. A suggestion to the person and nothing more.
  intended_agent TEXT    REFERENCES agents(name) ON DELETE SET NULL,

  PRIMARY KEY (proposal_id, position)
) STRICT;

-- A dependency points only at an earlier item, which makes a cycle impossible rather than checked for.
CREATE TABLE roadmap_proposal_needs (
  proposal_id    INTEGER NOT NULL,
  position       INTEGER NOT NULL,
  needs_position INTEGER NOT NULL,

  PRIMARY KEY (proposal_id, position, needs_position),
  CHECK (needs_position >= 1 AND needs_position < position),
  FOREIGN KEY (proposal_id, position)
    REFERENCES roadmap_proposal_items(proposal_id, position) ON DELETE CASCADE,
  FOREIGN KEY (proposal_id, needs_position)
    REFERENCES roadmap_proposal_items(proposal_id, position) ON DELETE CASCADE
) STRICT;

-- A roadmap exists only once a person confirmed it. It carries no status: where an item stands is
-- derived from its conversation when read (§5.7) and is stored nowhere.
CREATE TABLE roadmaps (
  id              INTEGER PRIMARY KEY,
  -- Small, per store, never reused: the 'rm<number>' of every item conversation's name.
  number          INTEGER NOT NULL UNIQUE CHECK (number >= 1),
  title           TEXT    NOT NULL
                  CHECK (length(trim(title, ' ' || char(9) || char(10) || char(13))) > 0
                         AND instr(title, char(10)) = 0 AND instr(title, char(13)) = 0),
  origin          TEXT    NOT NULL CHECK (origin IN ('proposed', 'edited')),
  -- As text, so that neither pruning the proposal nor purging the agent loses who it began with.
  proposer        TEXT,
  origin_proposal INTEGER,
  revision        INTEGER NOT NULL DEFAULT 1 CHECK (revision >= 1),
  created_utc     TEXT    NOT NULL CHECK (unixepoch(created_utc) IS NOT NULL),
  updated_utc     TEXT    NOT NULL CHECK (unixepoch(updated_utc) IS NOT NULL)
) STRICT;

-- Items are never deleted, because a removed item's conversation stays and says what it was.
CREATE TABLE roadmap_items (
  id             INTEGER PRIMARY KEY,
  roadmap_id     INTEGER NOT NULL REFERENCES roadmaps(id) ON DELETE CASCADE,
  number         INTEGER NOT NULL CHECK (number >= 1),
  position       INTEGER NOT NULL CHECK (position >= 1),
  text           TEXT    NOT NULL
                 CHECK (length(trim(text, ' ' || char(9) || char(10) || char(13))) > 0
                        AND instr(text, char(10)) = 0 AND instr(text, char(13)) = 0),
  intended_agent TEXT    REFERENCES agents(name) ON DELETE SET NULL,
  added_rev      INTEGER NOT NULL CHECK (added_rev >= 1),
  removed_rev    INTEGER,
  edited_utc     TEXT    CHECK (edited_utc IS NULL OR unixepoch(edited_utc) IS NOT NULL),

  UNIQUE (roadmap_id, number),
  CHECK (removed_rev IS NULL OR removed_rev > added_rev)
) STRICT;

-- Two live items never share a place in one roadmap's order. A removed item keeps the place it had.
CREATE UNIQUE INDEX ux_live_item_position ON roadmap_items(roadmap_id, position)
  WHERE removed_rev IS NULL;

CREATE TABLE roadmap_item_needs (
  item_id       INTEGER NOT NULL REFERENCES roadmap_items(id) ON DELETE CASCADE,
  needs_item_id INTEGER NOT NULL REFERENCES roadmap_items(id) ON DELETE CASCADE,

  PRIMARY KEY (item_id, needs_item_id),
  CHECK (item_id <> needs_item_id)
) STRICT;

-- A second layer, for a writer that forgets: a need names a live item of the same roadmap, earlier in its
-- order. The broker also checks the person's whole final content, because a reorder is not an insert and
-- this cannot see one (§5.7).
CREATE TRIGGER tr_a_need_is_live_and_earlier
BEFORE INSERT ON roadmap_item_needs
WHEN NOT EXISTS (SELECT 1
                   FROM roadmap_items i
                   JOIN roadmap_items n ON n.roadmap_id = i.roadmap_id
                  WHERE i.id = NEW.item_id AND n.id = NEW.needs_item_id
                    AND i.removed_rev IS NULL AND n.removed_rev IS NULL
                    AND n.position < i.position)
BEGIN
  SELECT RAISE(ABORT, 'an item needs only a live item that comes earlier in the same roadmap');
END;

-- The name an item's conversation will have, reserved when the roadmap is confirmed. No foreign key to
-- conversations: the conversation does not exist until work on the item starts, and existing is how a
-- surface knows it has. The name has the same character rules as any conversation's.
CREATE TABLE roadmap_item_conversations (
  item_id         INTEGER PRIMARY KEY REFERENCES roadmap_items(id) ON DELETE CASCADE,
  conversation_id TEXT    NOT NULL UNIQUE
                  CHECK (length(conversation_id) BETWEEN 1 AND 64
                         AND conversation_id NOT GLOB '*[^a-zA-Z0-9._-]*'
                         AND conversation_id NOT GLOB '*..*')
) STRICT;

-- Without this an agent could open a reserved name first, and the person pressing start would then speak
-- into a conversation the agent made. Only the first message is held: replies come after.
CREATE TRIGGER tr_a_reserved_conversation_opens_with_the_person
BEFORE INSERT ON messages
WHEN EXISTS (SELECT 1 FROM roadmap_item_conversations c WHERE c.conversation_id = NEW.conversation_id)
 AND NOT EXISTS (SELECT 1 FROM messages m WHERE m.conversation_id = NEW.conversation_id)
 AND NOT EXISTS (SELECT 1 FROM agents a
                  WHERE a.name = NEW.from_agent AND a.adapter = 'parley-console')
BEGIN
  SELECT RAISE(ABORT, 'a roadmap item''s conversation is opened by the person');
END;

-- What a proposer is told about its proposal. Not a message: it opens nothing, is owed nothing and wakes
-- nobody, as a change notice for a file does not (§9.5). The title is the agent's own.
CREATE TABLE roadmap_notices (
  id            INTEGER PRIMARY KEY,
  agent         TEXT    NOT NULL REFERENCES agents(name) ON DELETE CASCADE,
  proposal_id   INTEGER NOT NULL,
  title         TEXT    NOT NULL
                CHECK (instr(title, char(10)) = 0 AND instr(title, char(13)) = 0),
  outcome       TEXT    NOT NULL CHECK (outcome IN ('confirmed', 'declined', 'superseded')),
  created_utc   TEXT    NOT NULL CHECK (unixepoch(created_utc) IS NOT NULL),
  delivered_utc TEXT    CHECK (delivered_utc IS NULL OR unixepoch(delivered_utc) IS NOT NULL)
) STRICT;

CREATE INDEX ix_roadmap_notices_collection ON roadmap_notices(agent, delivered_utc);
