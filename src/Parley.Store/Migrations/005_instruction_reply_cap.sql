-- 005_instruction_reply_cap.sql
--
-- An agent's reply to an operator's instruction is bounded, as the instruction already was.
--
-- FOUND BY THE REVIEW GATE, LENS 2. `instructions.text` has carried rule 8.5's cap since 003 and
-- `instructions.reply` never carried anything. Nothing on the reporting path bounded it either, so
-- a node -- or anything able to reach the broker and claim to be one, which SPEC.md §16 says is
-- anything on the network -- could write an arbitrarily large blob into the operator's own record.
-- A 200,000-character reply was stored and read back whole by the probe in
-- `InstructionReportGuardTests`.
--
-- IT WAS ALWAYS POSSIBLE AND IT WAS NOT ALWAYS REACHABLE. Before §10.2 gained `instructions()`, a
-- reply came back only as the return value of the call that issued the instruction and was
-- otherwise write-only. The readback puts it in a listing that both operator surfaces render, so
-- what used to be an oversized row nobody read is now an oversized row on a page.
--
-- WHY A CHECK AND NOT A TRUNCATION AT THE EDGE ALONE. §9.3 puts invariants in storage because
-- application code can route around a check it makes itself. The edge does truncate -- refusing
-- would lose the answer entirely, which is the failure this whole feature exists to remove -- but
-- the edge is one caller, and this table has already acquired a second writer once.
--
-- WHY THE SAME NUMBER, AGAIN. 003 argued it for `text` and the argument is unchanged: what the cap
-- defends is how much of somebody's context one piece of text can fill, and a reply fills the same
-- context an instruction does. Two numbers would also mean two arguments the next time evidence
-- moves one. `SpecConsistencyTests` holds them together.
--
-- WHY THIS REBUILD IS SIMPLER THAN 003's. That one rebuilt `messages`, and its header records what
-- came with it: a trigger on another table that reads `messages` and is re-parsed during the
-- rename, AFTER INSERT triggers that would have double-counted every referral on the copy, and a
-- generated column that must be left out of both column lists. `instructions` has none of those.
-- No trigger names it, nothing references it with a foreign key, and every column is stored. So it
-- is a create, a copy, a drop, a rename and an index -- and the ordering hazards are absent rather
-- than handled.
--
-- EXISTING ROWS. A reply already over the cap would fail the CHECK on copy and take the whole
-- migration with it, which is the correct failure for a constraint being introduced -- but it
-- would strand a deployment on a row nobody can see. The copy therefore truncates what it carries,
-- with the same marker the edge uses, so an operator meets a shortened answer rather than a broker
-- that will not start.

CREATE TABLE instructions_new (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  agent        TEXT    NOT NULL REFERENCES agents(name) ON DELETE CASCADE,
  text         TEXT    NOT NULL,
  outcome      TEXT    NOT NULL
               CHECK (outcome IN ('pending', 'taken', 'declined', 'undeliverable', 'failed')),
  reply        TEXT,
  issued_utc   TEXT    NOT NULL,
  settled_utc  TEXT,

  -- The same bound as a message (rule 8.5), and deliberately the same number rather than a
  -- second one. SpecConsistencyTests holds the two together, because SQLite cannot say "the same
  -- as that one" and they would otherwise drift at the worst moment -- when evidence moves the
  -- message cap and this is left behind at a number nobody argued for.
  CHECK (length(text) <= 8000),

  -- And the agent's half of the same exchange, which had no bound at all. An operator's words are
  -- typed by a person and an agent's are produced by a model; if either needed the cap less, it
  -- was the one that already had it.
  CHECK (reply IS NULL OR length(reply) <= 8000),

  -- Settled and unsettled are one fact, so they move together or the row is lying about one of
  -- them. A reply belongs to a settled instruction: an agent that has not been given the turn
  -- yet cannot have said anything, and a row claiming otherwise would be read as an answer.
  CHECK ((outcome = 'pending') = (settled_utc IS NULL)),
  CHECK (outcome <> 'pending' OR reply IS NULL)
) STRICT;

INSERT INTO instructions_new
  (id, agent, text, outcome, reply, issued_utc, settled_utc)
SELECT
   id,
   agent,
   text,
   outcome,
   CASE
     WHEN reply IS NULL THEN NULL
     WHEN length(reply) <= 8000 THEN reply
     ELSE substr(reply, 1, 7968) || ' [... the rest was over the cap]'
   END,
   issued_utc,
   settled_utc
  FROM instructions
 ORDER BY id;

DROP TABLE instructions;
ALTER TABLE instructions_new RENAME TO instructions;

CREATE INDEX ix_instructions_agent ON instructions(agent, issued_utc);
