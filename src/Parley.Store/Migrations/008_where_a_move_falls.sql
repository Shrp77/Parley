-- Migration 008. Upgrades a schema 7 store in place and keeps every row (SPEC.md 9.2).
--
-- Where a move falls among the messages (SPEC.md 5.0). A move rewrites the conversation of every message on the exchanges
-- it moves, so telling where a message was written needs to know whether it came before or after each move of its
-- exchange. Timestamps cannot say: a move and a message the broker handles back to back can share a millisecond. Message
-- ids are given out in the order the broker handles them, so a move now records the newest message id when it was made,
-- and a message was written before a move exactly when its id is at most that.
--
-- A move recorded before this keeps the best its row allows: the newest message at or before its time.

ALTER TABLE conversation_moves ADD COLUMN last_message INTEGER;

UPDATE conversation_moves
   SET last_message = (SELECT MAX(id) FROM messages
                        WHERE unixepoch(created_utc, 'subsec') <= unixepoch(conversation_moves.moved_utc, 'subsec'));

-- Read once per message when an export works out where each was written; the key leads with the move.
CREATE INDEX ix_conversation_move_exchanges_exchange ON conversation_move_exchanges(exchange_id);
