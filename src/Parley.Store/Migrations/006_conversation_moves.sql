-- Schema 6: a conversation may have a parent, and the person may move whole exchanges between conversations
-- (SPEC.md 5.0). Only adds tables, indexes and one trigger, so every row a store holds is untouched.

-- A conversation's parent and how it came about. No key to conversations: it outlives either side being destroyed,
-- and holds only names.
CREATE TABLE conversation_links (
  child      TEXT NOT NULL PRIMARY KEY,
  parent     TEXT NOT NULL,
  how        TEXT NOT NULL CHECK (how IN ('spinoff', 'move')),
  linked_utc TEXT NOT NULL CHECK (unixepoch(linked_utc) IS NOT NULL),
  CHECK (child <> parent)
) STRICT;

CREATE INDEX ix_conversation_links_parent ON conversation_links(parent);

-- One act of moving. Names and numbers, never text, and kept as the link is. first_message is where a transcript draws
-- the note; null when none of what moved still had a message.
CREATE TABLE conversation_moves (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  source        TEXT    NOT NULL,
  destination   TEXT    NOT NULL,
  moved_by      TEXT    NOT NULL,
  moved_utc     TEXT    NOT NULL CHECK (unixepoch(moved_utc) IS NOT NULL),
  first_message INTEGER,
  messages      INTEGER NOT NULL CHECK (messages >= 0),
  CHECK (source <> destination)
) STRICT;

CREATE INDEX ix_conversation_moves_source ON conversation_moves(source);
CREATE INDEX ix_conversation_moves_destination ON conversation_moves(destination);

-- Which exchanges went. No key to exchanges: a purge may remove one and the record stays.
CREATE TABLE conversation_move_exchanges (
  move_id     INTEGER NOT NULL REFERENCES conversation_moves(id) ON DELETE CASCADE,
  exchange_id INTEGER NOT NULL,
  PRIMARY KEY (move_id, exchange_id)
) STRICT;

-- What one agent is told about one open exchange that moved. Not a message: it opens nothing, is owed nothing and wakes
-- nobody. AUTOINCREMENT so an acknowledgement that arrives late can never mark another agent's notice.
CREATE TABLE move_notices (
  id                INTEGER PRIMARY KEY AUTOINCREMENT,
  agent             TEXT    NOT NULL REFERENCES agents(name) ON DELETE CASCADE,
  exchange_id       INTEGER NOT NULL,
  from_conversation TEXT    NOT NULL,
  to_conversation   TEXT    NOT NULL,
  created_utc       TEXT    NOT NULL CHECK (unixepoch(created_utc) IS NOT NULL),
  delivered_utc     TEXT    CHECK (delivered_utc IS NULL OR unixepoch(delivered_utc) IS NOT NULL)
) STRICT;

CREATE INDEX ix_move_notices_collection ON move_notices(agent, delivered_utc);

-- Held here so a code path cannot route around it. The broker refuses these first, with a sentence.
CREATE TRIGGER tr_a_moved_exchange_may_go_there
BEFORE UPDATE OF conversation_id ON exchanges
WHEN NEW.conversation_id IS NOT OLD.conversation_id
BEGIN
  SELECT RAISE(ABORT, 'a closed conversation stays closed')
   WHERE (SELECT state FROM conversations WHERE id = NEW.conversation_id) = 'CLOSED';
  SELECT RAISE(ABORT, 'a roadmap item''s conversation takes part in no move')
   WHERE EXISTS (SELECT 1 FROM roadmap_item_conversations
                  WHERE conversation_id IN (OLD.conversation_id, NEW.conversation_id));
  SELECT RAISE(ABORT, 'an ownership conversation takes part in no move')
   WHERE OLD.conversation_id GLOB 'owns-*' OR NEW.conversation_id GLOB 'owns-*'
      OR EXISTS (SELECT 1 FROM ownership_questions WHERE exchange_id = OLD.id);
END;
