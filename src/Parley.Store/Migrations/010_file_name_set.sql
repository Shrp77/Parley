-- Parley schema, migration 010: a shared file's name is made of letters A to Z, digits, '.', '_' and '-'.
-- Applied inside a transaction by SchemaMigrator; do not add BEGIN/COMMIT here.
--
-- SPEC.md section 9.5 replaced a list of characters some disk refuses with a set a name is made of,
-- on the owner's call: a placement is renamed, each character outside the set becoming '_', and
-- refused when that spelling is already another file's name. Section 9.2 is the schema this
-- produces, and SchemaConformanceTests compares them.
--
-- **shared_files is rebuilt, and nothing references it.** 006 and 008 record why a parent cannot be
-- dropped inside this migrator. shared_files is not one: file_fetches and file_notices name a file
-- by conversation and key rather than by a foreign key, precisely so a record can outlive its file.
-- Its four triggers go with it and are created again below.
--
-- **A row whose name is outside the set is not carried forward**, and its file is left on disk. A
-- file with such a name is not in the location (section 9.5), whoever put it there, and the broker
-- renames nothing on a disk; so a row for one would describe a file the rules say is absent. Every
-- agent that fetched it is told it is gone, as a removal tells it, and its fetch record goes. The
-- person's surfaces count such a file as not listed, which is how it is found again.
--
-- name_key is now held to upper(name). The key folded every letter so that two spellings of one
-- accented name were one row; with every name ASCII, SQLite's own upper() computes the same key,
-- and a key that disagreed with its name would be a second name for one file.

INSERT INTO file_notices
    (agent, conversation_id, name_key, name, sha256_had, sha256_now, size_bytes, changed_utc)
SELECT ff.agent, ff.conversation_id, ff.name_key, ff.name, ff.sha256, NULL, NULL,
       strftime('%Y-%m-%dT%H:%M:%f', 'now') || '0000Z'
  FROM file_fetches ff
  JOIN agents a ON a.name = ff.agent
  JOIN shared_files f ON f.conversation_id = ff.conversation_id AND f.name_key = ff.name_key
 WHERE a.status = 'active'
   AND f.name GLOB '*[^A-Za-z0-9._-]*'
ON CONFLICT (agent, conversation_id, name_key) DO UPDATE
   SET name = excluded.name, sha256_had = excluded.sha256_had, sha256_now = excluded.sha256_now,
       size_bytes = excluded.size_bytes, changed_utc = excluded.changed_utc;

DELETE FROM file_fetches
 WHERE EXISTS (SELECT 1 FROM shared_files f
                WHERE f.conversation_id = file_fetches.conversation_id
                  AND f.name_key = file_fetches.name_key
                  AND f.name GLOB '*[^A-Za-z0-9._-]*');

DROP TRIGGER tr_placer_is_registered;
DROP TRIGGER tr_shared_file_stays_put;
DROP TRIGGER tr_shared_file_path_fits;
DROP TRIGGER tr_shared_file_path_fits_on_rename;

CREATE TABLE shared_files_010 (
  conversation_id TEXT    NOT NULL REFERENCES conversation_folders(conversation_id),
  -- The name in upper case. Every name is ASCII, so SQLite's upper() is the key, and every key on a
  -- file is this: 'Readme.md' and 'README.md' are one row, as they are one file on the disks of
  -- Windows and macOS.
  name_key        TEXT    NOT NULL CHECK (name_key = upper(name)),
  -- As placed, after renaming. Made of letters A to Z, digits, '.', '_' and '-', which every disk on
  -- the three platforms accepts and which carries no text of the placer's beyond a word, ending .md
  -- or .txt so that nothing placed has a handler that runs a program (§13). The folder, a separator
  -- and the name together are bounded by tr_shared_file_path_fits.
  name            TEXT    NOT NULL
                  CHECK (length(name) BETWEEN 4 AND 254
                         AND (lower(name) GLOB '*.md' OR lower(name) GLOB '*.txt')
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

INSERT INTO shared_files_010
    (conversation_id, name_key, name, size_bytes, sha256, placed_by, placed_utc, changed_utc, modified_ticks)
SELECT conversation_id, upper(name), name, size_bytes, sha256, placed_by, placed_utc, changed_utc, modified_ticks
  FROM shared_files
 WHERE name NOT GLOB '*[^A-Za-z0-9._-]*';

DROP TABLE shared_files;
ALTER TABLE shared_files_010 RENAME TO shared_files;

-- A placer is a registered agent or the person. Checked on insert only: a retired placer keeps its
-- files, and a purged one's go with its conversations (§14).
CREATE TRIGGER tr_placer_is_registered BEFORE INSERT ON shared_files
WHEN NEW.placed_by IS NOT NULL
BEGIN
  SELECT RAISE(ABORT, 'a file is placed by a registered agent or by the person')
   WHERE NOT EXISTS (SELECT 1 FROM agents WHERE name = NEW.placed_by);
END;

-- Who placed a file decides who may replace or remove it (§9.5), so neither that nor when may be
-- rewritten, and a file does not move between conversations. A replacement, an edit and a rename
-- all update the row in place and keep all three.
CREATE TRIGGER tr_shared_file_stays_put
BEFORE UPDATE OF conversation_id, placed_by, placed_utc ON shared_files
WHEN NEW.conversation_id IS NOT OLD.conversation_id
  OR NEW.placed_by IS NOT OLD.placed_by
  OR NEW.placed_utc IS NOT OLD.placed_utc
BEGIN
  SELECT RAISE(ABORT, 'a file keeps its conversation, its placer and when it was placed');
END;

-- A file's path inside the location is at most 255 characters (§9.5): its folder, a separator and
-- its name. Triggers because the folder is in another table, which a CHECK cannot read.
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
