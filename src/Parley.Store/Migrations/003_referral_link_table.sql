-- Referrals, kept where retention cannot reach them.
--
-- SPEC.md §15 asks for the `out_of_scope` rate per pair, and calls it the signal that the
-- ownership map is wrong somewhere. Today that is unmeasurable beyond the retention window:
-- `redirect_to` lives only inside `messages.payload_json`, §9.4 prunes by `DELETE FROM messages`,
-- and the referral goes with the row. A telemetry item that can only see the last three days
-- cannot show a boundary being argued about for a month, which is exactly the shape of problem
-- it exists to surface.
--
-- The pattern is already here rather than new. `exchanges.resolution` is denormalised onto the
-- exchange for the same reason and says so: it is "the only column distinguishing a ghost from
-- an answer once the message text has been pruned". This is that move, for referrals.
--
-- What it deliberately does not do is keep any prose. A referral is three names, a sub-question
-- identifier and a timestamp; the reasoning that accompanied it is in the message and is pruned
-- with it. §16 says Parley is a channel and not a store, and §9.4 argues that a log which grows
-- forever grows the blast radius of a leaked database with it. Names are not that log.

CREATE TABLE message_referrals (
    -- Not a foreign key, and that is the whole design rather than an omission.
    --
    -- A plain reference would make `DELETE FROM messages` fail once a referral pointed at the
    -- row, so the first prune after the first referral would error and retention would stop
    -- working. ON DELETE CASCADE would delete the referral with the message, which is precisely
    -- the outcome this table exists to prevent. Both available options destroy one of the two
    -- things that have to hold, so the column is a plain integer that names a message which may
    -- no longer exist. A reader must treat it as a historical identifier, not as a join target.
    message_id      INTEGER NOT NULL,

    -- This one is a real reference. Nothing deletes an exchange -- retention only removes
    -- messages, and retirement changes an agent's status rather than removing it -- so the
    -- exchange outlives the message that referred, and the join stays sound.
    exchange_id     INTEGER REFERENCES exchanges(id),

    -- Which sub-question was referred. The reason this table is per referral rather than a
    -- rollup: a question that splits across two boundaries refers one part and answers another,
    -- and a per-pair counter cannot say which part.
    sub_question    TEXT    NOT NULL CHECK (length(sub_question) BETWEEN 1 AND 64),

    from_agent      TEXT    NOT NULL REFERENCES agents(name),
    to_agent        TEXT    NOT NULL REFERENCES agents(name),
    redirected_to   TEXT    NOT NULL REFERENCES agents(name),
    created_utc     TEXT    NOT NULL,

    -- One row per (message, sub-question, target). Naming the same agent twice for one
    -- sub-question is a duplicate rather than two referrals; the trigger below collapses those
    -- with DISTINCT so a repeated name records once instead of failing an answer somebody is
    -- waiting on.
    PRIMARY KEY (message_id, sub_question, redirected_to),

    -- A self-referral asserts both halves of a contradiction: out_of_scope says the boundary is
    -- not mine, and the referral says it is. tr_referral_targets_exist already refuses it on the
    -- message, and this restates it as a constraint because the two guards protect different
    -- things -- that one protects the peer who receives the referral, this one protects the
    -- record from ever containing a contradiction even if the other is someday relaxed.
    CHECK (redirected_to <> from_agent),

    -- Referring the asker to itself is the same contradiction seen from the other side: it
    -- answers "not mine, ask yourself", which is not a referral.
    CHECK (redirected_to <> to_agent),
    CHECK (from_agent <> to_agent)
) STRICT;

-- The read §15 actually asks for.
CREATE INDEX ix_referrals_pair ON message_referrals(from_agent, redirected_to);

-- Populated by the schema rather than by the broker, on §9.3's argument: an invariant that
-- application code maintains is an invariant application code can route around, and a referral
-- that was made but not recorded is invisible in exactly the way a missing telemetry signal is.
-- Writing it here also makes it atomic with the message it describes, without the broker holding
-- a second statement it could fail between.
--
-- Anchored at $.resolutions[*].redirect_to rather than searched for anywhere in the payload.
-- tr_referral_targets_exist scans the whole document because its job is to catch a name wherever
-- it appears; this one records structure, so it reads only where a referral is defined to live.
-- That makes what is recorded a subset of what was validated, which is the safe direction: every
-- row here passed the check, and a stray `redirect_to` nested in free text is refused by that
-- trigger without ever being recorded as a referral by this one.
--
-- json_tree over the sub-object handles both shapes SPEC.md §5.3 admits: a bare name yields one
-- text node, and a list yields one per element. Filtering on type = 'text' takes the elements
-- and skips the array node itself.
CREATE TRIGGER tr_referrals_recorded
AFTER INSERT ON messages
FOR EACH ROW
WHEN NEW.type = 'answer' AND json_valid(NEW.payload_json)
BEGIN
  -- DISTINCT rather than OR IGNORE, and the difference is not stylistic. OR IGNORE discards a
  -- row failing ANY constraint, not only a duplicate key -- so it silently swallowed both CHECKs
  -- below, and an answer referring the asker back to itself was accepted and recorded as nothing
  -- at all. The duplicate it was there to absorb is a repeated name inside one list, which
  -- produces identical rows across every column, so DISTINCT absorbs it while a contradiction
  -- still aborts the insert.
  INSERT INTO message_referrals
      (message_id, exchange_id, sub_question, from_agent, to_agent, redirected_to, created_utc)
  SELECT DISTINCT
         NEW.id,
         NEW.exchange_id,
         json_extract(r.value, '$.id'),
         NEW.from_agent,
         NEW.to_agent,
         t.value,
         NEW.created_utc
    FROM json_each(NEW.payload_json, '$.resolutions') r
    JOIN json_tree(r.value, '$.redirect_to') t
   WHERE t.type = 'text'
     AND json_extract(r.value, '$.id') IS NOT NULL;
END;
