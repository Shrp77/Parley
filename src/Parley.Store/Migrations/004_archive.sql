-- 004_archive.sql
--
-- SPEC.md §10.2's archive, and the record §14 leaves behind when a conversation is destroyed.
--
-- Both fragments are copies of what SPEC.md §9 now carries, and tests/Parley.Spec.Tests executes
-- that document's fragments against SQLite in the order written. The two must agree; the document
-- is the authority and this is the implementation of it.
--
-- **No table is rebuilt here, and that is deliberate.** 003_message_cap.sql had to rebuild
-- `messages` because a CHECK cannot be added to an existing SQLite table, and its own header
-- records the ordering hazards that came with it -- triggers dropped and recreated, foreign keys
-- pointed at a table being replaced underneath them. Nothing in this migration needs that: a
-- nullable column is an ALTER, and a new table is a CREATE.
--
-- The destroy invariant of §14 needs no schema change at all. Every column that references
-- conversations(id) already does so without ON DELETE CASCADE, which is exactly the property the
-- deletion relies on: the deletes are ordered inside one transaction with foreign keys deferred
-- to commit, so a table nobody remembered fails the whole destroy rather than leaving a message
-- pointing at a conversation that is gone. Adding a cascade here would remove that guard while
-- looking like it was strengthening it.

-- Null means the conversation is in the ordinary list. No default and no backfill: every existing
-- row is unarchived, which is what an operator who has never archived anything expects to see.
ALTER TABLE conversations ADD COLUMN archived_utc TEXT;

CREATE TABLE destroyed_conversations (
  id            TEXT PRIMARY KEY,
  destroyed_utc TEXT NOT NULL,
  messages      INTEGER NOT NULL CHECK (messages >= 0),
  participants  TEXT NOT NULL
) STRICT;
