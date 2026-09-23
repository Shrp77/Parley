# Research: Parley file store

Phase 0 for `specs/015-file-store/`. Every decision below was taken against the code as it stands on `015-file-store`
(branched from `ef3b136`, 0.26.0, protocol 0.5, schema 8) and against the owner's decisions recorded in `spec.md`.
Each entry gives the decision, why, and what was rejected.

## R1. Bytes travel as streamed chunks on the existing gRPC connection

**Decision.** Two streaming RPCs on the `Agents` service: `PlaceFile` is client-streaming (a header frame naming the
conversation and file, then data frames), `FetchFile` is server-streaming (a header frame with size, hash and placer,
then data frames). Chunks of **256 KiB**. The broker hashes incrementally as it receives and never holds a whole file in
memory.

**Rationale.** `ParleyServer` calls `AddGrpc()` with no size options, so gRPC's default 4 MiB message limit applies; a
100 MB file (SC-002) cannot be a unary message, and raising the limit would make the broker allocate whole files per
call. Streaming stays on the one connection an agent already holds (FR-002), needs no second port or credential, and is
cancelled when the agent's call is — which is what releases a download's read-only hold when the agent goes (FR-012).

**Alternatives rejected.** A raised message size (memory proportional to file size, per concurrent call). A separate
HTTP endpoint for bytes (a second transport and a second thing to confine to a trusted path under §16). A shared network
folder (rejected by the owner: agents on other machines cannot be assumed to reach one).

## R2. The location sits beside the store, one folder per conversation

**Decision.** `<store path>.files/` beside the database, with one sub-folder per conversation that has files, and a
staging folder, `<store path>.files-incoming/`, **beside the location rather than inside it**, for placements in
progress (the owner's call, answering `/speckit-analyze` M7): the folder `files-folder` opens and the watcher watches then
never holds a half-uploaded file, and on the same disk a finished upload still moves into place instantly. `StoreFiles` gains `Files = ".files"` and `FilesIncoming = ".files-incoming"`, so `project remove --destroy`
(which already deletes every suffix `StoreFiles` names) removes the location with the store. `BrokerRuntime.OpenInMemory`
and `Over` take an optional files root and default to a temporary directory the runtime deletes on dispose, so tests
never write beside a real store.

**Rationale.** `StoreFiles` exists precisely so `Parley.Setup` and the broker agree on what belongs to a store without
referencing each other. A conversation's folder is what `files-folder` opens (owner's decision).

**Folder names** (the owner's call, answering `/speckit-analyze` M3). The conversation identifier when that is a safe and
unique folder name, otherwise the identifier followed by `~2`, `~3` and so on, chosen when the folder is first created,
recorded in `conversation_folders`, and never recomputed while the conversation exists — not even when its last file
is removed, so a file later copied into that folder by hand is still traced to its conversation (`/speckit-analyze` H4).
A folder in the location that `conversation_folders` does not name belongs to no conversation, and its files are ignored.
When a folder name is chosen, every folder already on disk counts as taken, including one a person made by hand, so a new
conversation never adopts a stray folder's files (`/speckit-analyze` L10). Conversation identifiers match `[A-Za-z0-9._-]{1,64}`, so they can never
contain `~`, and a suffixed name cannot collide with any other conversation's.
An identifier is unsafe as a folder name when it is a Windows reserved device name with or without an extension (`con`,
`nul.md`, `COM1` …), ends in a dot or a space (Windows strips them), begins with a dot (hidden on Linux and macOS), or
differs only in case from a folder already created (`Pricing` beside `pricing`: one folder on Windows and macOS). The first
draft prefixed reserved names with `_`, which collided with a conversation actually named `_con`, because such a folder
name is a legal identifier; conversation identifiers are older than this feature and cannot be narrowed now.

**Alternatives rejected.** A folder under the user's profile (not destroyed with the project, and not beside the store a
person already knows how to find). Hashed folder names (unreadable in the file browser the owner asked to open).

## R3. Metadata lives in the store, and the invariants live in its schema

**Decision.** Migration `009_file_store.sql`, schema 9, adds five tables — `conversation_folders`, `shared_files`,
`file_fetches`, `file_notices`, `file_placements` (see `data-model.md`). Constitution Principle V puts the invariants in storage: name
uniqueness per conversation (case-insensitively), the name rules, the hash's shape, at most one notice per agent per
file, and a trigger that a placer is a registered agent or the person.

**Rationale.** Two concurrent placements of one new name must have exactly one winner (spec edge case); a unique index
decides that for every caller, where an application check would race. The bytes stay on disk, not in SQLite: a 100 MB
row would bloat the WAL and every backup, and the owner wants a folder a person can open.

**Case.** Uniqueness is on a stored case-folded `name_key` (R4), because on Windows and macOS `Report.txt` and
`report.txt` are one file on disk; two rows for one file would let two agents each believe they own it. The first draft
used `COLLATE NOCASE`, which folds only ASCII, so `Übersicht.md` and `übersicht.md` would have been two rows for one file
(`/speckit-analyze` M1).

**Long paths.** With 255 characters inside the location, a file's absolute path on Windows can pass the old 260-character
limit. .NET on Windows handles long paths itself, so the broker and adapter are unaffected; an editor or file browser a
person opens may not be, so a launch that fails reports the file's path rather than failing silently.

**Placer and purge.** `placed_by` is text copied at placement, not a foreign key. Purging an agent deletes every
conversation it took part in, and placing a file made it a participant, so its files go with those conversations (the
owner's call, answering `/speckit-analyze` H6): `AgentRepository.PurgeAsync` deletes their rows explicitly and counts
them in its toll, the server deletes their folders once any download in progress finishes, and fetchers get "gone"
notices. A key would delete silently where purge reports what it destroys.

## R4. The name rules

**Decision.** A file name ends in `.md` or `.txt`, compared without regard to case (the owner's rule, 2026-09-14 — code
is placed as `PriceSnapshot.cs.txt`), and its path inside the location — conversation folder, separator, name — is at most
**255 characters**, the location's own path not counted (the owner's call, answering `/speckit-analyze` M1). Names are
compared by a **case-folded key for every letter** (`SharedFileName.Key`: the name upper-cased with the invariant culture;
normalisation form C was dropped when the build's `InvariantGlobalization` turned out to leave non-ASCII text
unnormalised, so a decomposed and a composed spelling are two names), not by SQLite's `NOCASE`, which folds only ASCII; no control characters; none of `/ \ : * ? " < > |`; not `.` or `..`;
no leading or trailing space or dot; not a Windows reserved device name with or without an extension. Checked in
`Parley.Core.SharedFileName` (pure, no I/O) and again by the schema.

**Rationale.** Constitution Principle IV: a name crosses into agents' contexts through lists and notices, so it gets a
bound as part of the design. 255 characters inside the location is the owner's figure; what it means for Windows' path
limit is in R3. The character rules are the union of what Windows, macOS and Linux refuse, so a name valid on the broker's
machine is valid on every machine that fetches it.

## R5. Read-only during a download: enforced where the platform can, detected everywhere

**Decision.** Each download opens the stored file for reading with **no write or delete sharing**
(`FileShare.Read`). On Windows that makes an editor's in-place save fail with a sharing violation and a
save-by-rename fail because the target cannot be replaced — the owner's rule, enforced by the operating system. The broker
also keeps an in-memory count of downloads in progress per file; a replacement, removal or prune waits for it to reach
zero (FR-012). Every download hashes what it streams and compares the result with the recorded hash at the end; a
mismatch fails the fetch and the adapter deletes what it wrote (FR-011).

**Platform difference — to be stated in SPEC.md, not hidden.** Linux and macOS have no mandatory file locks, so an
editor's save during a download **cannot be refused** there. What still holds everywhere is the guarantee the rule exists
for: a download never writes a mixed or partial file, because the end-of-stream hash check catches a change made under it,
and a rename-replace leaves the open descriptor reading the old content whole. The spec's "an editor's save is refused"
is therefore true on Windows and replaced by "the download fails and says the file changed" elsewhere.

**Alternatives rejected.** Copy-on-download snapshots (a 100 MB copy per concurrent download). Advisory locks on POSIX
(editors do not take them, so they would lock nothing).

## R6. The folder watcher, and why it cannot raise duplicate notices

**Decision.** One `FileSystemWatcher` on the location root, subdirectories included, started when the location first
holds a file. Events are debounced per file (500 ms; editors write several times per save), then the file is re-hashed
and its row updated. The same reconcile runs on every list and fetch when a file's size or modification time differs from
the row (FR-017), and over the whole location when the broker starts.

**No double counting.** Change detection compares **hashes**, not events. When a placement moves new content into
place it writes the row's new hash first; the watcher then re-hashes, finds the same hash, and does nothing. Any number of
events for one save produce one hash change and so one set of notices.

**Renames and hand-copied files.** A rename event carries the old and new names, so the placer carries over. **A rename
is written as an update of the row's `name` and `name_key`, never as a delete and an insert**, so `placed_by` and
`placed_utc` stay exactly as they were and no insert-time check is repeated against the placer (`/speckit-analyze` M11). Fetchers of the old name get "gone" notices in the same transaction. A file found
by the start-up scan with no row (renamed or copied while the broker was stopped) is first matched by content hash against
rows whose files went missing from the same folder: exactly one match is a rename and keeps its placer (the owner's call,
answering `/speckit-analyze` M2); none, or several, and it is recorded as placed by the person
(FR-018), and a row whose file is missing is removed with "gone" notices. A file whose name breaks any rule of R4 is
ignored exactly as a wrong extension is, and a rename to such a name counts as the old name gone (`/speckit-analyze`
M14).

**Alternatives rejected.** Polling only (a person's saved correction would not reach agents until somebody listed).
Watcher only (Windows drops events when its buffer overflows; the reconcile on read is what makes a missed event a delay,
not a loss).

## R7. Change notices ride the inbox beside closure polls, and wake nothing

**Decision.** `file_fetches` records (agent, conversation, name, hash received) on each completed fetch, and only for a fetch the broker streamed to that agent
and completed, with the hash it streamed: the broker keeps that pair in memory until it is reported or the agent's
connection ends, and refuses any other report with `FILE_REJECTION_NOT_FETCHED` (`/speckit-analyze` L4). The record is keyed by agent, file and hash, so two concurrent fetches of
one file are each reportable; a refused report leaves the file written, and `parley_file_fetch` says so: *"written, but
Parley will not tell you if it changes; fetch it again to be told"* (`/speckit-analyze` L9). A hash change or
disappearance upserts `file_notices` for every recorded fetcher that is not retired; a change back to the fetched hash
deletes the pending notice (FR-022). `DrainInbox` returns them in a new `file_notices` field of `InboxReply`, next to
`closure_polls`, and deletes them as it hands them over; fetch records stay (FR-023). `Waiting` reports
`file_notices_waiting` so the plugin's waiting hook can mention them. No wake is requested for a notice (FR-021): the code
path never calls `RequestAttentionForMail`.

**Rationale.** SPEC.md §5.0 already delivers something that is not a message through the inbox — the closure poll — for
exactly this reason: §5 admits three message types, and a fourth authored by the broker would make the broker a
participant. A notice is the same kind of thing, so it takes the same route and owes no answer (principle 2.7 is about
messages).

**Bounded collection** (the owner's call, answering `/speckit-analyze` C2). One `DrainInbox` returns at most 200
notices, oldest `changed_utc` first, and deletes only those; `InboxReply.file_notices_waiting` says how many are still
waiting. The inbox `limit` keeps meaning messages only, so an agent limiting its messages is not also holding back
notices. Because notices wake nothing, the remainder is not re-woken the way a limited message drain is (SPEC.md §5.4):
it waits for the agent's next check, and `Waiting`'s `file_notices_waiting` lets the plugin's hook say it is there.
Oldest first, so a burst of new changes cannot keep an older notice waiting behind it.

## R8. Which conversation a placement goes into

**Decision.** `PlaceFile` names a conversation optionally and the broker resolves it with the existing
`ExchangeRepository.AssignConversationAsync(agent, named, senderIsPerson: false)` — the rule that assigns a Request.
A closed conversation is refused with the same refusal. A fetch, list, removal or open names a conversation optionally; a
name alone is searched across the whole project (the owner's call, answering `/speckit-analyze` H3). One match is used;
several make the call do nothing and return `FILE_REJECTION_AMBIGUOUS_NAME` with every matching conversation, so the
agent can repeat the call naming the one it wants.

**Rationale.** SPEC.md principle 2.10: what a line of work produces stays in its conversation, and the broker attaches it
rather than trusting the agent's choice.

**A file can start a conversation** (the owner's call, answering `/speckit-analyze` M4). An agent holding no request that
names no conversation, or a conversation that does not exist, starts one exactly as a Request would. §5.0 derives
membership from exchanges, and a conversation started by a file has none, so the placement also joins the placer through
`ConversationRepository.JoinAsync` — otherwise the agent that started it could neither add a second file by naming it nor
take part in closing it. Such a conversation has a participant and no messages; the surfaces' conversation lists and
transcripts must show it without assuming a first message exists.

## R9. What an agent's adapter may read and write

**Decision.** `Parley.Mcp` reads a file to place and writes a fetched file only inside its **repository root**:
`ServerOptions.Workspace` when the launch sets it, otherwise the process's current directory (a client starts the server
in the repository it serves). Paths are resolved with `Path.GetFullPath` and refused outside the root; a target directory
that is a symbolic link or junction out of the root is refused. A fetch writes to a temporary name in the target
directory, verifies the hash, and only then moves it into place, honouring `replace` (FR-013, FR-014).

**Rationale.** SPEC.md principle 2.5: each agent changes only its own repository. The adapter is the only process on the
agent's machine; the broker never sees the agent's filesystem.

**Identity.** No file operation takes a sender. The adapter sends its launch identity as it does for every call, and the
broker records the placer from the attached agent (`BrokerRuntime.RunAsync`), keeping Constitution Principle III intact.

## R10. Opening a file must not run it

**Superseded in part (owner, 2026-09-14, answering `/speckit-analyze` H2).** The location accepts only `.md` and `.txt`
files (R4), so nothing an agent can place has a handler that executes. The launcher keeps one guard as defence in depth
— it opens only a regular file inside the location whose name ends in `.md` or `.txt`, and refuses anything else with its
path — and no longer needs the per-platform executable lists below, which are kept as the record of why the rule exists.

**Original decision, kept as the record of why the rule exists.** A shared launcher in `Parley.Client` (so the terminal, the web UI and the command line use one
implementation) opens a file with the operating system's registered **edit** handler where one exists and otherwise its
default handler — **except** for types whose default action executes code, which are refused with the file's location:
on Windows every `PATHEXT` extension plus `.lnk`, `.url`, `.scr`, `.msi`, `.msc`, `.hta`, `.jar`, `.ps1`, `.vbs`,
`.wsf`, `.reg`; on Linux `.desktop` and any file with an execute bit; on macOS `.app`, `.command`, `.tool`, `.terminal`
and executables. Folders open with the platform's file browser. The launcher is a seam, replaced in every driven test.

**Rationale.** A placed file is untrusted data from another participant (FR-041). On Windows, "open with the registered
application" is `ShellExecute`, which runs a `.bat` or an `.exe`; one agent could otherwise run code on the person's
machine by placing a file with the right extension and waiting for a person to open it. `Launch.IsWebAddress` in the
terminal already records this exact hazard for URLs; this is the same guard for files. The owner asked for "the
registered editor", and an executable has no editor to open it in.

**Tests never launch anything.** `Shell.Opener` exists because the suite once opened a browser tab on every run; the file
launcher follows the same seam from its first line.

## R11. Where a remote machine is detected

**Decision.** A new operator RPC, `DescribeFileLocation`, returns the broker's `MachineName.Current` and the absolute path
of the location, a conversation's folder or a file. A surface compares that machine name with its own
`MachineName.Current`: equal and the path exists locally, it launches; otherwise it prints "on machine X, at path Y" and
launches nothing (FR-034).

**Rationale.** Machine names are already how Parley identifies machines (`role@machine`, §4.1.1), and a path that does
not exist locally is the same answer by a second route, so a mistaken name match cannot launch the wrong file.

## R12. Agent operations and operator operations stay on different services

**Decision.**

| Operation | Service | Who |
|---|---|---|
| `PlaceFile` (stream), `FetchFile` (stream), `ListFiles`, `RemoveOwnFiles` | `Agents` | the attached agent |
| `ListSharedFiles`, `RemoveFiles`, `PruneFiles`, `DescribeFileLocation` | `Operators` | the person |

**Rationale.** "Agent operations and operator operations are different surfaces" is a boundary that does not move.
Removing any file and pruning are the person's (FR-029, FR-030); an agent's removal is limited to files it placed, decided
by the broker from the row (FR-027, FR-028). Opening is local to a surface and needs only `DescribeFileLocation`.

## R13. Surfaces, doors and parity

**Decision.** `parley files list|remove|prune|open|folder` on the command line; five plugin commands
(`files-list`, `files-remove`, `files-prune`, `files-open`, `files-folder`) that run those commands and carry
`disable-model-invocation: true` like every existing plugin command, so a model cannot invoke them. The terminal gains a
Files dialog (from a project or a conversation) with list, remove, prune, open and open-folder; the web UI gains a Files
page with the same. `SurfaceTests` holds `RemoveFilesAsync` to one file behind a description and a confirmation and
`PruneFilesAsync` to one file behind "Type the project's name", like `PruneAsync`. `DriftTests` already requires both
surfaces to bind every `BrokerClient` operation, so the new ones are held to parity by an existing check.

**Web UI opening.** Per the owner's decision it launches on the machine running the web UI, under R11's rule.

## R14. Placement counts

**Decision.** `file_placements` keeps placements and bytes per agent, updated in the transaction that records a
placement or replacement; the operator's list reply carries them and every surface prints them under the list (FR-039).
No limit is enforced (FR-040).

## R15. Version, schema, and what SPEC.md must say first

**Decision.** Protocol **0.6** (new wire operations and a new inbox field), `VersionPrefix` **0.27.0**, schema **9**.
SPEC.md changes before code, in this order: §1 and §2.2 (what crosses, and why files do not undo the cap), §3 (terms:
file location, shared file, content hash, change notice), §4.2 and §5 (notices are delivered through the inbox and are not
messages), §5.0 (a file's conversation), §9.2–§9.3 (the five tables and their invariants, executed by `Parley.Spec.Tests`),
§10.1 and §10.2 (the new operations), §13 (a file is untrusted data; a hash identifies content and attests nothing about
its author; opening never executes), §14 (destroying a conversation or project removes its files), §16 (file bytes on the
trusted path; removal rests on claimed identity), §18 (0.6). The constitution's "Only question and answer text crosses …
never files" was amended to 2.0.0 on 2026-09-14, before implementation, in its own amendment (see its Sync Impact
Report): the analysis found that changing a principle inside a feature's tasks is not allowed.

## R16. What retention does and does not touch

**Decision.** The message pruner (§9.4) never touches files, fetch records or notices. Notices go when collected; fetch
records go with their file (after its "gone" notices are written) or with their agent on purge (foreign key cascade);
placement counts stay until the project is destroyed. Files go only by removal, prune, destroying the conversation or the
project (FR-031).

## R17. File lists are paged, and notices are bounded per check

**Decision** (the owner's call, answering `/speckit-analyze` C2). `ListFiles` and `ListSharedFiles` take `page_size` and
`page_token` and return `next_page_token`, under the bounds `OperatorsService` already applies to `conversations` and
`instructions`: 50 by default, never more than 200 whatever is asked. Files are ordered by `(conversation_id, name_key)`,
the unique key, and the token resumes after the last key returned, so a file present throughout appears exactly once,
and one placed or removed between pages is shown or not according to where its key falls, never twice. `total_files` and
`total_bytes` count the whole location (or the named conversation) on every page; `totals` carries the conversations on
the page, each counted whole. The command line and the terminal follow every page and show one list; the web UI shows a
page with a link to the next, as its Agents page does. Change notices are bounded at 200 per inbox check (R7).

**Other lists about files** (the owner's call, answering the second `/speckit-analyze` run's C4). An ambiguity refusal
names at most 200 conversations and says how many more hold the name (`FileRefusal.conversations_more`), and a removal
request naming more than 200 files is refused whole before anything is removed (`FILE_REJECTION_TOO_MANY_NAMES`), for
agents and the person alike. `PlaceFileReply.assignable_conversations` repeats protocol 0.5's unbounded list for a
refused Request; bounding it belongs to that operation and is recorded here rather than changed.

**Rationale.** Constitution 2.0.0 Principle IV: what an operation returns about files is text that crosses, and a list or
a batch of notices has a maximum count per call. The agent's list lands in a model's context, and SPEC.md §10.2 already
calls an unbounded operator query rule 8.5's mistake one level up. One page rule for every list in Parley is one rule to
test and one for a person to learn.

**Alternatives rejected.** A smaller page for agents (a second rule with no measurement behind it); a cap of 200 with no
paging (a conversation holding more than 200 files could never be listed whole by an agent); notices following the inbox
`limit` (a small limit meant for messages would hold back notices too); a ceiling of 50 notices (each is a name and two
hashes, so a tighter ceiling mostly costs extra checks after a prune).
