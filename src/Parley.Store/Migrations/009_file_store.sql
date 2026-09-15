-- Parley schema, migration 009: protocol 0.6, files cross between repositories.
-- Applied inside a transaction by SchemaMigrator; do not add BEGIN/COMMIT here.
--
-- SPEC.md section 9.5 describes the file location and section 9.2 carries this schema, which
-- SchemaConformanceTests compares against what this migration builds. Only new tables, so nothing is
-- rebuilt and no row written under 0.5 changes: a store at schema 8 gains five empty tables.
--
-- The text below is section 9.2's own, including its comments, so that the two cannot drift apart in a
-- constraint's wording while their shapes still agree.

-- Where a conversation's files live on disk (§9.5): its identifier, or the identifier followed by
-- ~2, ~3 and so on when that is not a usable folder name. Chosen once and kept until the
-- conversation goes, so the name a person was shown stays the name on disk, and a file copied into
-- the folder by hand is still traced to its conversation after its last placed file was removed.
--
-- Not cascading from conversations, like every reference to one (§14): destroy and purge delete
-- these rows by name and report how many went.
CREATE TABLE conversation_folders (
  conversation_id TEXT PRIMARY KEY REFERENCES conversations(id),
  -- Unique ignoring case, because Windows and macOS hold 'Pricing' and 'pricing' as one folder.
  -- NOCASE folds only ASCII, which is enough here: an identifier and its suffix are ASCII.
  folder          TEXT NOT NULL COLLATE NOCASE UNIQUE
                  CHECK (length(folder) BETWEEN 1 AND 72
                         AND folder NOT GLOB '*[^a-zA-Z0-9._~-]*'),
  created_utc     TEXT NOT NULL,

  -- The identifier itself, or the identifier and a suffix of 2 or more. '~' cannot occur in an
  -- identifier, so a suffixed folder can never be another conversation's own name.
  CHECK (folder = conversation_id COLLATE BINARY
         OR (substr(folder, 1, length(conversation_id) + 1) = conversation_id || '~'
             AND substr(folder, length(conversation_id) + 2) NOT GLOB '*[^0-9]*'
             AND substr(folder, length(conversation_id) + 2) NOT GLOB '0*'
             AND CAST(substr(folder, length(conversation_id) + 2) AS INTEGER) >= 2))
) STRICT;

-- A folder keeps its name. The name is on disk and on a person's screen, and changing the row would
-- leave the files where they were under a name nothing records.
CREATE TRIGGER tr_conversation_folder_is_fixed BEFORE UPDATE ON conversation_folders
BEGIN
  SELECT RAISE(ABORT, 'a conversation''s folder keeps its name');
END;

-- One row per file in the file location (§9.5). The bytes stay on disk: a row per megabyte would
-- grow the log and every backup, and a person opens the folder itself.
CREATE TABLE shared_files (
  conversation_id TEXT    NOT NULL REFERENCES conversation_folders(conversation_id),
  -- The name in upper case for every letter, computed by the broker because SQL folds only ASCII.
  -- Every key on a file is this, so 'Übersicht.md' and 'übersicht.md' are one row, as they are one
  -- file on the disks that hold them.
  name_key        TEXT    NOT NULL CHECK (length(name_key) >= 1 AND name_key = upper(name_key)),
  -- As placed. One flat component a disk on any of the three platforms accepts, ending .md or .txt
  -- so that nothing placed has a handler that runs a program (§13). The folder, a separator and the
  -- name together are bounded by tr_shared_file_path_fits.
  name            TEXT    NOT NULL
                  CHECK (length(name) BETWEEN 4 AND 254
                         AND (lower(name) GLOB '*.md' OR lower(name) GLOB '*.txt')
                         AND name NOT GLOB '*[/\:*?"<>|]*'
                         AND name NOT GLOB ('*[' || char(1) || '-' || char(31) || char(127) || ']*')
                         AND name NOT GLOB '.*'
                         AND name NOT GLOB ' *'
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

-- That an agent fetched a file, and the content it received (§9.5): what decides who is told when
-- the file changes, and what the change is compared against. Written only once the adapter reports
-- writing and verifying the content the broker streamed to it.
--
-- Not a key to shared_files, because a record must outlive its file long enough for the notice that
-- the file is gone. It cascades from its agent, whose row it is, and goes with its conversation by
-- name (§14).
CREATE TABLE file_fetches (
  agent           TEXT NOT NULL REFERENCES agents(name) ON DELETE CASCADE,
  conversation_id TEXT NOT NULL REFERENCES conversations(id),
  name_key        TEXT NOT NULL CHECK (length(name_key) >= 1 AND name_key = upper(name_key)),
  name            TEXT NOT NULL,
  sha256          TEXT NOT NULL CHECK (length(sha256) = 64 AND sha256 NOT GLOB '*[^0-9a-f]*'),
  fetched_utc     TEXT NOT NULL,

  PRIMARY KEY (agent, conversation_id, name_key)
) STRICT;

-- A change notice (§9.5), waiting for one agent about one file it fetched. Not a message (§5), so
-- not in the message log. The conversation is not a key, so a notice outlives its conversation being
-- destroyed and the agent is still told its file is gone.
CREATE TABLE file_notices (
  agent           TEXT    NOT NULL REFERENCES agents(name) ON DELETE CASCADE,
  conversation_id TEXT    NOT NULL,
  name_key        TEXT    NOT NULL CHECK (length(name_key) >= 1 AND name_key = upper(name_key)),
  name            TEXT    NOT NULL,
  sha256_had      TEXT    NOT NULL
                  CHECK (length(sha256_had) = 64 AND sha256_had NOT GLOB '*[^0-9a-f]*'),
  -- NULL when the file is gone, and then so is its size.
  sha256_now      TEXT    CHECK (sha256_now IS NULL
                                 OR (length(sha256_now) = 64 AND sha256_now NOT GLOB '*[^0-9a-f]*')),
  size_bytes      INTEGER CHECK (size_bytes IS NULL OR size_bytes >= 0),
  changed_utc     TEXT    NOT NULL,

  -- At most one per agent per file, carrying the latest state.
  PRIMARY KEY (agent, conversation_id, name_key),

  -- A notice that nothing changed cannot exist: a change back to what was fetched deletes the row.
  CHECK (sha256_now IS NULL OR sha256_now <> sha256_had),
  CHECK ((sha256_now IS NULL) = (size_bytes IS NULL))
) STRICT;

-- A collection takes the oldest first (§9.5), so a burst of new changes cannot hold an old one back.
CREATE INDEX ix_file_notices_collection ON file_notices(agent, changed_utc);

-- Placements counted per agent, so a size limit can be set from evidence as rule 8.5's cap was. Not
-- a key to agents: the evidence outlives a purge.
CREATE TABLE file_placements (
  agent      TEXT    NOT NULL PRIMARY KEY,
  placements INTEGER NOT NULL CHECK (placements > 0),
  bytes      INTEGER NOT NULL CHECK (bytes >= 0),
  last_utc   TEXT    NOT NULL
) STRICT;
