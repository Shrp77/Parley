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
    -- A pointer such as 'env:VENDOR_API_KEY' or 'keychain:parley/billing', resolved at
    -- invocation time and never persisted after resolution. See SPEC.md section 12.
    credential_ref  TEXT,
    last_seen_utc   TEXT,
    status          TEXT    NOT NULL
                    -- 'unreachable' is not a worse 'offline'. Offline is the ordinary resting
                    -- state of a lazily spawned agent and must stay routable, or the traffic
                    -- meant to wake it would be refused. Unreachable means spawning was tried
                    -- and failed repeatedly, so routing here only burns a TTL. See SPEC.md 14.
                    CHECK (status IN ('registered', 'live', 'offline', 'unreachable')),

    -- SPEC.md section 12 claims the registry holds pointers and never secret values, and
    -- that this is what makes the manifest safe to commit. A claim the storage layer does
    -- not enforce is a convention, and conventions lose to one tired paste of an API key,
    -- which then lands in backups and in any diagnostic that dumps the registry. Restricting
    -- the column to known reference schemes makes that accident impossible rather than
    -- merely discouraged.
    CHECK (credential_ref IS NULL
           OR credential_ref LIKE 'env:_%'
           OR credential_ref LIKE 'keychain:_%'
           OR credential_ref LIKE 'file:_%'),

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

CREATE TABLE exchanges (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    pair_key          TEXT    NOT NULL,
    asker             TEXT    NOT NULL REFERENCES agents(name),
    answerer          TEXT    NOT NULL REFERENCES agents(name),
    owed_by           TEXT    NOT NULL REFERENCES agents(name),
    state             TEXT    NOT NULL CHECK (state IN ('OPEN', 'CLOSED', 'ORPHANED')),
    clarify_depth     INTEGER NOT NULL DEFAULT 0,
    clarify_depth_cap INTEGER NOT NULL DEFAULT 3,
    -- Milliseconds, not seconds. Storing seconds truncated sub-second TTLs while
    -- expires_utc kept full precision, so a rehydrated exchange could not reproduce
    -- its own deadline.
    ttl_ms            INTEGER NOT NULL,
    opened_utc        TEXT    NOT NULL,
    expires_utc       TEXT    NOT NULL,
    closed_utc        TEXT,
    is_decision       INTEGER NOT NULL DEFAULT 0 CHECK (is_decision IN (0, 1)),
    decision_ref      TEXT,
    -- Optimistic concurrency stamp. Incremented on every write; a save that does not
    -- match the loaded value is rejected rather than silently overwriting. Without
    -- this, two paths that both load, transition, and save lose one transition, and
    -- the one most likely to be lost is a clarify_depth increment, which turns the
    -- depth cap into a suggestion.
    version           INTEGER NOT NULL DEFAULT 0,

    CHECK (asker <> answerer),
    CHECK (owed_by IN (asker, answerer)),
    CHECK (clarify_depth >= 0 AND clarify_depth <= clarify_depth_cap),
    CHECK (ttl_ms > 0),
    CHECK ((state = 'CLOSED') = (closed_utc IS NOT NULL)),

    -- pair_key must genuinely be derived from the two participants. ux_open_pair below is
    -- the strongest invariant in this schema, and it is only as good as the value it
    -- constrains: a mismatched key would have uniqueness enforced against the wrong string,
    -- permitting two simultaneously open exchanges for the same real pair. char(31) is the
    -- unit separator, and TEXT columns collate BINARY by default, which is the ordinal
    -- comparison PairKey.For uses.
    CHECK (pair_key = min(asker, answerer) || char(31) || max(asker, answerer))
) STRICT;

-- The core invariant: at most one open exchange per unordered pair.
-- Enforced in storage rather than application code so correctness survives broker bugs.
CREATE UNIQUE INDEX ux_open_pair
    ON exchanges (pair_key) WHERE state = 'OPEN';

-- Reaper scan: find open exchanges past their TTL.
CREATE INDEX ix_open_expiry
    ON exchanges (expires_utc) WHERE state = 'OPEN';

-- Orphan replay on agent re-registration.
CREATE INDEX ix_orphaned_owed
    ON exchanges (owed_by) WHERE state = 'ORPHANED';

CREATE TABLE messages (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    -- NULL for Notify, which opens no exchange and takes no pair slot.
    exchange_id     INTEGER REFERENCES exchanges(id),
    type            TEXT    NOT NULL CHECK (type IN ('question', 'clarify', 'answer', 'notify')),
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
    conversation_id TEXT,
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
    -- column is not restricted to known schemes the way credential_ref is, because model
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

    -- Same character set as responder_model, and for a related reason. This value is agent
    -- supplied and names an export, so it is likely to reach a filename, a report heading or a
    -- console; the restriction keeps newlines and path separators out of somewhere they would
    -- do damage. Bounded so an identifier cannot become a payload in its own right.
    CHECK (conversation_id IS NULL
           OR (length(conversation_id) BETWEEN 1 AND 64
               AND conversation_id NOT GLOB '*[^a-zA-Z0-9._:/-]*'))
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
