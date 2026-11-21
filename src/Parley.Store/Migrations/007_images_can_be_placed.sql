-- Migration 007. Upgrades a schema 6 store in place and keeps every row (SPEC.md 9.2).
--
-- A shared file may now be a PNG, JPEG, GIF or WebP image as well as .md or .txt (the owner's decision, 2026-11-05;
-- SPEC.md 9.5). SVG stays out, because it can carry scripts. A CHECK cannot be altered, so shared_files is rebuilt as 002
-- and 005 rebuilt theirs: built beside the old table, copied, the old one dropped, the new one taking its name. Nothing
-- references it by key, so dropping cascades nowhere. Dropping takes its triggers, which are recreated below from their
-- latest text, after the copy so the copy fires none of them.

CREATE TABLE shared_files_007 (
  conversation_id TEXT    NOT NULL REFERENCES conversation_folders(conversation_id),
  -- The name in upper case. Every name is ASCII, so SQLite's upper() is the key, and every key on a
  -- file is this: 'Readme.md' and 'README.md' are one row, as they are one file on the disks of
  -- Windows and macOS.
  name_key        TEXT    NOT NULL CHECK (name_key = upper(name)),
  -- As placed, after renaming. Made of letters A to Z, digits, '.', '_' and '-', which every disk on
  -- the three platforms accepts and which carries no text of the placer's beyond a word, ending in
  -- an extension no handler runs a program for (§13). The folder, a separator and the name together
  -- are bounded by tr_shared_file_path_fits.
  name            TEXT    NOT NULL
                  CHECK (length(name) BETWEEN 4 AND 254
                         AND (lower(name) GLOB '*.md' OR lower(name) GLOB '*.txt'
                              OR lower(name) GLOB '*.png' OR lower(name) GLOB '*.jpg' OR lower(name) GLOB '*.jpeg'
                              OR lower(name) GLOB '*.gif' OR lower(name) GLOB '*.webp')
                         AND name NOT GLOB '*[^A-Za-z0-9._-]*'
                         AND name NOT GLOB '.*'
                         AND upper(substr(name, 1, instr(name, '.') - 1)) NOT IN
                             ('CON', 'PRN', 'AUX', 'NUL',
                              'COM1', 'COM2', 'COM3', 'COM4', 'COM5', 'COM6', 'COM7', 'COM8', 'COM9',
                              'LPT1', 'LPT2', 'LPT3', 'LPT4', 'LPT5', 'LPT6', 'LPT7', 'LPT8', 'LPT9')),
  size_bytes      INTEGER NOT NULL CHECK (size_bytes >= 0),
  -- The broker's SHA-256 of the stored bytes, never the placer's.
  sha256          TEXT    NOT NULL CHECK (length(sha256) = 64 AND sha256 NOT GLOB '*[^0-9a-f]*'),
  -- An agent, or NULL for a file the person put there. Text rather than a key: purge deletes these
  -- rows by name and reports them (§14), where a key would cascade silently or refuse the purge.
  placed_by       TEXT,
  placed_utc      TEXT    NOT NULL,
  changed_utc     TEXT    NOT NULL,
  -- The file's last-write time when it was last reconciled, so a change made outside Parley is
  -- noticed on the next list or fetch without hashing every file again.
  modified_ticks  INTEGER NOT NULL,

  PRIMARY KEY (conversation_id, name_key)
) STRICT;

INSERT INTO shared_files_007
  (conversation_id, name_key, name, size_bytes, sha256, placed_by, placed_utc, changed_utc, modified_ticks)
SELECT conversation_id, name_key, name, size_bytes, sha256, placed_by, placed_utc, changed_utc, modified_ticks
  FROM shared_files;

DROP TABLE shared_files;

ALTER TABLE shared_files_007 RENAME TO shared_files;

CREATE TRIGGER tr_placer_is_registered BEFORE INSERT ON shared_files
WHEN NEW.placed_by IS NOT NULL
BEGIN
  SELECT RAISE(ABORT, 'a file is placed by a registered agent or by the person')
   WHERE NOT EXISTS (SELECT 1 FROM agents WHERE name = NEW.placed_by);
END;

CREATE TRIGGER tr_shared_file_stays_put
BEFORE UPDATE OF conversation_id, placed_by, placed_utc ON shared_files
WHEN NEW.conversation_id IS NOT OLD.conversation_id
  OR NEW.placed_utc IS NOT OLD.placed_utc
  OR (NEW.placed_by IS NOT OLD.placed_by
      AND NOT (NEW.placed_by IS NOT NULL AND OLD.placed_by IS NOT NULL
               AND EXISTS (SELECT 1 FROM agents WHERE name = NEW.placed_by)
               AND NOT EXISTS (SELECT 1 FROM agents WHERE name = OLD.placed_by)))
BEGIN
  SELECT RAISE(ABORT, 'a file keeps its conversation, its placer and when it was placed');
END;

CREATE TRIGGER tr_shared_file_path_fits BEFORE INSERT ON shared_files
BEGIN
  SELECT RAISE(ABORT, 'a file''s path inside the location is at most 255 characters')
   WHERE (SELECT length(folder) FROM conversation_folders
           WHERE conversation_id = NEW.conversation_id) + 1 + length(NEW.name) > 255;
END;

CREATE TRIGGER tr_shared_file_path_fits_on_rename BEFORE UPDATE OF name ON shared_files
BEGIN
  SELECT RAISE(ABORT, 'a file''s path inside the location is at most 255 characters')
   WHERE (SELECT length(folder) FROM conversation_folders
           WHERE conversation_id = NEW.conversation_id) + 1 + length(NEW.name) > 255;
END;
