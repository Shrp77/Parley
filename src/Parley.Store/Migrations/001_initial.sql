-- Parley schema, migration 001.
-- Applied inside a transaction by SchemaMigrator; do not add BEGIN/COMMIT here.

CREATE TABLE agents (
    name            TEXT    PRIMARY KEY,
    adapter         TEXT    NOT NULL,
    tier            TEXT    NOT NULL CHECK (tier IN ('full', 'pull', 'headless')),
    workspace       TEXT,
    session_ref     TEXT,
    -- A pointer such as 'env:VENDOR_API_KEY' or 'keychain:parley/billing'.
    -- Never a secret value. See SPEC.md section 12.
    credential_ref  TEXT,
    last_seen_utc   TEXT,
    status          TEXT    NOT NULL CHECK (status IN ('registered', 'live', 'offline'))
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
    ttl_seconds       INTEGER NOT NULL,
    opened_utc        TEXT    NOT NULL,
    expires_utc       TEXT    NOT NULL,
    closed_utc        TEXT,
    is_decision       INTEGER NOT NULL DEFAULT 0 CHECK (is_decision IN (0, 1)),
    decision_ref      TEXT,

    CHECK (asker <> answerer),
    CHECK (owed_by IN (asker, answerer)),
    CHECK (clarify_depth >= 0 AND clarify_depth <= clarify_depth_cap),
    CHECK ((state = 'CLOSED') = (closed_utc IS NOT NULL))
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
    -- Which model actually produced this. A weak responder's 'unknown' must not read
    -- as a strong signal. See SPEC.md section 11.3.
    responder_model TEXT,
    payload_json    TEXT    NOT NULL,
    created_utc     TEXT    NOT NULL,
    delivered_utc   TEXT,

    CHECK (from_agent <> to_agent),
    CHECK ((type = 'notify') = (exchange_id IS NULL))
) STRICT;

CREATE INDEX ix_inbox
    ON messages (to_agent, id) WHERE delivered_utc IS NULL;

CREATE INDEX ix_messages_exchange
    ON messages (exchange_id, id);
