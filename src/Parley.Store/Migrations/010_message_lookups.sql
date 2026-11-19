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
-- When an agent last spoke in a conversation (the flags read this for every open exchange, and the Inbox reads the flags): with
-- only (conversation_id, created_utc) it walked back through the whole conversation to find the agent's last message, 10 ms an
-- open exchange for one of 2,500 messages the agent had not written in.
CREATE INDEX IF NOT EXISTS ix_messages_conversation_from_time ON messages(conversation_id, from_agent, created_utc);

-- The newest turn of an agent (the flags read three of these for every open exchange, and the Inbox reads the flags on every
-- render): ORDER BY id with only (agent, started_utc) to use sorted every turn the agent ever took, 20 s for 2,000 open
-- exchanges. And the exchanges an agent owes (the wake after a start asked once per agent with a scan of the table each).
CREATE INDEX IF NOT EXISTS ix_invocations_agent_id ON invocations(agent, id);
CREATE INDEX IF NOT EXISTS ix_exchanges_owed_by ON exchanges(owed_by);
-- IF NOT EXISTS so a store taken back a version by hand, as the migration tests do, upgrades again.
