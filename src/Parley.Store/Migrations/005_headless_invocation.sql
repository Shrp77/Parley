-- Letting an agent be started when it owes an answer and nobody is running it.
--
-- SPEC.md §11.1 classes an agent as Full, Pull-only or Headless, and §11.2 calls the headless
-- tier the important one: a responder need not be an agent at all. Until now that tier was a
-- string in a column. Nothing invoked anything, so every exchange needed a person to open a
-- session at each end -- which is not what the protocol is for.
--
-- The flag below is deliberately not the tier. A repository with a live Claude Code session is
-- genuinely Pull: it holds native tools and drains its inbox on demand, and that is a true
-- description of it while somebody is working in it. The same repository with no session open is
-- reachable only by starting one. So this records a capability -- "you may start me" -- rather
-- than reclassifying what the agent is, and the two combine: pull while live, invokable while
-- not. Overloading `tier` would have made an agent change class every time a window closed.

ALTER TABLE agents ADD COLUMN invoke_when_idle INTEGER NOT NULL DEFAULT 0
    CHECK (invoke_when_idle IN (0, 1));

-- What invocation cost, kept for the same reason notify_volume is kept: the decision about
-- whether to bound it is deferred until there is evidence, and the evidence has to outlive the
-- messages that produced it.
--
-- Unbounded on purpose, matching the Notify decision. An agent that answers without being asked
-- is an agent that bills without being asked, so what is recorded is chosen to make a runaway
-- visible early: not just how many, but how long they took and how often they failed. A loop
-- between two agents shows up here as a rising count against a flat set of participants long
-- before it shows up on an invoice.
CREATE TABLE invocations (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,

    -- Who was started. Not a foreign key to agents on delete, for the reason message_referrals
    -- gives: this outlives whatever produced it, and nothing deletes an agent anyway.
    agent         TEXT    NOT NULL REFERENCES agents(name),

    -- Why. An invocation that answers nothing is the interesting one -- it means the agent was
    -- started, spent tokens, and either could not find the work or chose not to do it.
    owed_before   INTEGER NOT NULL CHECK (owed_before >= 0),
    owed_after    INTEGER NOT NULL CHECK (owed_after >= 0),

    started_utc   TEXT    NOT NULL,
    elapsed_ms    INTEGER NOT NULL CHECK (elapsed_ms >= 0),

    -- The process's own verdict. Zero is success; anything else is the agent's runner failing,
    -- which is a deployment problem rather than a protocol one and must not be silent.
    exit_code     INTEGER NOT NULL,

    -- Whether the invoker gave up waiting. Separated from exit_code because a timeout is not a
    -- failure of the agent: it is a question that took longer than the invoker was willing to
    -- hold a process open for, and the two need different responses.
    timed_out     INTEGER NOT NULL DEFAULT 0 CHECK (timed_out IN (0, 1)),

    -- No output is stored. What the agent said travels through the protocol as messages, under
    -- the retention policy of §9.4; capturing stdout here would make a second, unpruned copy of
    -- everything an agent ever wrote, outside every guard that governs the first.
    --
    -- owed_after is deliberately unconstrained against owed_before. It can legitimately be
    -- larger: an invocation takes time, and a peer may ask this agent something while it runs.
    -- A constraint saying otherwise would refuse a true row on a busy deployment.
    CHECK (owed_before >= 0 AND owed_after >= 0)
) STRICT;

CREATE INDEX ix_invocations_agent ON invocations(agent, started_utc);
