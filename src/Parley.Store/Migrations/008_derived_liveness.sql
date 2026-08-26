-- Liveness became a reading of last_seen_utc rather than a value somebody writes down, so two
-- of the five stored statuses no longer mean anything. SPEC.md §14.
--
-- What was wrong with storing it. 'live' was written on attach and again on every sweep, and
-- the transition out of it was written only by a clean detach -- so a session that was killed,
-- crashed, or lost its machine left a row reading 'live' for ever, with a frozen timestamp
-- beside it that only the supervisor ever compared. Everything that read the registry believed
-- it. 'unreachable' had the same shape from the other end: written on evidence that could go
-- stale, and cleared by nothing.
--
-- A timestamp cannot lie in that direction, because it stops advancing when the process stops,
-- however impolitely. So Live, NotResponding and Unreachable are derived from last_seen_utc at
-- the point of reading. What remains storable is what a timestamp cannot imply:
--
--   'offline'  registered and never activated -- no session has ever attached
--   'live'     has attached; the age of last_seen_utc decides what that means now
--   'retired'  gone for good, by a person's word or by the broker finding no workspace
--
-- The mapping is not a rename. 'registered' becomes 'offline' because the new Offline means
-- exactly what the old Registered did. 'unreachable' becomes 'offline' and loses nothing: those
-- rows carry a last_seen_utc, and the derivation reads the silence off it and reports
-- Unreachable again without help. A row that somehow has no timestamp was never reachable to
-- begin with, and Offline is the honest answer for it.
--
-- Retirement is untouched. It is a judgement about the future rather than an observation of the
-- past, no timestamp implies it, and a derived rule would clear it the moment a deleted folder
-- reappeared -- which is the control-that-does-not-hold §14 warns about.

UPDATE agents SET status = 'offline' WHERE status IN ('registered', 'unreachable');

-- The narrower set is enforced by trigger rather than by tightening the CHECK, and that is a
-- deliberate choice rather than a shortcut. SQLite cannot alter a constraint in place, so
-- narrowing it means rebuilding the table -- and this table carries the control-character GLOB
-- that keeps a peer's `owns` line from imitating the entries around it in a roster, the JSON
-- bound on adapter_config, and the consent trigger from 006. Retyping all of that to change one
-- IN list is how a security control gets dropped by an edit that was about something else.
--
-- Same shape as 002, which bounded clarify_depth_cap by trigger for the same reason: the
-- existing CHECK stays as a wider floor, and this rejects what the wider floor still admits.

CREATE TRIGGER tr_agents_status_is_storable_on_insert
BEFORE INSERT ON agents
FOR EACH ROW WHEN NEW.status NOT IN ('offline', 'live', 'retired')
BEGIN
  SELECT RAISE(ABORT, 'status must be offline, live or retired; liveness is derived');
END;

CREATE TRIGGER tr_agents_status_is_storable_on_update
BEFORE UPDATE OF status ON agents
FOR EACH ROW WHEN NEW.status NOT IN ('offline', 'live', 'retired')
BEGIN
  SELECT RAISE(ABORT, 'status must be offline, live or retired; liveness is derived');
END;
