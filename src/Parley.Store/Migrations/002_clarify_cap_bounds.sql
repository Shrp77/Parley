-- Bound clarify_depth_cap, closing the gap SPEC.md §9.3 names against itself.
--
-- §9.3 range-checks open_pair_cap so that a broker bug writing a large value cannot switch the
-- open-question rule off while leaving every constraint satisfied, and then says of the
-- neighbouring column: "clarify_depth_cap has the same shape and no such bound; that is a gap,
-- not a precedent to copy." This is that gap.
--
-- It matters more now than when it was written. The cap became configurable per deployment in
-- the same change as this migration, so the value reaching this column is no longer always the
-- constant 3 -- it comes from a launch environment, which is exactly the path a typo travels.
--
-- Enforced by trigger rather than by CHECK, and not by preference. SQLite cannot add a CHECK to
-- an existing table; doing it properly means the twelve-step rebuild, and `exchanges` is the
-- target of a composite foreign key from `messages`. Rebuilding a table that another table
-- references, to add a bound, risks more than the bound is worth. A trigger enforces the same
-- invariant against the same writes.
--
-- The band is 0 to 1000.
--
-- Zero is admitted deliberately: it means an answerer must answer with what it has, which is a
-- coherent policy rather than a mistake, and ExchangePolicy already permits it.
--
-- A thousand is far above any value anybody would choose on purpose -- the highest deliberate
-- setting we have a use for is 200, for measuring what depth agents actually reach when the cap
-- is not in their way -- and far below unbounded. That is the whole ambition: the worst a bug
-- can do is pick a bad value inside a sane band, rather than disable the rule. At the message
-- cap of 4000 characters a thousand clarifications is four megabytes spent on one exchange,
-- which holds a directional pair slot for the whole of it, so the ceiling is not merely
-- arbitrary largeness.

CREATE TRIGGER tr_clarify_cap_bounded_on_insert
BEFORE INSERT ON exchanges
FOR EACH ROW
WHEN NEW.clarify_depth_cap NOT BETWEEN 0 AND 1000
BEGIN
  SELECT RAISE(ABORT, 'clarify_depth_cap must be between 0 and 1000');
END;

-- The column is written once at admission and the broker has no operation that changes it, so
-- this guards a path nothing currently takes. It exists because "nothing currently takes it" is
-- a fact about today's code and the constraint is meant to outlive it: an UPDATE that raised the
-- cap on a live exchange would retroactively grant depth that was refused when it was asked for,
-- and would do it to an exchange already carrying a clarify_depth the CHECK below had approved.
CREATE TRIGGER tr_clarify_cap_bounded_on_update
BEFORE UPDATE OF clarify_depth_cap ON exchanges
FOR EACH ROW
WHEN NEW.clarify_depth_cap NOT BETWEEN 0 AND 1000
BEGIN
  SELECT RAISE(ABORT, 'clarify_depth_cap must be between 0 and 1000');
END;
