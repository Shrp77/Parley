# Data model: migration 006

```sql
-- A conversation's parent and how it came about. No key to conversations: it outlives either side (§14).
CREATE TABLE conversation_links (
  child      TEXT NOT NULL PRIMARY KEY,
  parent     TEXT NOT NULL,
  how        TEXT NOT NULL CHECK (how IN ('spinoff', 'move')),
  linked_utc TEXT NOT NULL CHECK (unixepoch(linked_utc) IS NOT NULL),
  CHECK (child <> parent)
) STRICT;
CREATE INDEX ix_conversation_links_parent ON conversation_links(parent);

-- One act of moving. Names and numbers, never text.
CREATE TABLE conversation_moves (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  source        TEXT    NOT NULL,
  destination   TEXT    NOT NULL,
  moved_by      TEXT    NOT NULL,
  moved_utc     TEXT    NOT NULL CHECK (unixepoch(moved_utc) IS NOT NULL),
  first_message INTEGER,               -- where a transcript draws the note; null when no message was held
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

-- What one agent is told about one open exchange that moved, at its next collection.
CREATE TABLE move_notices (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  agent         TEXT    NOT NULL REFERENCES agents(name) ON DELETE CASCADE,
  exchange_id   INTEGER NOT NULL,
  from_conversation TEXT NOT NULL,
  to_conversation   TEXT NOT NULL,
  created_utc   TEXT    NOT NULL CHECK (unixepoch(created_utc) IS NOT NULL),
  delivered_utc TEXT    CHECK (delivered_utc IS NULL OR unixepoch(delivered_utc) IS NOT NULL)
) STRICT;
CREATE INDEX ix_move_notices_collection ON move_notices(agent, delivered_utc);

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
```

Destroy leaves links and move records, and deletes notices for the exchanges it removes. Retention leaves links and records, and prunes delivered notices as `roadmap_notices` are.

Updated by a move: `exchanges.conversation_id`, `messages.conversation_id`, `conversation_cycles.conversation_id`, `message_lengths.(conversation_id, position)`, `conversation_participants` rows, and `conversations.state` when a poll is voided.

Never changed by a move: any message's text, author, recipient, `created_utc`, `delivered_utc`, `content_hash` or id; `shared_files`, `conversation_folders`, `file_fetches`, `file_notices`.

## Domain types

- `ConversationLinks(Parent?, How?, Children[])` on `ConversationExport`, and `Parent` on `ConversationSummary`.
- `MoveRecord(Id, Source, Destination, MovedBy, MovedUtc, FirstMessage?, Messages, Exchanges[])`, on `ConversationExport`.
- `MovePreview(Exchanges[], FilesStaying[], Refused?)`, each exchange: `(Id, Open, Requester, Receiver, Messages, FirstWords, AddedBecause?)`.
- `MoveOutcome(Refused?, Exchanges, Messages)`.
- `SettledElsewhere(Exchange, SettledBy, Conversation)`, on `ConversationExport`.
- `MoveNotice(Exchange, From, To)`, on the agent's inbox.
