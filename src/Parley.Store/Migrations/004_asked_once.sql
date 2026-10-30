-- Migration 004. Upgrades a schema 3 store in place and keeps every row (SPEC.md 9.2).
--
-- When Parley first asked an agent what it is responsible for, kept on the agent's own row (SPEC.md 10.2). Having
-- been asked at all stops Parley asking again by itself, and the question's row cannot say so for ever: destroying
-- its conversation takes the row with it. The agent's row is what a destroy keeps and a purge removes, so a
-- repository that attaches again under a purged name is a new agent and is asked.

ALTER TABLE agents ADD COLUMN asked_utc TEXT CHECK (asked_utc IS NULL OR unixepoch(asked_utc) IS NOT NULL);

UPDATE agents
   SET asked_utc = (SELECT min(q.asked_utc) FROM ownership_questions q WHERE q.agent = agents.name)
 WHERE EXISTS (SELECT 1 FROM ownership_questions q WHERE q.agent = agents.name);

-- A store whose ownership conversation was destroyed before this migration kept only the tombstone. An agent named
-- in one whose identifier is its own role's question (owns-<role>-<stamp>), and that Parley took part in, is taken to
-- have been asked then. An agent may name a conversation owns-anything itself, but cannot draw Parley into it.
UPDATE agents
   SET asked_utc = (SELECT min(d.destroyed_utc) FROM destroyed_conversations d
                     WHERE d.id GLOB 'owns-' || agents.role || '-[0-9]*'
                       AND instr(', ' || d.participants || ',', ', ' || agents.name || ',') > 0
                       AND instr(', ' || d.participants || ',', ', parley-facilitator@') > 0)
 WHERE asked_utc IS NULL
   AND EXISTS (SELECT 1 FROM destroyed_conversations d
                WHERE d.id GLOB 'owns-' || agents.role || '-[0-9]*'
                  AND instr(', ' || d.participants || ',', ', ' || agents.name || ',') > 0
                  AND instr(', ' || d.participants || ',', ', parley-facilitator@') > 0);

-- Marked by storage, in the write that asks, so no route that asks can forget to.
CREATE TRIGGER tr_asking_marks_the_agent
AFTER INSERT ON ownership_questions
BEGIN
  UPDATE agents SET asked_utc = NEW.asked_utc WHERE name = NEW.agent AND asked_utc IS NULL;
END;
