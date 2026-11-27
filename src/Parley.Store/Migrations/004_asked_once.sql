-- Migration 004. Upgrades a schema 3 store in place and keeps every row (SPEC.md 9.2).
--
-- When Parley first asked an agent what it is responsible for (SPEC.md 10.2), and when its node was first asked to
-- refresh it after a line accepted automatically (SPEC.md 11.1), kept on the agent's own row. Each happens once for
-- an agent, and the question's row cannot say so for ever: destroying its conversation takes the row with it. The
-- agent's row is what a destroy keeps and a purge removes, so a repository that attaches again under a purged name
-- is a new agent, asked and refreshed afresh.

ALTER TABLE agents ADD COLUMN asked_utc TEXT CHECK (asked_utc IS NULL OR unixepoch(asked_utc) IS NOT NULL);

ALTER TABLE agents ADD COLUMN refreshed_utc TEXT CHECK (refreshed_utc IS NULL OR unixepoch(refreshed_utc) IS NOT NULL);

UPDATE agents
   SET asked_utc = (SELECT min(q.asked_utc) FROM ownership_questions q WHERE q.agent = agents.name)
 WHERE EXISTS (SELECT 1 FROM ownership_questions q WHERE q.agent = agents.name);

-- A store whose ownership conversation was destroyed before this migration kept only the tombstone. An agent named
-- in one whose identifier is its own role's question (owns-<role>-<stamp>, or with the -<n> a collision adds), and
-- that Parley took part in, is taken to have been asked then. An agent may name a conversation owns-anything itself,
-- but cannot draw Parley into it.
UPDATE agents
   SET asked_utc = (SELECT min(d.destroyed_utc) FROM destroyed_conversations d
                     WHERE (d.id GLOB 'owns-' || agents.role || '-[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]-[0-9][0-9][0-9][0-9][0-9][0-9]'
                            OR d.id GLOB 'owns-' || agents.role || '-[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]-[0-9][0-9][0-9][0-9][0-9][0-9]-[0-9]*')
                       AND instr(', ' || d.participants || ',', ', ' || agents.name || ',') > 0
                       AND instr(', ' || d.participants || ',', ', parley-facilitator@') > 0)
 WHERE asked_utc IS NULL
   AND EXISTS (SELECT 1 FROM destroyed_conversations d
                WHERE (d.id GLOB 'owns-' || agents.role || '-[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]-[0-9][0-9][0-9][0-9][0-9][0-9]'
                       OR d.id GLOB 'owns-' || agents.role || '-[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]-[0-9][0-9][0-9][0-9][0-9][0-9]-[0-9]*')
                  AND instr(', ' || d.participants || ',', ', ' || agents.name || ',') > 0
                  AND instr(', ' || d.participants || ',', ', parley-facilitator@') > 0);

-- A refresh already sent. One whose question's conversation was destroyed before this migration left no trace, and
-- that agent may be refreshed once more after a later line is accepted automatically.
UPDATE agents
   SET refreshed_utc = (SELECT q.settled_utc FROM ownership_questions q WHERE q.agent = agents.name AND q.restart_sent = 1)
 WHERE EXISTS (SELECT 1 FROM ownership_questions q WHERE q.agent = agents.name AND q.restart_sent = 1);

-- Marked by storage, in the write that asks, so no route that asks can forget to.
CREATE TRIGGER tr_asking_marks_the_agent
AFTER INSERT ON ownership_questions
BEGIN
  UPDATE agents SET asked_utc = NEW.asked_utc WHERE name = NEW.agent AND asked_utc IS NULL;
END;

-- Marked in the write that records the refresh as sent, for the same reason. A sent refresh is on an accepted
-- question, so it has a settled time.
CREATE TRIGGER tr_refreshing_marks_the_agent
AFTER UPDATE OF restart_sent ON ownership_questions
WHEN NEW.restart_sent = 1
BEGIN
  UPDATE agents SET refreshed_utc = NEW.settled_utc WHERE name = NEW.agent AND refreshed_utc IS NULL;
END;
