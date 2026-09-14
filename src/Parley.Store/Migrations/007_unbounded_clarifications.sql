-- §5.2's clarification cap becomes off by default, the way §7.1's pair cap did in 006, and for the
-- same reason: nothing cuts a line of work off before its participants are finished (principle
-- 2.8), and clarification is how two participants converge. The default was 200.
--
-- WHY 2147483647 RATHER THAN 0. Zero already means something -- no clarification at all -- and a
-- deployment may want exactly that. The largest integer means nothing else.
--
-- WHY THE TRIGGERS AND NOT THE CHECK. exchanges carries CHECK (clarify_depth <= clarify_depth_cap).
-- SQLite cannot alter a CHECK, and this migrator cannot rebuild exchanges, which two tables
-- reference by foreign key -- 006 carries the probe that established it. It does not need to: at a
-- cap of the largest integer the CHECK can never bind. What stood in the way is the range of caps,
-- and that is held by the two triggers below, which can be replaced.
--
-- WHAT SURVIVES. A real cap is still bounded to 0..1000, so a bug writing a large number is still
-- refused rather than silently switching the rule off. The one value beyond the band is the one
-- that means "off", and it has to be written on purpose.
--
-- The trigger bodies are 001's, verbatim, with one clause added to each.

DROP TRIGGER tr_clarify_cap_bounded_on_insert;
DROP TRIGGER tr_clarify_cap_bounded_on_update;

CREATE TRIGGER tr_clarify_cap_bounded_on_insert BEFORE INSERT ON exchanges
FOR EACH ROW WHEN NEW.clarify_depth_cap NOT BETWEEN 0 AND 1000
  AND NEW.clarify_depth_cap <> 2147483647
BEGIN
  SELECT RAISE(ABORT, 'clarify_depth_cap must be between 0 and 1000');
END;

-- Guards a path no operation takes today. The cap is written once at admission, and that is a
-- fact about the current broker rather than about the schema: raising it on a live exchange
-- would grant depth retroactively that was refused when it was asked for.
CREATE TRIGGER tr_clarify_cap_bounded_on_update BEFORE UPDATE OF clarify_depth_cap ON exchanges
FOR EACH ROW WHEN NEW.clarify_depth_cap NOT BETWEEN 0 AND 1000
  AND NEW.clarify_depth_cap <> 2147483647
BEGIN
  SELECT RAISE(ABORT, 'clarify_depth_cap must be between 0 and 1000');
END;
