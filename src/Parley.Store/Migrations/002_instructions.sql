-- SPEC.md §10.2's `instruct`, and the only text in this store a person wrote.
--
-- Not a message, and it could not have been one. messages.from_agent is a foreign key to
-- agents(name) and an operator is not an agent -- which is that key doing its job rather than an
-- obstacle to route around. Relaxing it to admit one sender that is not registered would admit
-- every other unregistered sender in the same edit.
--
-- Cascades on the agent like size_refusals does: §10.2's purge removes an agent and everything
-- that referred to it, and an instruction to an agent that no longer exists refers to nothing.
CREATE TABLE instructions (
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
  CHECK (length(text) <= 4000),

  -- Settled and unsettled are one fact, so they move together or the row is lying about one of
  -- them. A reply belongs to a settled instruction: an agent that has not been given the turn
  -- yet cannot have said anything, and a row claiming otherwise would be read as an answer.
  CHECK ((outcome = 'pending') = (settled_utc IS NULL)),
  CHECK (outcome <> 'pending' OR reply IS NULL)
) STRICT;

CREATE INDEX ix_instructions_agent ON instructions(agent, issued_utc);
