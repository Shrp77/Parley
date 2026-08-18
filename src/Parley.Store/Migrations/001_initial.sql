-- Parley schema, migration 001.
-- Applied inside a transaction by SchemaMigrator; do not add BEGIN/COMMIT here.

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
    status          TEXT    NOT NULL CHECK (status IN ('registered', 'live', 'offline')),

    -- SPEC.md section 12 claims the registry holds pointers and never secret values, and
    -- that this is what makes the manifest safe to commit. A claim the storage layer does
    -- not enforce is a convention, and conventions lose to one tired paste of an API key,
    -- which then lands in backups and in any diagnostic that dumps the registry. Restricting
    -- the column to known reference schemes makes that accident impossible rather than
    -- merely discouraged.
    CHECK (credential_ref IS NULL
           OR credential_ref LIKE 'env:_%'
           OR credential_ref LIKE 'keychain:_%'
           OR credential_ref LIKE 'file:_%')
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
    responder_model TEXT,
    payload_json    TEXT    NOT NULL,
    created_utc     TEXT    NOT NULL,
    delivered_utc   TEXT,

    CHECK (from_agent <> to_agent),
    CHECK ((type = 'notify') = (exchange_id IS NULL)),

    -- SPEC.md rule 8.5: 2,000 characters per message, enforced rather than advised.
    -- Terseness is the context-poisoning defence, so the limit lives in the schema
    -- where no code path can route around it.
    CHECK (length(payload_json) <= 2000),

    -- The column is named _json and the readers parse it as such.
    CHECK (json_valid(payload_json)),

    -- SPEC.md section 11.3 makes responder identity a MUST for answers specifically: an
    -- unattributed 'unknown' from a weak local model is indistinguishable from a considered
    -- 'unknown' from a frontier model, and the asker acts on both identically. A broker
    -- forced answer (clarify cap breach) records 'parley:broker'.
    CHECK (type <> 'answer' OR responder_model IS NOT NULL)
) STRICT;

CREATE INDEX ix_inbox
    ON messages (to_agent, id) WHERE delivered_utc IS NULL;

CREATE INDEX ix_messages_exchange
    ON messages (exchange_id, id);
