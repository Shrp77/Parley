-- Migration 003. Upgrades a schema 2 store in place and keeps every row (SPEC.md 9.2).
--
-- 1. A notice's sha256_now now says a file was tampered with: the hash found on disk where the placed file should be
--    (SPEC.md 9.5). Builds from before placed files were immutable wrote it for a file an agent changed, and such a
--    notice read now would call that file tampered with, so any still waiting are deleted.
--
-- 2. Problems with the file location, kept until the person dismisses them: a file moved to quarantine because its
--    bytes no longer match what was placed, and anything a sweep could not move or delete because another program
--    held it.

DELETE FROM file_notices WHERE sha256_now IS NOT NULL;

CREATE TABLE file_problems (
  -- AUTOINCREMENT, so a dismissed problem's id is never a later problem's: a person dismissing from a page that
  -- had gone stale would otherwise dismiss whichever problem took its id.
  id              INTEGER PRIMARY KEY AUTOINCREMENT,
  kind            TEXT    NOT NULL CHECK (kind IN ('quarantined', 'locked')),
  -- The conversation whose folder it was in, or NULL for an entry no conversation owns. Not a key: a problem
  -- outlives its conversation being destroyed.
  conversation_id TEXT,
  -- The entry, as a path inside the location with '/' between its parts.
  path            TEXT    NOT NULL CHECK (length(path) >= 1),
  sha256_placed   TEXT    CHECK (sha256_placed IS NULL
                                 OR (length(sha256_placed) = 64 AND sha256_placed NOT GLOB '*[^0-9a-f]*')),
  sha256_found    TEXT    CHECK (sha256_found IS NULL
                                 OR (length(sha256_found) = 64 AND sha256_found NOT GLOB '*[^0-9a-f]*')),
  -- Where a quarantined file was moved, inside the quarantine directory.
  moved_to        TEXT,
  detail          TEXT    NOT NULL CHECK (length(detail) >= 1),
  raised_utc      TEXT    NOT NULL CHECK (unixepoch(raised_utc) IS NOT NULL),
  dismissed_utc   TEXT    CHECK (dismissed_utc IS NULL OR unixepoch(dismissed_utc) IS NOT NULL),

  CHECK ((kind = 'quarantined') = (moved_to IS NOT NULL)),
  CHECK (kind <> 'quarantined' OR (sha256_placed IS NOT NULL AND sha256_found IS NOT NULL
                                   AND sha256_placed <> sha256_found))
) STRICT;

-- What the person has not dismissed, oldest first, and whether an entry is already raised.
CREATE INDEX ix_file_problems_open ON file_problems(path, kind) WHERE dismissed_utc IS NULL;
