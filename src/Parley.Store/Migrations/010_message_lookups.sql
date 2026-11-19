-- Migration 010. Upgrades a schema 9 store in place and keeps every row (SPEC.md 9.2).
--
-- Indexes for how the messages table is read. Until now it had its primary key and the dedup index only, so reading one
-- exchange's messages, one conversation's, or one agent's walked every message in the project: about 120 ms for an exchange
-- and 400 ms for a conversation's wrote-elsewhere notes at 100,000 messages, growing with the store. Nothing here changes a row.
CREATE INDEX IF NOT EXISTS ix_messages_exchange     ON messages(exchange_id, id);
CREATE INDEX IF NOT EXISTS ix_messages_conversation ON messages(conversation_id, id);
CREATE INDEX IF NOT EXISTS ix_messages_conversation_time ON messages(conversation_id, created_utc);
CREATE INDEX IF NOT EXISTS ix_messages_from         ON messages(from_agent, id);
CREATE INDEX IF NOT EXISTS ix_messages_to           ON messages(to_agent, id);
-- IF NOT EXISTS so a store taken back a version by hand, as the migration tests do, upgrades again.
