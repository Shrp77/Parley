-- Migration 011. Upgrades a schema 10 store in place and keeps every row (SPEC.md 9.2).
--
-- A person's freeze on an agent (SPEC.md 11.1). A column on the agent's row rather than a table referencing it, so a purge takes
-- it with the row and nothing else has to be taught about it. NULL means not frozen. Only a person writes it.
ALTER TABLE agents ADD COLUMN frozen_utc TEXT
    CHECK (frozen_utc IS NULL OR unixepoch(frozen_utc) IS NOT NULL);
-- Which run of the broker the person froze it under: a number the broker gives itself every time it starts. A later start, which
-- is what a logon or a reboot brings, finds a different one and lets the freeze lapse (SPEC.md 11.1).
ALTER TABLE agents ADD COLUMN frozen_run TEXT
    CHECK (frozen_run IS NULL OR (frozen_utc IS NOT NULL AND length(frozen_run) = 36));
-- The person's choice that this freeze outlasts a broker's start. Only meaningful while frozen, and cleared with the freeze.
ALTER TABLE agents ADD COLUMN keep_frozen INTEGER NOT NULL DEFAULT 0
    CHECK (keep_frozen IN (0, 1) AND (keep_frozen = 0 OR frozen_utc IS NOT NULL));
