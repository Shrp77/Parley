# Feature Specification: Parley file store

**Feature Branch**: `015-file-store`  
**Created**: 2026-09-14  
**Status**: Draft  
**Input**: User description: "Parley file store: let files cross the boundary between agents. An agent places (uploads) a file to Parley; Parley writes it to a shared location; other agents get (download) the file from that shared Parley location. Parley acts as a file middle ground. Files are only cleaned up when /parley:files-prune is called. /parley:files-list shows the files in the location and their sizes. /parley:files-remove takes a list of file names to remove from the location." Extended the same day: "File-Open (opens the named file(s) in whatever is the registered editor), and File-Folder which opens the folder containing this conversation's files. On cross machine cases, file-folder will only tell the user that the file is on a remote system called X in a folder with path Y." And: "If a file is being downloaded, opened versions are read only … tell the agent if it's changed later. No time limit, the agent chooses if it's relevant to what it's working on."

## Why this is a protocol change

SPEC.md §1 said, until this feature rewrote it, that what crosses between agents is "messages … never files", and §2.2
treats the 8,000-character
message cap as a defence against one agent's context being poured into another's. Integration work runs into that
wall: a class, a schema, a fixture, a log or a design note is real work product that one
repository produces and another needs, and today the only routes are a person copying it by hand or an agent pasting
it into a message the cap refuses.

This feature lets files cross, **without** undoing what the cap defends. A file travels beside the conversation, not
inside it: it is placed in the project's file location under the conversation it belongs to, fetched onto disk in the
receiving agent's own repository, and never delivered into a model's context by Parley. What a message or a notice
carries about a file is its name and its content hash, which are the same small, bounded things the cap was always meant
to permit. SPEC.md now says this in §1 and §2.2 (rewritten 2026-09-14) and must say it in §13, rather than leave the old sentence
standing beside the new behaviour.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - An agent hands a file to another agent (Priority: P1)

`billing` has written the price-snapshot class `orders` needs for the work in conversation `tax-inclusive-pricing`,
and notes on how to use it. Code travels as text, so the class goes in as `PriceSnapshot.cs.txt` and the notes as
`price-snapshot.md`.
`billing` places the file, and it goes into that conversation's files. `billing` sends `orders` a Request that names the
file and its content hash, and `orders` fetches it into its own repository and works with it. Neither agent reads the
other's repository, and no person carries the file between them.

**Why this priority**: This is the whole reason for the feature. Every other story is housekeeping around it.

**Independent Test**: Two agents in one project, on different machines from the broker and from each other, in one
conversation. The first places a file; the second fetches it by name. The fetched file is byte-identical to the placed
one and its content hash matches the one the first agent was given.

**Acceptance Scenarios**:

1. **Given** an agent holding a request in one conversation, **When** it places a file under a name not already used in
   that conversation, **Then** the file is stored in that conversation's files, and the agent is told the conversation,
   the name, the size, the content hash and that it succeeded.
2. **Given** an agent holding requests in several conversations, **When** it places a file without naming a
   conversation, **Then** it is refused, and the refusal lists the conversations it may name — the same rule that
   assigns its Requests.
3. **Given** a file in a conversation's files, **When** any agent in the project fetches it by conversation and name to a
   path in its own repository, **Then** the file written there is byte-identical to the one placed, and the agent is told
   the path, the size, the content hash and who placed it.
4. **Given** a name that exactly one conversation in the project holds, **When** an agent fetches by name alone,
   **Then** that file is fetched, whichever conversation it is in; **given** a name several conversations hold, **When**
   an agent fetches, removes or opens it by name alone, **Then** nothing happens, and the agent is asked which one it
   wants, with the conversations holding that name listed so it can repeat the call naming one.
5. **Given** a name another agent placed in a conversation, **When** an agent places a file under that name in the same
   conversation, **Then** it is refused, the existing file is unchanged, and the refusal names the agent that placed it.
6. **Given** a file fetched by an agent, **When** the agent's turn continues, **Then** the file's contents are not in
   the agent's context unless the agent itself chooses to open the file.
7. **Given** a name no file in the conversation has, **When** an agent fetches it, **Then** it is refused, nothing is
   written, and the refusal says no such file exists.
8. **Given** a path in the fetching agent's repository where a different file already exists, **When** it fetches to
   that path without saying it may replace it, **Then** it is refused and the existing file is unchanged.
9. **Given** a path in the fetching agent's repository that already holds exactly the file's content, **When** it
   fetches to that path, **Then** nothing is rewritten and the agent is told its copy is already current.
10. **Given** a Request naming a file and the content hash its sender meant, **When** the receiver fetches it expecting
    that hash and the file has since changed, **Then** the fetch is refused, nothing is written, and the refusal gives the
    hash the location now holds.
11. **Given** a download of a file in progress, **When** anything tries to write the stored file — an editor saving it,
    the agent that placed it replacing it, a person removing it — **Then** the stored file stays as it is until the
    download finishes: an editor's save is refused where the operating system can refuse it (Windows), and on other
    platforms the download fails saying the file changed and writes nothing; a replacement or removal takes effect only
    once every download in progress has completed.
12. **Given** a file whose name does not end in `.md` or `.txt`, **When** an agent places it, **Then** it is refused,
    nothing is stored, and the refusal says only `.md` and `.txt` files are accepted and that code goes in with `.txt`
    added, such as `PriceSnapshot.cs.txt`.
13. **Given** an agent holding no request, **When** it places a file naming no conversation, or naming one that does not
    exist yet, **Then** a new conversation starts holding the file — under the name given, or one the broker chooses —
    the agent is its participant, and the reply names the conversation so the agent can name it in the Request it sends
    next.

---

### User Story 2 - An agent hears that a file it fetched has changed (Priority: P2)

`orders` fetched `billing`'s `PriceSnapshot.cs.txt` yesterday and moved on to other work. Since then the file was corrected in
place by the person, then replaced by `billing` with a new version. The next time `orders` checks its inbox it finds one
notice: the file in `tax-inclusive-pricing` is not the content it fetched — here is the hash it has, and the hash
there now. `orders` decides whether that matters to what it is doing, and fetches again if it does.

**Why this priority**: A fetched copy silently going stale is the failure a shared file invites. Without a notice an
agent either re-checks every file it ever fetched on every turn, or builds on an old version without knowing.

**Independent Test**: An agent fetches a file and its turn ends. The file is edited twice and then replaced. The agent's
next inbox check holds exactly one notice for that file, carrying the hash it fetched and the current hash; the agent was
not started or woken by any of the changes. The file is then removed; the next check says it is gone.

**Acceptance Scenarios**:

1. **Given** an agent that fetched a file, **When** the file's content changes by any route — edited in its folder,
   replaced by the agent that placed it — **Then** the agent's next inbox check holds a notice naming the conversation,
   the file, the content hash the agent fetched, the content hash now, the size and when it changed.
2. **Given** an agent that fetched a file, **When** the file is removed or pruned, **Then** its next inbox check holds a
   notice saying the file is gone.
3. **Given** a file that changed several times before the agent checked its inbox, **When** it checks, **Then** it finds
   one notice for that file carrying the latest state, not one per change.
4. **Given** a notice waiting for an agent, **When** the change happens, **Then** the agent is not started, woken or
   given a turn because of it; the notice waits for whenever it next checks, however long that is.
5. **Given** a notice delivered to an agent, **When** the agent reads it, **Then** nothing is owed back: it is not a
   message, opens no exchange and needs no reply.
6. **Given** an agent that fetched a file, received a notice and fetched it again, **When** the file changes afterwards,
   **Then** the next notice compares against the content of the second fetch.
7. **Given** an agent that fetched a file and was then retired or purged, **When** the file changes, **Then** no notice
   is kept for it.
8. **Given** a file that changed back to exactly the content an agent fetched before the agent checked its inbox,
   **When** it checks, **Then** no notice is shown for that file.

---

### User Story 3 - An agent or a person sees what is in the location (Priority: P3)

Before fetching, an agent wants to know what is there, how big it is and whether it has changed since it last looked;
the person running the deployment wants to know what is filling the disk. `/parley:files-list` shows every file with its
conversation, size, content hash, who placed it and when, and the totals.

**Why this priority**: An agent cannot fetch what it cannot name, and a location nothing ever cleans up on its own
must be visible to the person who will have to clean it.

**Independent Test**: Place three files of known sizes and content from two agents across two conversations; the list
shows all three under the right conversations with exact sizes, correct content hashes, the right placer and time, and
the correct totals, on every surface.

**Acceptance Scenarios**:

1. **Given** files in the location, **When** the list is asked for, **Then** every file appears exactly once across
   its pages, with its conversation, name, size, content hash, the agent that placed it and when, with a total for each
   conversation on the page and for the whole location.
2. **Given** a conversation, **When** the list is asked for that conversation, **Then** only its files and its total are
   shown.
3. **Given** an empty location, **When** the list is asked for, **Then** it says the location is empty rather than
   showing an empty table.
4. **Given** the same location, **When** the list is asked for from the command line, the terminal and the web UI,
   **Then** all three show the same files, sizes and hashes.
5. **Given** a file that has been replaced or edited, **When** the list is asked for, **Then** it shows the new content
   hash, size and time, so a reader comparing hashes can see the file is different.
6. **Given** 250 files, **When** the list is asked for with no page size, **Then** 50 are returned with a way to ask for
   the next page; asked for 500 at once, at most 200 are returned; following the pages shows all 250, each once, and
   the location's total counts all 250 on every page.

---

### User Story 4 - An agent replaces or takes back a file it placed (Priority: P4)

`billing` revised `PriceSnapshot.cs.txt`. It places the new version under the same name in the same conversation, and the
location now holds the new content with a new content hash, so anyone comparing hashes can see it changed, and every
agent that fetched the old version hears about it. Later it removes a file it no longer needs. It can do neither to a file
another agent placed.

**Why this priority**: A file under a stable name that its owner keeps current is how one repository publishes work
product another depends on. And an agent that can place a file but not replace or take it back has to ask a person to
undo its own work. The rule that stops one agent changing another's file is exactly the rule that makes its own file its
own to change.

**Independent Test**: Two agents each place a file. The first places new content under its own name: the location
holds the new content and a different hash. It places identical content again: nothing changes. It removes its file:
the file is gone. It tries to replace or remove the second agent's file: refused, and that file is unchanged.

**Acceptance Scenarios**:

1. **Given** a file an agent placed, **When** that agent places different content under the same name in the same
   conversation, **Then** the location holds the new content, the list shows the new size, content hash and time, and
   the agent is told the file was replaced along with the previous hash and the new one.
2. **Given** a file an agent placed, **When** that agent places byte-identical content under the same name, **Then**
   nothing is rewritten, the placement time is unchanged, no notices are raised, and the agent is told the file was
   already current.
3. **Given** a file an agent placed, **When** that agent removes it by conversation and name, **Then** the file is gone,
   the name is free to place again in that conversation, and no other file is touched.
4. **Given** a file another agent placed, **When** an agent tries to replace or remove it, **Then** it is refused, the
   file is unchanged, and the refusal names the agent that placed it.
5. **Given** a list of names from one agent, some its own and some not, **When** it removes them, **Then** its own files
   are removed and each of the others is reported as refused, rather than the whole request failing.

---

### User Story 5 - The person removes specific files (Priority: P5)

A file is superseded or large and no longer needed, whichever agent placed it. The person runs `/parley:files-remove`
with one or more names and those files are gone; everything else stays.

**Why this priority**: The selective cleanup for everything agents did not take back themselves, including files
whose placer is retired or gone.

**Independent Test**: Place four files from two agents across two conversations, the person removes two, list: the
other two remain, unchanged.

**Acceptance Scenarios**:

1. **Given** files in the location, **When** the person removes a list of names, **Then** each named file is gone
   whichever agent placed it, and no other file is touched.
2. **Given** a name that exists in more than one conversation, **When** the person removes it without naming a
   conversation, **Then** nothing is removed for that name, and the report lists the conversations holding it.
3. **Given** a list including a name that is not in the location, **When** it is removed, **Then** the names that
   exist are removed, and the report names the ones that did not exist rather than failing the whole request.
4. **Given** a surface that removes files, **When** the person asks it to, **Then** it first says which files and how
   much space they hold, and removes nothing until the person confirms.

---

### User Story 6 - The person empties the location (Priority: P6)

The work that needed the files is finished. The person runs `/parley:files-prune` and the location is empty.

**Why this priority**: The one cleanup that removes everything, and it is deliberately not automatic: nothing removes
a file on a schedule or because a conversation ended.

**Independent Test**: Place files in several conversations, prune, list: the location is empty and its disk space is
given back; no conversation, message or other deployment state changed.

**Acceptance Scenarios**:

1. **Given** files in any number of conversations, **When** the person prunes, **Then** every file in every
   conversation is removed and the report says how many files and how much space were freed.
2. **Given** a prune, **When** it has run, **Then** conversations, messages, exchanges and agents are exactly as they
   were before it.
3. **Given** a surface that prunes, **When** the person asks it to, **Then** it first says how many files and how
   much space will be removed, and removes nothing until the person confirms with a deliberate act.
4. **Given** time passing and conversations closing, **When** nobody prunes or removes, **Then** every file stays.

---

### User Story 7 - The person opens a file (Priority: P7)

The person wants to read or correct a file an agent placed — a schema with a typo in it, a log they need to search. They
run `/parley:files-open` with one or more names and each file opens in whatever editor their system has registered for
that kind of file. The editor opens the stored file itself, in the shared location, so a correction the person saves is
the shared file's new content: its size, content hash and time update, every agent that fetched it hears so, and the
next agent to fetch it gets the correction.

**Why this priority**: Without it the person can see that a file exists but has to find it on disk by hand to read it,
and fixing a file means asking the agent that placed it to place it again.

**Independent Test**: On the broker's machine, place a file, have an agent fetch it, open it, change and save it: the
list shows a new content hash and size, the placer is unchanged, the fetching agent's next inbox check holds a notice,
and a new fetch returns the edited content. From a different machine, open it: the command reports the machine and path
instead.

**Acceptance Scenarios**:

1. **Given** files in the location and a person on the machine that holds it, **When** they open one or more names,
   **Then** each file opens in the editor the system has registered for its type.
2. **Given** a file opened in an editor, **When** the person saves a change, **Then** the location records the new
   size, content hash and time, the agent that placed the file stays its owner, agents that fetched it get a notice, and
   later fetches return the new content.
3. **Given** a file open in an editor while an agent is downloading it, **When** the person saves, **Then** on Windows
   the save is refused because the stored file is read-only until the download finishes, and a save after it finishes
   succeeds; on Linux and macOS the save succeeds, the download fails saying the file changed and writes nothing, and the
   agent may fetch again.
4. **Given** a person on a different machine from the one holding the location, **When** they open a name, **Then**
   nothing is launched, and they are told the file is on the machine called X at the path Y.
5. **Given** a file type with no registered editor, **When** it is opened, **Then** the person is told no editor is
   registered for it and where the file is.
6. **Given** a name that exists in more than one conversation, **When** it is opened without naming a conversation,
   **Then** nothing opens and the person is told which conversations hold it.
7. **Given** the terminal or the web UI, **When** the person opens a file there, **Then** it behaves exactly as the
   command does on the machine that surface is running on.

---

### User Story 8 - The person opens a conversation's files folder (Priority: P8)

The person wants to see a conversation's files the way their system shows any folder. They run `/parley:files-folder`
and the folder holding that conversation's files opens in their system's file browser. On a different machine from the
one holding the location, nothing opens: they are told the files are on the machine called X, in the folder at path Y.

**Why this priority**: The quickest way to see, sort and preview a conversation's files is the tool the person already
uses for every other folder; and when the files are elsewhere, knowing exactly where is the useful answer.

**Independent Test**: On the broker's machine, run it for a conversation with files: that conversation's folder opens.
Run it from another machine: it reports the machine name and the folder path, and launches nothing.

**Acceptance Scenarios**:

1. **Given** a conversation with files and a person on the machine that holds the location, **When** they open its
   folder, **Then** the system's file browser opens on that conversation's folder.
2. **Given** a person on a different machine, **When** they open a conversation's folder, **Then** nothing is launched,
   and they are told the files are on the machine called X, in the folder at path Y.
3. **Given** no conversation named on the command line, **When** the folder is opened, **Then** the project's file
   location opens, showing one folder per conversation; the terminal and web UI open the folder of the conversation
   being viewed, if any.
4. **Given** a conversation with no files, **When** its folder is opened, **Then** the person is told it has no files
   rather than being shown a folder that does not exist.
5. **Given** a file changed or deleted by the person in that folder, **When** the location is next listed or fetched
   from, **Then** the change is reflected — an edited file has its new size, hash and time, a deleted file is gone — and
   agents that fetched it get a notice.

---

### Edge Cases

- **An upload that stops part way** (the agent's process ends, the connection drops, the broker restarts): no partial
  file ever appears in the list or can be fetched. A new name stays free; a replacement leaves the previous content, its
  hash and its time exactly as they were.
- **Two agents placing the same new name in one conversation at the same moment**: exactly one succeeds; the other is
  refused as placed by the winner.
- **The same name in two conversations**: two independent files with their own owners, hashes, times and notices.
- **Two names differing only in case in one conversation** (`Readme.md`, `README.md`, `Übersicht.md`,
  `übersicht.md`): the same name; the second placement is a replacement by its placer or a refusal naming the placer.
- **A name that, with its conversation's folder, would be longer than 255 characters**: refused, saying how many
  characters that conversation leaves for a name.
- **The same agent replacing one name twice at the same moment**: the location ends holding one of the two contents
  whole, and the hash shown is that content's hash; never a mix.
- **Several downloads of one file at once**: all read the same content; the file stays read-only until the last one
  finishes.
- **A download that never finishes** (the agent's connection is lost mid-download): the file is read-only only while the
  download is actually in progress; a download whose agent has gone stops counting, so an abandoned download does not
  hold the file read-only for ever.
- **A download that starts while an editor is part way through saving**: the download waits for the save to finish and
  reads one consistent content, or fails saying the file is being changed; it never delivers a half-saved file.
- **A replacement, removal or prune asked for while a download is in progress**: it takes effect once the download
  finishes; the downloading agent gets the content it started on, then a notice that it changed or is gone.
- **A fetched file whose written bytes do not match the content hash**: treated as a failed fetch; nothing is left at
  the target path, and nothing is recorded as fetched.
- **Edits or deletions made in the folder while the broker is not running**: noticed the next time the location is
  listed or fetched from, reflected as above, and notices raised for agents that fetched the file.
- **A file copied into a conversation's folder by hand rather than placed**: if its name meets every name rule, including ending in `.md` or `.txt`, taken into the
  location as placed by the person running the deployment, so agents can fetch it but not replace or remove it. A file
  whose name breaks any rule (another extension, a reserved device name, a forbidden character, too long for its folder)
  is ignored: never listed, never fetchable, never opened.
- **A file renamed in the folder**: the old name is gone and the new name appears, still owned by the agent that placed
  it; agents that fetched it under the old name are told the old name is gone. A rename to a name that breaks any name rule, such as
  an extension other than `.md` or `.txt`, takes the file out of the location, and agents that fetched it are told it is gone. A rename made
  while the broker was not running is recognised at start-up when the new file's content hash matches exactly one file
  that went missing from the same folder: it keeps its placer. Otherwise the new file is recorded as placed by the person.
- **A folder watcher that misses a change**: the next list or fetch re-checks the file, so a missed notification delays
  the new hash and the notices, never loses them.
- **A save that leaves the content exactly as it was**: the hash is unchanged, so no notice is raised.
- **An agent that has fetched hundreds of files, many of which changed**: one notice per changed file, collected at most
  200 per inbox check, oldest change first; each reply says how many are still waiting, and those wait for the agent's
  next check. No turn is spent on any of them until the agent chooses.
- **An agent claiming to have placed a file it did not**: replacement and removal are decided by who the location
  recorded as the placer, not by anything the requesting agent says.
- **An uploader supplying its own content hash**: not trusted; the hash the location records and reports is computed
  from the bytes actually stored.
- **A conversation that holds only files** (started by a placement): it has a participant and no messages; it
  appears in every surface's conversation list, its transcript says it has no messages and lists its files, a Request
  naming it continues it, and it closes like any other.
- **Placing a file into a closed conversation**: refused, as a Request naming it is; files already there stay until
  removed or pruned.
- **A conversation whose identifier cannot be a folder name as it is** (`con`, `pricing.`, `.pricing`, or `Pricing`
  beside an existing `pricing`): its folder is the identifier with `~2`, `~3` … added, fixed when the folder is first
  created, and `files-folder` and every message about the location name the folder actually used.
- **A conversation destroyed by the person**: its files folder goes with it, and agents that fetched its files are told
  they are gone.
- **The broker's disk fills during an upload**: the upload is refused, nothing partial remains, a replaced file keeps its
  previous content, and the person can see why.
- **A name that tries to escape its folder** (a path separator, `..`, a drive or device name): refused when placed, so
  nothing is ever written outside the conversation's folder.
- **A fetch path outside the fetching agent's own repository**: refused; a file is only ever written inside the
  repository of the agent fetching it.
- **An agent that placed a file is later retired**: its files stay, still show who placed them, can no longer be
  replaced by any agent, and only the person can remove them.
- **An agent that placed a file is later purged**: purge deletes every conversation the agent took part in, as it always
  has, and placing a file made it a participant, so its files go with those conversations — their bytes are deleted,
  purge's report counts them, and agents that fetched them are told they are gone (the owner's call).
- **A project is destroyed**: its file location goes with its store.
- **A zero-byte file**: can be placed, listed with size zero and the hash of empty content, fetched, replaced, opened and
  removed.
- **A file whose contents look like instructions**: it is data; nothing in Parley reads, interprets or delivers it
  into a model's context.

## Requirements *(mandatory)*

### Functional Requirements

**The location**

- **FR-001**: Each project MUST have exactly one file location, divided into one folder per conversation that has files,
  shared by every agent in that project and by nobody outside it.
- **FR-002**: Files MUST travel through the broker, so that an agent on any machine that can reach the project's broker
  can place and fetch files without any other shared storage.
- **FR-003**: Every file MUST belong to exactly one conversation. A name MUST be unique within its conversation, comparing every letter without regard to case (the
  owner's call: `Readme.md` and `README.md` are one file, for accented and non-Latin letters as much as for A–Z); the same
  name in two conversations MUST be two independent files.
- **FR-004**: A name MUST be a single flat file name ending in `.md` or `.txt`, compared without regard to case. A name
  with any other extension, a path separator, a parent reference, or a reserved device or drive name MUST be refused, and
  a refused extension MUST be answered with how to place it (add `.txt`, such as `PriceSnapshot.cs.txt`). A file's path
  inside the location — its conversation's folder, a separator and its name — MUST be at most 255 characters; the
  location's own path is not counted (the owner's call). A name that would exceed it MUST be refused, saying how many
  characters its conversation leaves for a name.
- **FR-005**: Every file MUST have a SHA-256 content hash of its current stored content, computed by the broker, never
  taken from the placing agent.

**Placing, replacing and fetching**

- **FR-006**: An agent MUST be able to place a file from its own repository into a conversation's files. The
  conversation MUST be decided by the same rule that decides a Request's conversation (SPEC.md §5.0); placing into a
  closed conversation MUST be refused. An agent holding no request that names no conversation, or names one that does not
  exist, MUST start a new conversation holding the file, exactly as a Request would (the owner's call, answering
  `/speckit-analyze` M4). Placing a file MUST make the placer a participant of the file's conversation.
- **FR-007**: An agent placing under a name it placed itself in that conversation MUST replace that file's content. If
  the new content's hash differs, the location MUST hold the new content, size, hash and time; if it is identical,
  nothing MUST change and the agent MUST be told the file was already current.
- **FR-008**: An agent placing under a name another agent placed in that conversation MUST be refused, leaving the
  existing file unchanged and naming the agent that placed it.
- **FR-009**: A new file MUST NOT become visible or fetchable, and a replacement MUST NOT take effect, until the new
  content has been received completely; an interrupted placement MUST leave the location exactly as it was.
- **FR-010**: Any agent in the project MUST be able to fetch any file by conversation and name, to a path inside its own
  repository. A name alone MUST be searched for across the whole project (the owner's call): found in exactly
  one conversation, it MUST be used; found in several, the call MUST do nothing and MUST ask which one is wanted, listing
  the conversations holding the name so the caller can repeat it naming one. The same rule applies to removing and
  opening by name. At most 200 conversations MUST be listed, with how many more hold the name (constitution
  Principle IV).
- **FR-011**: A fetch MUST write a byte-identical copy of one whole, consistent content of the file, MUST check what it
  wrote against that content's hash, and MUST leave nothing at the target path if it cannot complete or the check fails.
- **FR-012**: While any download of a file is in progress, the stored file MUST be held read-only. Where the operating
  system can refuse another process's write (Windows), a write by an editor or any other process MUST be refused; where it
  cannot (Linux, macOS), a download during which the stored content changes MUST fail with a clear "the file changed"
  refusal and write nothing. On every platform a replacement, removal or prune through Parley MUST take effect only after
  every download in progress has finished, and a download whose agent is no longer connected MUST stop holding the file.
- **FR-013**: A fetch MAY state the content hash the agent expects; if the location holds different content, the fetch
  MUST be refused, write nothing, and report the hash the location holds.
- **FR-014**: A fetch MUST NOT replace a different file already at the target path unless the agent explicitly asks to
  replace it; if the file already there has the same content hash, the fetch MUST succeed without rewriting it.
- **FR-015**: Placing, fetching, replacing, removing and change notices MUST give an agent only conversations, file names,
  sizes, content hashes, paths, who placed each file and when; Parley MUST NOT deliver a file's contents into an agent's
  context.

**Changes and the agents that fetched them**

- **FR-016**: While the location holds files, the broker MUST watch it, and any write, update, rename or deletion of a
  file there MUST be reflected: a changed file's size, content hash and time recalculated, a renamed file listed under its
  new name, a deleted file removed from the location.
- **FR-017**: Before listing a file or serving a fetch, the broker MUST confirm the recorded size and time still match
  the stored file and recalculate the hash if they do not, so a change the watcher missed, or made while the broker was not
  running, is never served under a stale hash and never goes unnoticed.
- **FR-018**: A change made outside Parley MUST NOT change who placed a file. A file that appears in a conversation's
  folder without being placed MUST be recorded as placed by the person running the deployment — unless, when the broker
  starts, its content hash matches exactly one file that went missing from the same folder, in which case it MUST be
  treated as that file renamed and keep its placer (the owner's call, answering `/speckit-analyze` M2).
- **FR-019**: The broker MUST record each completed fetch: which agent fetched which file, and the content hash it
  received. It MUST refuse a report of a fetch it did not stream to that agent, or of a hash other than the content it
  streamed. A later fetch of the same file by the same agent MUST replace that record.
- **FR-020**: When a file's content hash changes by any route, or the file is removed, pruned, renamed away or destroyed
  with its conversation, every agent with a fetch record for it that is not retired or purged MUST have a change notice
  waiting in its inbox, naming the conversation, the file, the hash it fetched, and the current hash, size and time or
  that the file is gone.
- **FR-021**: Change notices MUST be delivered on the agent's next inbox check, MUST NOT start, wake or give a turn to
  any agent, and MUST NOT expire. One inbox check MUST return at most 200 notices, oldest change first, whatever limit
  the agent gives for messages, and MUST say how many are still waiting; those are delivered on later checks. (What
  crosses about files is bounded per call: constitution Principle IV.)
- **FR-022**: An agent MUST have at most one change notice per file waiting, carrying the file's latest state; a change
  that returns a file to exactly the content the agent fetched MUST withdraw that agent's waiting notice.
- **FR-023**: A change notice MUST NOT be a message: it opens no exchange, belongs to no pair's traffic, and owes no
  reply. Once collected it is gone, and the agent's fetch record stays, so a further change raises a further notice.

**Listing and removing**

- **FR-024**: The list MUST show every file with its conversation, name, size, content hash, the agent that placed it
  and when, with a total per conversation and for the location, and MUST be available for one conversation or for all.
  It MUST be returned in pages, as the operator's `conversations` and `instructions` lists are: 50 files by default and never more
  than 200 whatever is asked, with a way to ask for the next page, every file appearing once across the pages. The
  total for the location (or the named conversation) MUST count every file whatever the page; a page MUST give the
  totals of the conversations it shows.
- **FR-025**: Agents and the person running the deployment MUST both be able to list the location.
- **FR-026**: An agent MUST be able to remove, by conversation and name, files it placed itself. One removal request MUST
  name at most 200 files; a request naming more MUST be refused whole, removing nothing.
- **FR-027**: An agent MUST NOT be able to replace or remove a file another agent placed; each such name MUST be refused,
  naming the agent that placed it, without preventing the agent's own files in the same request. (No agent has
  authority over another's work.)
- **FR-028**: Which agent placed a file MUST be recorded by the broker when the file is first placed, and replacement and
  removal by an agent MUST be decided from that record. Replacing a file MUST NOT change who placed it (for changes made
  outside Parley, FR-018).
- **FR-029**: The person running the deployment MUST be able to remove any files by name, whichever agent placed them; a
  name held by several conversations MUST be qualified by conversation, and names not present MUST be reported without
  preventing removal of the others. The same limit of 200 names per request applies.
- **FR-030**: The person running the deployment MUST be able to prune the location, removing every file in every
  conversation. Pruning MUST NOT be available to agents, because it removes files other agents placed.
- **FR-031**: Files MUST be removed only by an agent removing its own file, by the person removing, pruning or deleting
  files, by destroying their conversation or purging an agent that took part in it, or by destroying the project; nothing MUST remove a file on a schedule, by age,
  when a conversation closes, or when message retention runs. Replacement MUST NOT keep the previous content.

**Opening a file and a folder**

- **FR-032**: The person MUST be able to open one or more files by name, each in the editor the operating system has
  registered for its type, and the editor MUST open the stored file in the shared location itself. Opening MUST launch
  nothing for a file whose name does not end in `.md` or `.txt`, so opening never runs a program another participant
  placed.
- **FR-033**: The person MUST be able to open the folder holding one conversation's files in the operating system's file
  browser; with no conversation named on the command line, the project's file location MUST open.
- **FR-034**: When opening a file or a folder is asked for on a machine other than the one holding the location, nothing
  MUST be launched, and the person MUST be told the name of the machine holding it and the full path of the file or
  folder there.
- **FR-035**: When no editor is registered for a file's type, the person MUST be told so, with where the file is.
- **FR-036**: Opening files and folders MUST be operations of the person running the deployment, not of agents.

**Surfaces, counting and the protocol**

- **FR-037**: Listing, removing, pruning, opening a file and opening a folder MUST be available to the person from the
  command line, the terminal and the web UI, and through `/parley:files-list`, `/parley:files-remove`,
  `/parley:files-prune`, `/parley:files-open` and `/parley:files-folder`. The terminal and the web UI MUST open files and
  folders on the machine they run on, under the same rule as the command line.
- **FR-038**: Every operator surface that removes or prunes MUST describe what will be removed (file count and size)
  before acting and MUST require a deliberate confirmation.
- **FR-039**: Parley MUST count placements, including replacements, (how many and how many bytes, by agent) and make
  those counts visible to the person running the deployment, so that a size limit can later be set from evidence.
- **FR-040**: There MUST be no limit on file size or location size in this version.
- **FR-041**: A file's contents MUST be treated as untrusted data from another participant: nothing in Parley may
  read, interpret or act on them beyond computing its content hash. (What an agent is given about a file is FR-015's.)
- **FR-042**: The protocol version MUST change, and SPEC.md MUST describe the file location, its operations, the content
  hash, changes made outside Parley, change notices, and why files crossing no longer contradicts §1, §2.2 and §13.

### Key Entities *(include if feature involves data)*

- **File location**: the one shared place per project where files live, on the machine running the project's broker.
  Holds one folder per conversation that has files, and goes when the project's store is destroyed.
- **Conversation files**: a conversation's folder in the location. Goes when its conversation is destroyed; stays when it
  closes. Its folder name is fixed when first created and kept for as long as the conversation exists, even while it
  holds no files.
- **Shared file**: a file in a conversation's files. Has a name (unique in its conversation), its current content, a
  size, a SHA-256 content hash, a name ending in `.md` or `.txt`, the participant that placed it (which decides whether an agent may replace or remove
  it), and when its current content was placed or last changed. Its contents are opaque to Parley.
- **Content hash**: the SHA-256 of a file's current stored content, computed by the broker, recalculated whenever the
  content changes by any route. The same hash means the same content; a different hash means the file is different.
- **Fetch record**: that one agent fetched one file, and the content hash it received. What decides who is told about a
  later change, and what the change is compared against.
- **Change notice**: waiting in one agent's inbox, about one file it fetched: the hash it has, and the file's current hash,
  size and time, or that the file is gone. Not a message; at most one per agent per file.
- **Placement count**: how many placements and bytes each agent has made, kept so a limit can be set from what actually
  happened.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: An agent can hand a file to another agent in a different repository, on a different machine, with one
  place action and one fetch action, and no person involved.
- **SC-002**: A 100 MB file placed and fetched is byte-identical at the far end, with matching content hashes, in 100% of
  attempts.
- **SC-003**: The list's conversations, sizes, hashes and totals match the files actually stored exactly, on all three
  operator surfaces.
- **SC-004**: When a file's content changes by any route — replaced by its agent, or edited or overwritten in its folder
  — 100% of later lists and fetches show a content hash different from the previous one; when identical content is
  placed or saved, 100% show the hash unchanged.
- **SC-005**: 100% of agents that fetched a file and are not retired find a change notice for it on their next inbox check
  after it changes or goes, whenever that check is, with no more than one notice per file — or, when more than 200 are
  waiting, on the later checks the replies say are needed, with none lost.
- **SC-006**: No change to any file starts, wakes or gives a turn to any agent.
- **SC-007**: A receiver fetching a file expecting the hash its sender named never ends up with different content
  without being told.
- **SC-008**: An agent can replace or take back a file it placed in one action with no person involved, and 100% of
  attempts by an agent to replace or remove a file another agent placed are refused with the file unchanged.
- **SC-009**: 100% of downloads complete with the content they started on, matching its hash, while the file is being
  saved, replaced or removed at the same time; and an abandoned download never leaves a file read-only once its agent
  has gone.
- **SC-010**: A person on the broker's machine can open a named file in its registered editor, or a conversation's
  folder in the file browser, in one action; a person on any other machine is told the machine name and full path in one
  action, with nothing launched.
- **SC-011**: A change saved to an opened file is reflected in the list's hash and size by, at the latest, the next list
  or fetch, in 100% of cases, with the file's owner unchanged.
- **SC-012**: After a prune, the list shows zero files and the location's disk space is freed, while every
  conversation, message and agent is unchanged apart from the change notices raised.
- **SC-013**: Across the feature's tests, no file content appears in anything Parley delivers into an agent's context,
  and no fetch writes a partial or mixed file.
- **SC-014**: An upload interrupted at any point leaves the location exactly as it was: no partial new file, and no
  partially replaced one.
- **SC-015**: Every removal or prune by the person is preceded by a description of what will go and a deliberate
  confirmation on every surface that offers it.
- **SC-016**: The person can see, per agent, how many placements and bytes have been made, without reading any file.
- **SC-017**: No list reply carries more than 200 files, no inbox check more than 200 change notices, no refusal more
  than 200 conversations and no removal request more than 200 names, whatever the caller asks for, while following the pages or the checks still reaches every file and every notice.

## Assumptions

- **Only `.md` and `.txt` files are accepted** (the owner's call, 2026-09-14, answering `/speckit-analyze` H2). Code
  goes in with `.txt` added — `PriceSnapshot.cs.txt` — and the receiving agent names its own copy as it likes. This keeps
  every file in the location something a text editor opens, so no placed file has a handler that runs a program, and it
  keeps the store to the documents and code that cross-repository work actually hands over. **Only the name is checked**
  (the owner's call): a file's bytes are not inspected, so a binary renamed to `.txt` is accepted — and still opens in a
  text editor and runs nothing, which is what the rule protects.
- **Placing reads from, and fetching writes to, the agent's own repository.** This keeps §2.5's rule that an agent
  changes only its own repository; a file is written into the receiver's repository by the receiver.
- **Files are grouped by conversation** (the owner's call, 2026-09-14), so a conversation's files are the folder
  `files-folder` opens. Every agent in the project can still see and fetch any conversation's files; grouping organises
  files, it does not hide them.
- **A file's conversation is assigned like a Request's.** An agent holding requests from one conversation places into
  it, one holding several names which, one holding none names a conversation it is part of or starts a new one — a file
  can start a conversation just as a Request can (the owner's call, M4), and the placer becomes its participant. This keeps SPEC.md principle
  2.10: everything a line of work produces stays in its conversation.
- **Agents learn about a new file the ordinary way:** a Request or Result names it, ideally with its content hash.
  Placing a file sends no message and wakes nobody, because the protocol already has the message that says "here is
  something for you".
- **Agents learn about a changed file through change notices** (the owner's call, 2026-09-14). Every agent that fetched a
  file is told when it later changes or goes, with no time limit, and decides for itself whether that matters to what it
  is working on. The notice waits for the agent's next inbox check and wakes nothing, so a file saved often costs no
  turns.
- **"Active" agents means agents not retired or purged.** An agent that is simply not running still gets its notices
  when it next checks its inbox; that is what "next time it checks its inbox" means for an agent that was stopped.
- **A change notice is not a message.** Principle 2.7 says every message is answered; a notice, like a closure poll
  (SPEC.md §5.0), is something the broker surfaces through the inbox rather than something a participant sent, so there is
  nobody to answer and nothing to answer them with.
- **Whether a file is open in an editor is not tracked** (the owner's call, 2026-09-14). Parley can see when it launches
  an editor but not when the person closes it, so nothing depends on knowing. Every change, from any source, is caught by
  the folder watcher and reaches every agent that fetched the file.
- **A download makes the stored file read-only for as long as it runs** (the owner's call, 2026-09-14), so a download
  always delivers one whole content. An editor that tries to save meanwhile reports the save failed in its own way;
  Parley cannot speak through the editor, and the person saves again when the download finishes. **Only Windows can
  refuse another process's write**; Linux and macOS have no mandatory file locks. There the guarantee the rule exists for
  still holds — no agent ever receives a half-saved or mixed file — because every download is checked against the
  content hash and fails if the file changed under it (research.md R5, found by `/speckit-analyze` as H1).
- **Replacing a file under its name is legal** (the owner's call, 2026-09-14), and the SHA-256 content hash is how a
  change is seen. A different hash means different content; the same hash means nothing changed.
- **Only the agent that placed a file replaces it** (confirmed by the owner, 2026-09-14). Placement decides removal
  because no agent has authority over another's work, and replacing another agent's file changes its work in the same
  way. "The agent that placed it" is the owner's confirmed wording, used rather than *originator*, which SPEC.md §3
  already defines as the participant whose request began a line of work.
- **The person may remove or change any file as they see fit** (the owner's call, 2026-09-14): by removing, pruning,
  or editing and deleting in the folder. The person running the deployment sees everything and can do everything
  (SPEC.md principle 2.9).
- **`files-open` opens the stored file in place** (the owner's call, 2026-09-14), and the folder watcher is what keeps
  the hash true afterwards. Changes made outside Parley are detected and re-hashed rather than forbidden (the owner's
  call).
- **`files-open` from another machine reports the machine and path**, as the owner specified for `files-folder`,
  because the stored file is not on that machine to open. It does not fetch a copy: a copy would not be the shared file,
  and an edit to it would change nothing.
- **"The registered editor" is the operating system's default application for the file's type**, and "the file
  browser" is the operating system's folder view.
- **The terminal and the web UI open files and folders on the machine they run on** (the owner's call, 2026-09-14),
  under the same remote rule as the command line. A web UI viewed from another machine's browser therefore opens them
  where the web UI runs, or reports the location if that is not the broker's machine.
- **Opening is the person's.** Agents fetch copies into their own repositories; they never launch editors or file
  browsers, and most run with no one at the screen.
- **A file that appears in a folder by hand belongs to the person.** Recording it as any agent's would let that agent
  replace or remove something it never placed.
- **The location keeps current content only.** A replacement or an edit discards the previous content; there is no
  history or versioning. Two versions needed at once get two names.
- **An agent's removal or replacement needs no confirmation step.** The confirmation on operator surfaces exists because
  a person is about to delete files others placed; an agent changing its own file is acting on its own work.
- **SHA-256** because the owner asked for a SHA and it is the common, unbroken choice; the hash identifies content, it is
  not a signature and says nothing about who wrote the file.
- **No size limit yet.** The owner chose to measure first, as rule 8.5's cap was eventually set from `parley refusals`.
  Placements are counted for that reason.
- **Only removal, prune and destruction clean up.** A location that grows until pruned is the owner's explicit choice;
  the list's totals are how the person sees it growing.
- **Transport is the broker's existing connection**; no additional shared storage, network share or service is required
  for agents. The broker stores the location beside the project's store.
- **Out of scope for this version:** any extension other than `.md` and `.txt` (binaries, archives, executables),
  per-recipient visibility, automatic expiry, size limits, keeping previous
  versions, transferring ownership of a file, moving a file between conversations, tracking whether a file is open in an
  editor, notices that wake an agent, and previews of file contents inside Parley's own surfaces.
- **Depends on** SPEC.md being changed first (the document is the primary artifact): §1 and §2.2 on what crosses
  between agents, §4.2 and §10.1 for change notices collected through the inbox, §5 for why a notice is not a message,
  §5.0 for a file's conversation, §9 for how the location, fetch records and notices are stored and the folder watched,
  §10.1 and §10.2 for the new agent and operator operations, §13 for how a fetched file is treated and what its content
  hash does and does not attest, §14 for what destroying a conversation or project removes, §16 for the trusted-network
  assumption now covering file bytes and claimed identity deciding replacement and removal, and §18 for the new protocol
  version.

## Amendment, 2026-09-16: names are a set, and a placement is renamed

The owner's call, taken after the review gate had recorded — and left undecided — that a file's name reached another
agent's model as up to two hundred and fifty characters of the placer's choosing. It supersedes FR-003's clause about
accented and non-Latin letters, and FR-004's list of refused characters, and is carried by SPEC.md §9.5 and migration
010 (schema 10, version 0.28.0, protocol still 0.6 because 0.6 was never released).

- **A name is made of `A`–`Z`, `a`–`z`, `0`–`9`, `.`, `_` and `-`.** Every other rule stands: `.md` or `.txt`, no
  leading dot, no reserved device name, 255 characters with the folder. Because every name is ASCII, the key is the
  name in upper case and SQLite computes the same one, so the combining-mark caveat is gone.
- **A placement asking for any other character is renamed**, each such character becoming `_`, one for one. The reply
  carries the name placed and the name asked for. The broker renames; the adapter shows the spelling first.
- **A renamed placement whose spelling is already a file's name is refused**, whoever placed that file, with the
  sentence the owner specified: the name asked for has illegal characters and will be named the safe spelling, which
  collides with the existing file of that name, named as it is stored. Placing under the safe spelling replaces a
  caller's own file on purpose.
- **A name looked up is never renamed.** A fetch or removal by a name outside the set is refused, and told the spelling a
  placement of it would have had.
- **A file a person copies in under any other name stays ignored** (the owner's call), and the person's listing counts
  such files without naming them, on every surface.
- **A store at schema 9** keeps every row whose name is in the set. A row under any other name is dropped, its file left
  on disk, and each active agent that fetched it is told it is gone.

Found while building it: a placement replaced a file the person had put on disk before the watcher recorded it,
because only the watcher had ever looked. A placement now asks the disk too, and refuses.
