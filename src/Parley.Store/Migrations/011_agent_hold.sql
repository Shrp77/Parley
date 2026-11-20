-- Migration 011. Upgrades a schema 10 store in place and keeps every row (SPEC.md 9.2).
--
-- A person's hold on an agent (SPEC.md 11.1). A column on the agent's row rather than a table referencing it, so a purge takes
-- it with the row and nothing else has to be taught about it. NULL means not held. Only a person writes it.
ALTER TABLE agents ADD COLUMN held_utc TEXT
    CHECK (held_utc IS NULL OR unixepoch(held_utc) IS NOT NULL);
