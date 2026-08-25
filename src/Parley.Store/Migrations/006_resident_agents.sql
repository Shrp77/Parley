-- Agents that stay up, rather than being started once per message.
--
-- Migration 005 built the one-shot form: a process per waiting message, which answered and
-- exited. It worked, and it was the wrong shape. An agent started per message cannot hold a
-- position across turns -- it cannot remember why it asked a clarification, cannot get better at
-- its own domain by answering thirty questions about it, and cannot negotiate a contested
-- boundary, which is the case the protocol exists for. SPEC.md §11.1 described that one-shot form
-- as the Headless tier; the section was wrong and has been rewritten with this change.
--
-- What replaces it is a resident agent: started once, given turns over its standard input, and
-- kept alive until it is told to quit.

-- How to start this agent. Previously the command lived only in the launch configuration of the
-- server that would run it, because that server did its own invoking. A fleet supervises agents
-- other than its own, so the command has to be somewhere the supervisor can read.
--
-- It is written by that agent's own server at registration and by nothing else, which is what
-- makes storing an executable command line here no wider a trust than the launch environment it
-- came from: §13 takes an agent's identity from its own environment, the registration keys on
-- that identity, and so no agent can write the command line another agent will be started with.
-- The supervisor never composes this string -- it splits it and executes it as argv, never
-- through a shell.
ALTER TABLE agents ADD COLUMN invoke_command TEXT
    CHECK (invoke_command IS NULL OR length(invoke_command) BETWEEN 1 AND 1000);

-- What a turn cost. The one-shot form recorded an exit code and a duration because that was all
-- a process could tell you. A resident agent reports its own accounting per turn, and recording
-- it is the same bargain §17 question 1 struck for notifications: leave it unbounded, but
-- measure, so the decision to bound it can cite evidence rather than taste.
--
-- Defaults rather than NOT NULL alone, so rows written by the one-shot form stay valid and
-- readable. They will show zero cost, which is true of what was recorded rather than of what was
-- spent, and the started_utc on those rows is how to tell the two eras apart.
ALTER TABLE invocations ADD COLUMN cost_usd REAL NOT NULL DEFAULT 0.0
    CHECK (cost_usd >= 0.0);

-- How many model turns went into one Parley turn. A single "deal with your inbox" can become a
-- long agentic loop, and the count is what separates an agent that answered a question from one
-- that went and did an afternoon's work on the strength of it.
ALTER TABLE invocations ADD COLUMN model_turns INTEGER NOT NULL DEFAULT 0
    CHECK (model_turns >= 0);

-- Tool calls refused for want of permission. A resident agent runs with nobody at the keyboard,
-- so a call needing approval is not paused for a human -- it is refused, and the agent answers
-- anyway and worse. Nothing else in the record distinguishes that from an agent that has simply
-- become unhelpful, which is why it is counted rather than inferred.
ALTER TABLE invocations ADD COLUMN permission_denials INTEGER NOT NULL DEFAULT 0
    CHECK (permission_denials >= 0);

-- Consent and command must arrive together or not at all.
--
-- A row consenting to be started with nothing to start is a supervisor that finds a candidate,
-- tries, and fails on every pass -- silently, since a missing command is not an error anywhere
-- else. Enforced in storage rather than in the server for the reason §9.3 gives: the server is
-- one writer among the ones a future adapter might add, and an invariant it holds is an
-- invariant only while it is the only writer.
CREATE TRIGGER tr_agents_consent_needs_command_insert
BEFORE INSERT ON agents
WHEN NEW.invoke_when_idle = 1 AND NEW.invoke_command IS NULL
BEGIN
    SELECT RAISE(ABORT, 'an agent that consents to invocation must record how to start it');
END;

CREATE TRIGGER tr_agents_consent_needs_command_update
BEFORE UPDATE ON agents
WHEN NEW.invoke_when_idle = 1 AND NEW.invoke_command IS NULL
BEGIN
    SELECT RAISE(ABORT, 'an agent that consents to invocation must record how to start it');
END;
