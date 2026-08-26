-- What starts an idle agent widened from "owes an answer" to "has anything waiting", so the
-- two columns recording it are renamed to match. SPEC.md §11.1.
--
-- The old name was accurate when an OPEN exchange was the only thing that could start a
-- process. It is not any more: an answer, a ghost and a notify all leave a message nobody has
-- collected and no debt at all, so an agent could be started with owed_before = 0 and a full
-- inbox. A column called owed_before holding that number is not merely vague, it reads as
-- "this turn was started for nothing", which is the exact opposite of what happened -- and
-- §10.2's invocations report is built on distinguishing those two cases.
--
-- ALTER TABLE ... RENAME COLUMN preserves the rows, which matters: this table outlives the
-- agents in it on purpose, and a deployment that has been running the fleet has real
-- measurements here. Rebuilding the table to change a name would have thrown them away for
-- tidiness.
--
-- The CHECK constraints follow the rename automatically -- SQLite rewrites references to a
-- renamed column inside constraint expressions -- so nothing below re-states them.

ALTER TABLE invocations RENAME COLUMN owed_before TO waiting_before;
ALTER TABLE invocations RENAME COLUMN owed_after  TO waiting_after;
