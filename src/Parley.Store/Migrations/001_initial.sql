-- Parley schema, migration 001.
-- Applied inside a transaction by SchemaMigrator; do not add BEGIN/COMMIT here.
--
-- Requires SQLite 3.38 or later: STRICT tables (3.37), generated columns (3.31), and
-- unixepoch() (3.38). An older engine fails at the first CREATE TABLE with an error that
-- does not say which feature it objected to. See SPEC.md section 9.2.

CREATE TABLE agents (
    name            TEXT    PRIMARY KEY,
    adapter         TEXT    NOT NULL,
    tier            TEXT    NOT NULL CHECK (tier IN ('full', 'pull', 'headless')),
    workspace       TEXT,
    session_ref     TEXT,
    -- Whatever the named adapter needs to invoke this agent: a model identifier, an endpoint,
    -- a timeout, a notification channel. Opaque here by design — the broker stores it and
    -- hands it back at invocation, and only the adapter interprets it. That opacity is what
    -- makes the registry vendor-agnostic in structure rather than merely in intent.
    --
    -- Free-form because it has no invariant. Credentials are the opposite case and live in
    -- agent_credentials below, where each one can be constrained. See SPEC.md section 12.
    adapter_config  TEXT,
    owns            TEXT,
    last_seen_utc   TEXT,
    status          TEXT    NOT NULL
                    -- 'unreachable' is not a worse 'offline'. Offline is the ordinary resting
                    -- state of a lazily spawned agent and must stay routable, or the traffic
                    -- meant to wake it would be refused. Unreachable means spawning was tried
                    -- and failed repeatedly, so routing here only burns a TTL. See SPEC.md 14.
                    -- 'retired' is the operator's judgement that a repository is gone for
                    -- good, and unlike the others it is not liveness: start-up clears the rest
                    -- and must leave this one alone. The row survives rather than being
                    -- deleted, because exchanges and messages reference it and SPEC.md 9.4
                    -- keeps exchange rows after pruning their text so telemetry outlives the
                    -- traffic. Deleting would cascade to the skeleton that exists to be kept.
                    CHECK (status IN ('registered', 'live', 'offline', 'unreachable', 'retired')),

    -- The column is named _config and readers parse it as JSON. Capped because an adapter
    -- configuration is a handful of settings, not a payload, and an unbounded one would be a
    -- place to hide things.
    CHECK (adapter_config IS NULL
           OR (json_valid(adapter_config) AND length(adapter_config) <= 4000)),

    -- One line saying what this agent owns, written by whoever configured its launch and read
    -- by every agent that lists the roster. See SPEC.md 17, question 10: without it the
    -- signal in the protocol is the name, and what each agent owns lives in one hand-written
    -- copy per repository.
    --
    -- Bounded here rather than in the code that writes it, because the argument for the bound
    -- is the argument for every other cap in this file: terseness is a security control, and
    -- this text is charged against every agent's working memory once per peer per listing.
    -- An order of magnitude below the 2000-character message cap, because a message is read
    -- once by one agent and this is read by all of them, repeatedly.
    --
    -- The statement must render as what it says, on one line, because it is put into a list
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

    -- The agent name grammar, mirroring AgentName's ^[a-z][a-z0-9-]{0,31}$. Every other
    -- constraint in this schema is built on this column: asker, answerer, owed_by and both
    -- message endpoints are foreign keys to it, and pair_key is derived by concatenating two
    -- of these values around char(31).
    --
    -- That derivation is only unambiguous while a name cannot itself contain char(31).
    -- Permit one and ('a', 'b<US>c') and ('a<US>b', 'c') collapse to the same pair_key, so
    -- ux_open_pair serialises two unrelated pairs against one slot while letting a genuine
    -- pair open twice. The pair_key CHECK below would not notice: the key it recomputes is
    -- equally ambiguous.
    --
    -- GLOB rather than LIKE because LIKE is case-insensitive for ASCII in SQLite, which
    -- would admit the uppercase names the grammar excludes. Inside a GLOB class a trailing
    -- hyphen is a literal, not a range.
    CHECK (length(name) BETWEEN 1 AND 32
           AND name GLOB '[a-z]*'
           AND name NOT GLOB '*[^a-z0-9-]*')
) STRICT;

-- Credentials are references, never values. SPEC.md section 12 claims the registry holds
-- pointers only, and that this is what makes a manifest safe to commit; a claim the storage
-- layer does not enforce is a convention, and conventions lose to one tired paste of an API
-- key, which then lands in backups and in any diagnostic that dumps the registry.
--
-- A row per credential rather than a JSON object on agents, because the constraint has to
-- reach every value. SQLite prohibits subqueries in CHECK constraints, and reaching inside a
-- JSON document needs json_each, which is one — so a JSON column could assert only that it
-- was JSON, not that its contents were references. A trigger could do it, but a row per
-- reference gets the same guarantee with the plain CHECK used everywhere else in this schema.
--
-- Plural by construction. An adapter needing an API key and an organisation token has two
-- rows, each addressable by the name its descriptor used, which is what the invocation path
-- looks them up by.
CREATE TABLE agent_credentials (
    -- Cascades because a credential row is a pointer that means nothing without its agent,
    -- and retiring an agent should not fail on an uninformative foreign key error. Nothing is
    -- lost that matters: the secret itself lives in a store Parley did not write to in the
    -- general case and does not delete from here, so what goes is the registration, which is
    -- recreated by registering again.
    agent       TEXT NOT NULL REFERENCES agents(name) ON DELETE CASCADE,
    -- The adapter's name for it: 'api_key', 'org_token'. Not the secret's name in the store.
    name        TEXT NOT NULL,
    reference   TEXT NOT NULL,

    PRIMARY KEY (agent, name),

    CHECK (length(name) BETWEEN 1 AND 64
           AND name NOT GLOB '*[^a-z0-9_]*'),

    CHECK (reference LIKE 'env:_%'
           OR reference LIKE 'keychain:_%'
           OR reference LIKE 'file:_%')
) STRICT;

-- A conversation exists as a row so it can carry a state. While it was only a string on
-- messages, no rule could ask whether a discussion was over without reconstructing it from the
-- traffic, and the idle rule in SPEC.md 9.4 could not be evaluated at all: it reasons about the
-- exchanges in a conversation, and exchanges did not carry the identifier.
--
-- Created by the broker on first reference, inside the transaction that admits the Question
-- naming it, and idempotently: two agents may name the same new identifier at once and neither
-- should fail.
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

-- Membership is derived, not declared: a row appears when an agent first becomes party to an
-- exchange carrying the identifier. The vote column is the closure poll of SPEC.md 5.0. NULL
-- means not yet asked or not yet answered, which are the same thing to the rule that reads it,
-- since silence assents.
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

CREATE TABLE exchanges (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    pair_key          TEXT    NOT NULL,
    asker             TEXT    NOT NULL REFERENCES agents(name),
    answerer          TEXT    NOT NULL REFERENCES agents(name),
    owed_by           TEXT    NOT NULL REFERENCES agents(name),
    state             TEXT    NOT NULL CHECK (state IN ('OPEN', 'CLOSED', 'ORPHANED')),
    clarify_depth     INTEGER NOT NULL DEFAULT 0,
    clarify_depth_cap INTEGER NOT NULL DEFAULT 3,
    -- SPEC.md 7.1. Carried per row rather than read from configuration, so an exchange is
    -- admitted on the terms it was opened under.
    open_pair_cap     INTEGER NOT NULL DEFAULT 5
                      CHECK (open_pair_cap BETWEEN 1 AND 50),
    -- Milliseconds, not seconds. Storing seconds truncated sub-second windows while
    -- expires_utc kept full precision, so a rehydrated exchange could not reproduce
    -- its own deadline.
    --
    -- Two windows, not one deadline (SPEC.md 6). A single inactivity timer cannot tell an
    -- agent that never received the question from one that received it and is working, and the
    -- second routinely lasts hours. Delivery is already recorded, so the split needs no
    -- acknowledgement message.
    delivery_window_ms INTEGER NOT NULL,
    working_window_ms  INTEGER NOT NULL,
    -- SPEC.md 14. Per row for the same reason the windows are: held globally, lengthening the
    -- setting would retroactively revive orphans already past the old period, and shortening it
    -- would ghost a batch of live ones at once.
    grace_period_ms    INTEGER NOT NULL,
    opened_utc        TEXT    NOT NULL,
    expires_utc       TEXT    NOT NULL,
    closed_utc        TEXT,
    -- Which of the two windows in SPEC.md 6 is running. Derivable from the question message,
    -- and denormalised here anyway: the reaper scans exchanges and would otherwise join to
    -- messages on every sweep to answer something the exchange already knows.
    delivered_utc     TEXT,
    is_decision       INTEGER NOT NULL DEFAULT 0 CHECK (is_decision IN (0, 1)),
    decision_ref      TEXT,
    -- Content identity, SPEC.md 13.1. Names what the question was, so ux_open_question can
    -- refuse the same question twice while it is still open.
    question_hash     TEXT    NOT NULL,
    -- Authoritative; messages carry a copy held in agreement by a composite foreign key.
    conversation_id   TEXT    REFERENCES conversations(id),
    -- NULL while open. Set when the exchange closes, and the only column distinguishing a
    -- ghost from an answer once the message text has been pruned. A rollup of the
    -- per-sub-question resolutions, taking the least resolved, so an exchange counts as
    -- answered only when every part of it was. See SPEC.md 5.3.
    resolution        TEXT    CHECK (resolution IN ('answered','out_of_scope','unknown','ghosted')),
    -- Optimistic concurrency stamp. Incremented on every write; a save that does not
    -- match the loaded value is rejected rather than silently overwriting. Without
    -- this, two paths that both load, transition, and save lose one transition, and
    -- the one most likely to be lost is a clarify_depth increment, which turns the
    -- depth cap into a suggestion.
    version           INTEGER NOT NULL DEFAULT 0,

    CHECK (asker <> answerer),
    CHECK (owed_by IN (asker, answerer)),
    CHECK (clarify_depth >= 0 AND clarify_depth <= clarify_depth_cap),
    CHECK (delivery_window_ms > 0 AND working_window_ms > 0),
    -- Bounded both ways because both ends defeat the rule rather than tuning it. At zero an
    -- orphan is ghosted the instant it is created, so the resume policy never runs. At the top
    -- the exchange effectively never closes, restoring the unbounded retention ghosting exists
    -- to remove. One second to thirty days.
    CHECK (grace_period_ms BETWEEN 1000 AND 2592000000),
    CHECK ((state = 'CLOSED') = (closed_utc IS NOT NULL)),
    -- A closed exchange says why it closed; an open one cannot, since it has not.
    CHECK ((state = 'CLOSED') = (resolution IS NOT NULL)),

    -- pair_key must genuinely be derived from the two participants. Cycle detection and the
    -- retention rules both key on it, and a mismatched key would make two unrelated pairs
    -- indistinguishable to every rule built on it. char(31) is the unit separator, and TEXT
    -- columns collate BINARY by default, which is the ordinal comparison PairKey.For uses.
    CHECK (pair_key = min(asker, answerer) || char(31) || max(asker, answerer))
) STRICT;

-- Composite parent key for the denormalised conversation on messages. Redundant for
-- uniqueness, since id is already the primary key, and required because SQLite resolves a
-- composite foreign key only against indexed parent columns.
CREATE UNIQUE INDEX ux_exchange_conversation ON exchanges(id, conversation_id);

-- Distinctness, SPEC.md 7.1. An index rather than a trigger: it is declarative, and it covers
-- every path that can produce an OPEN row, including the UPDATE that resumes an orphan. The
-- one-open-exchange-per-pair rule it replaces made repetition structurally impossible without
-- ever saying so, and removing that rule removed a guard nobody had written down.
CREATE UNIQUE INDEX ux_open_question
    ON exchanges (asker, answerer, question_hash) WHERE state = 'OPEN';

-- Reaper scan: find open exchanges past their deadline.
CREATE INDEX ix_open_expiry
    ON exchanges (expires_utc) WHERE state = 'OPEN';

-- Orphan replay on agent re-registration.
CREATE INDEX ix_orphaned_owed
    ON exchanges (owed_by) WHERE state = 'ORPHANED';

CREATE TABLE messages (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    -- NULL for Notify, which opens no exchange and takes no pair slot.
    exchange_id     INTEGER REFERENCES exchanges(id),
    -- Four an agent may send, and one only the broker writes. 'ghost' is SPEC.md 14's notice
    -- that no answer is coming, and it needs a type of its own because none of the other four
    -- can carry it: an 'answer' is refused on a ghosted exchange by tr_ghost_needs_no_answer,
    -- and a 'notify' may not name an exchange. tr_ghost_is_the_brokers guards who may write it.
    type            TEXT    NOT NULL CHECK (type IN ('question', 'clarify', 'answer', 'notify',
                                                     'ghost')),
    from_agent      TEXT    NOT NULL REFERENCES agents(name),
    to_agent        TEXT    NOT NULL REFERENCES agents(name),
    -- Which model actually produced this. See SPEC.md section 11.3.
    responder_model TEXT    NOT NULL,
    payload_json    TEXT    NOT NULL,
    -- Deduplication identity and window. See SPEC.md section 13.1.
    content_hash    TEXT    NOT NULL,
    -- Agent-declared grouping of exchanges belonging to one line of discussion. NULL is the
    -- common case: an exchange that stands alone. The agent supplies it because the agent is
    -- the only participant that knows whether this question continues the last one, and a
    -- broker inferring that from a timeout would be guessing exactly where it matters. It is
    -- a grouping key and nothing more — it confers no privileges and changes no routing.
    -- See SPEC.md sections 5.0 and 9.4.
    -- The direct reference matters for the one message type with no exchange. A Notify
    -- declares its own conversation (SPEC.md section 5.0), so the composite key below cannot
    -- vouch for it: that key is satisfied by a NULL in either column, and a Notify always has
    -- NULL exchange_id. Without this a notification could name a discussion that does not
    -- exist, and export would return messages for a conversation the registry never heard of.
    conversation_id TEXT    REFERENCES conversations(id),
    created_utc     TEXT    NOT NULL,
    -- Derived, never supplied. ux_message_dedup below is only as good as the value it
    -- constrains: a bucket that does not match created_utc silently disables deduplication
    -- for that row, in exactly the way a pair_key that did not match its participants would
    -- have disabled ux_open_pair. Computing it in the schema means the broker cannot write a
    -- wrong one, which is stronger than checking that it did not. SQLite rejects an INSERT
    -- that supplies a value for this column at all.
    --
    -- The 60 is the window from SPEC.md section 13.1 and is deliberately a schema fact:
    -- changing it changes the meaning of every bucket already stored, so it should cost a
    -- migration rather than a configuration edit.
    dedup_bucket    INTEGER GENERATED ALWAYS AS (unixepoch(created_utc) / 60) STORED,
    delivered_utc   TEXT,

    CHECK (from_agent <> to_agent),
    CHECK ((type = 'notify') = (exchange_id IS NULL)),

    -- SPEC.md rule 8.5: 2,000 characters per message, enforced rather than advised.
    -- Terseness is the context-poisoning defence, so the limit lives in the schema
    -- where no code path can route around it.
    CHECK (length(payload_json) <= 2000),

    -- The column is named _json and the readers parse it as such.
    CHECK (json_valid(payload_json)),

    -- SPEC.md section 11.3 makes responder identity a MUST, and not only for answers: an
    -- unattributed 'unknown' from a weak local model is indistinguishable from a considered
    -- 'unknown' from a frontier model, and the asker acts on both identically. The same
    -- calibration applies to a question, and the section 13 envelope renders the identity on
    -- every delivered message, so a nullable column would leave the envelope a hole the
    -- schema itself permitted.
    --
    -- Not every responder is a model: section 11.2 admits scripts and humans, which use the
    -- 'script:' and 'human:' forms, and a broker forced answer records 'parley:broker'. The
    -- column is not restricted to known schemes the way a credential reference is, because model
    -- identifiers cannot be enumerated in advance. Requiring it to be present is what makes
    -- an unattributed message impossible rather than discouraged.
    --
    -- The character restriction is a second trust-boundary control, not tidiness. This value
    -- is rendered into the section 13 envelope header, which is the broker-authored region
    -- outside the payload delimiters. The delimiter token defends the payload block; it does
    -- nothing for the header. An adapter reporting a responder containing newlines and
    -- plausible framing would inject text that reads as though the broker wrote it, which is
    -- the same forgery the fixed '---' separator allowed, reached through another field. The
    -- permitted set covers real model identifiers and the script:, human: and parley: forms
    -- while excluding everything needed to fabricate envelope structure. The length bound
    -- also stops an unbounded header, since only the payload is otherwise capped.
    CHECK (length(responder_model) BETWEEN 1 AND 64
           AND responder_model NOT GLOB '*[^a-zA-Z0-9._:/-]*'),

    -- SHA-256 as lowercase hex. Unlike dedup_bucket the schema cannot compute this, so the
    -- shape is constrained instead: a broker writing an empty or truncated hash would make
    -- unrelated messages collide, and over-deduplication silently discards real traffic while
    -- the index looks perfectly healthy.
    CHECK (length(content_hash) = 64
           AND content_hash NOT GLOB '*[^0-9a-f]*'),

    -- created_utc must be parseable, because dedup_bucket is derived from it. unixepoch()
    -- returns NULL for text it cannot read, and SQLite treats NULLs as distinct in a unique
    -- index — so a single malformed timestamp does not merely mis-bucket one row, it removes
    -- that row from ux_message_dedup altogether. Byte-identical messages would then both
    -- store, silently, with the index still reporting no conflict.
    CHECK (unixepoch(created_utc) IS NOT NULL),

    -- Agent supplied, and it names an export, so it is likely to reach a filename, a report
    -- heading or a console. The set is narrower than responder_model's and the difference is
    -- deliberate: no '/' and no ':', because both are path syntax, and no '..' anywhere,
    -- because a run of dots is path syntax even without a separator. An earlier version shared
    -- responder_model's set and its comment claimed to keep path separators out while
    -- permitting '/', so 'conversation_id' could be '../../etc/passwd' -- a guarantee that was
    -- written down, believed, and not enforced. Bounded so an identifier cannot become a
    -- payload in its own right.
    --
    -- responder_model keeps '/' because model identifiers genuinely carry it, as in
    -- 'some-org/some-model-9'. It is not rendered into a filename; this is.
    CHECK (conversation_id IS NULL
           OR (length(conversation_id) BETWEEN 1 AND 64
               AND conversation_id NOT GLOB '*[^a-zA-Z0-9._-]*'
                   AND conversation_id NOT GLOB '*..*')),

    -- SPEC.md rule 1: a Question states what the asker will do with each possible answer. The
    -- length floor does the real work; requiring only that the key be present admits "n/a",
    -- which satisfies a check and states nothing.
    CHECK (type <> 'question'
           OR length(coalesce(json_extract(payload_json, '$.consequences'), '')) >= 40),

    -- The exchange holds the authoritative conversation; this column repeats it, and the
    -- composite foreign key is what stops the copy becoming a second opinion. A message may
    -- not name a conversation its exchange is not in, nor one that does not exist.
    --
    -- Kept deliberately, against the usual rule that one fact belongs in one column, because
    -- the rule guards against silent drift and there is none once the copy cannot differ. What
    -- it buys is a message log that answers "what happened in this conversation" on its own,
    -- at the moment someone is looking because something has gone wrong.
    --
    -- A composite foreign key is satisfied by a NULL in either column, so a message may carry
    -- NULL where its exchange carries an identifier. That loses the shortcut without
    -- misreporting anything. The copy can fail to help; it cannot lie. It also makes an
    -- exchange's conversation immutable once it carries any message, which SPEC.md 5.0 claimed
    -- as a convention.
    FOREIGN KEY (exchange_id, conversation_id)
        REFERENCES exchanges(id, conversation_id)
) STRICT;

-- Deduplication backstop. SPEC.md section 13.1 requires byte-identical messages to be
-- refused within a short window. A moving window is not expressible as a unique index, so
-- the broker checks the exact window inside the write transaction and this index catches
-- what a broker that skipped the check would otherwise store. It is deliberately coarser
-- than the rule: two identical messages either side of a bucket boundary both survive it.
-- The exact rule needs the broker; this needs nothing, which is the point.
CREATE UNIQUE INDEX ux_message_dedup
    ON messages (content_hash, dedup_bucket);

-- Export and the retention idle rule both gather a conversation. Partial, because most rows
-- carry no identifier and indexing those would spend space recording an absence.
CREATE INDEX ix_message_conversation
    ON messages (conversation_id, id) WHERE conversation_id IS NOT NULL;

CREATE INDEX ix_inbox
    ON messages (to_agent, id) WHERE delivered_utc IS NULL;

CREATE INDEX ix_messages_exchange
    ON messages (exchange_id, id);

-- ---------------------------------------------------------------------------------------
-- Triggers. These come last because SQLite compiles a trigger against a table that must
-- already exist, so this file's order is the order a database is built in.
--
-- Every one of these enforces a rule that CHECK cannot: each needs to count or look up other
-- rows, and CHECK forbids subqueries. That makes them weaker than the constraints above --
-- procedural code that must itself be correct, constraining only the statements written
-- against -- which is why two of them come in INSERT and UPDATE pairs.
-- ---------------------------------------------------------------------------------------

CREATE TRIGGER tr_open_pair_cap BEFORE INSERT ON exchanges WHEN NEW.state = 'OPEN'
BEGIN
  SELECT RAISE(ABORT, 'open_pair_cap reached')
   WHERE (SELECT count(*) FROM exchanges
           WHERE state = 'OPEN' AND asker = NEW.asker AND answerer = NEW.answerer)
         >= NEW.open_pair_cap;
END;

CREATE TRIGGER tr_ghost_needs_no_answer BEFORE UPDATE ON exchanges
WHEN NEW.resolution = 'ghosted'
BEGIN
  SELECT RAISE(ABORT, 'ghosted requires that no answer was given')
   WHERE EXISTS (SELECT 1 FROM messages WHERE exchange_id = NEW.id AND type = 'answer');
END;

-- SPEC.md section 5.0: closure is terminal, so a vote against a concluded discussion is a
-- stored contradiction -- the row would read as a participant declining a closure that had
-- already happened. The broker refuses this in ConversationRepository.RecordVoteAsync; this
-- is the backstop for every other path. A CHECK cannot express it, since it must read
-- another table.
CREATE TRIGGER tr_no_vote_after_closure BEFORE UPDATE ON conversation_participants
WHEN NEW.vote IS NOT NULL
BEGIN
  SELECT RAISE(ABORT, 'conversation is closed')
   WHERE EXISTS (SELECT 1 FROM conversations
                  WHERE id = NEW.conversation_id AND state = 'CLOSED');
END;

-- SPEC.md section 5.0: a closed conversation stays closed. Closure is by consensus, so a
-- single agent naming a concluded identifier would reopen the discussion unilaterally,
-- defeating the poll, the decline veto and the silence rule together. The broker refuses this
-- with a stated reason (OpenRejection.ConversationClosed); this is the backstop that covers
-- every other path to an exchange row.
CREATE TRIGGER tr_closed_conversation_stays_closed BEFORE INSERT ON exchanges
WHEN NEW.conversation_id IS NOT NULL
BEGIN
  SELECT RAISE(ABORT, 'conversation is closed')
   WHERE EXISTS (SELECT 1 FROM conversations
                  WHERE id = NEW.conversation_id AND state = 'CLOSED');
END;

CREATE TRIGGER tr_open_pair_cap_resume BEFORE UPDATE ON exchanges
WHEN NEW.state = 'OPEN' AND OLD.state <> 'OPEN'
BEGIN
  SELECT RAISE(ABORT, 'open_pair_cap reached on resume')
   WHERE (SELECT count(*) FROM exchanges
           WHERE state = 'OPEN' AND asker = NEW.asker AND answerer = NEW.answerer
                 AND id <> NEW.id)
         >= NEW.open_pair_cap;
END;

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
