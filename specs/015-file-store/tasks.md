---

description: "Task list for the Parley file store (015)"
---

# Tasks: Parley file store

**Input**: Design documents from `specs/015-file-store/`
**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/wire.md, contracts/commands.md, quickstart.md

**Tests**: Included. The spec does not ask for TDD by name, but this repository's constitution makes tests part of every
change: "the DDL carrying it is worth exactly what its test is worth" (Principle V) and "Probe; do not reason" (Quality
gates). A test that pins a guard is written, run and seen to **fail** against the unguarded code before the guard is
trusted.

**Organization**: One phase per user story in spec.md priority order, after a setup phase that changes the specification
first (Constitution Principle I) and a foundational phase every story needs.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (different files, no dependency on an unfinished task)
- **[Story]**: US1–US8, the user stories of spec.md
- Paths are from the repository root. Test executables run directly:
  `tests/Parley.X.Tests/bin/Debug/net10.0/Parley.X.Tests.exe` (CLAUDE.md, "Zero tests ran").

---

## Phase 1: Setup — the specification leads

**Purpose**: SPEC.md is the primary artifact; the code follows it (research.md R15).

- [x] T001 (done 2026-09-14, with the constitution amendment) Rewrite SPEC.md §1 and §2.2 so files crossing between agents is stated and argued: bytes land on disk in the fetching agent's repository, never in a context; what a message or notice carries is a name and a hash, within the cap's rationale — `SPEC.md`
- [X] T002 Add the terms *file location*, *shared file*, *content hash* and *change notice* to the §3 terminology table — `SPEC.md`
- [X] T003 Specify change notices in §4.2 and §5/§5.0: delivered through `inbox()` beside closure polls, not a message, owe no reply, wake nothing, at most 200 per `inbox()` call, oldest first, with the count still waiting; a file's conversation assigned as a Request's, a placement by an agent holding no request starting a conversation and making the placer a participant — `SPEC.md`
- [X] T004 Add schema 9 to §9.2 (the five tables and triggers of data-model.md) and its invariants to §9.3, written as executable SQL fragments — `SPEC.md`
- [X] T005 Add the agent operations (`file_place`, `file_fetch` — which carries the adapter's report of a verified fetch — `file_list`, `file_remove`; named as their tools are, less the `parley_` prefix, which `SpecifiedOperationTests` requires) to §10.1 and the operator operations (`list_shared_files`, `remove_files`, `prune_files`, `describe_file_location`) to §10.2, with the read-only hold during a fetch and its platform difference (research.md R5), and both file lists paged under the bound §10.2 already gives `conversations` (research.md R17) — `SPEC.md`
- [X] T006 Extend §13 (a file is untrusted data; SHA-256 identifies content and attests nothing about its author; opening never executes a file — research.md R10), §14 (destroying a conversation or project removes its files), §16 (file bytes on the trusted path; removal and replacement rest on claimed identity) — `SPEC.md`
- [X] T007 Add the `0.6 — files cross the boundary` entry to §18 with what a second implementation will observe, and set the header version to 0.6 — `SPEC.md`
- [X] T008 Run `tests/Parley.Spec.Tests` and fix every reference, table and schema fragment it reports — `SPEC.md`
- [X] T009 [P] Confirm the constitution is at 2.0.0 and its boundary reads "What crosses between agents is messages and placed files, never contexts" (amended 2026-09-14 by `/speckit-constitution`); no edit expected, and any difference from SPEC.md as rewritten by T001–T007 is fixed in SPEC.md's favour — `.specify/memory/constitution.md`
- [X] T010 [P] Bump `VersionPrefix` to 0.27.0 — `Directory.Build.props`
- [X] T011 [P] Set `ProtocolVersion.Current` to "0.6" and update the theory data in `AdversarialTests.An_adapter_speaking_another_protocol_is_refused` so "0.5" is a refused version — `src/Parley.Core/ProtocolVersion.cs`, `tests/Parley.Server.Tests/AdversarialTests.cs`

**Checkpoint**: SPEC.md describes the whole feature and `Parley.Spec.Tests` passes.

---

## Phase 2: Foundational — every story needs these

**⚠️ No user story work begins until this phase is green.**

### Core types (no I/O)

- [X] T012 [P] Write `SharedFileNameTests` covering every rule of research.md R4 (folder + separator + name at most 255 characters with the location's own path not counted, `.md`/`.txt` extension only, a case-folded key that equates `Übersicht.md` and `übersicht.md`, forbidden characters, `.`/`..`, leading/trailing space or dot, reserved device names with and without extension, case) — `tests/Parley.Core.Tests/SharedFileNameTests.cs`
- [X] T013 [P] Implement `SharedFileName` (validate, normalise, reason for refusal) — `src/Parley.Core/SharedFileName.cs`
- [X] T014 [P] Implement `ContentHash` (64 lowercase hex, parse, equality) with tests — `src/Parley.Core/ContentHash.cs`, `tests/Parley.Core.Tests/ContentHashTests.cs`
- [X] T015 [P] Add records `SharedFile`, `FileNotice`, `FileLocation`, `FilePlacementCount`, `RemoveFilesOutcome`, `PruneFilesOutcome` — `src/Parley.Core/SharedFile.cs`, `src/Parley.Core/FileNotice.cs`, `src/Parley.Core/FileLocation.cs`, `src/Parley.Core/FileOutcomes.cs`
- [X] T016 [P] Add `StoreFiles.Files = ".files"` and `StoreFiles.FilesIncoming = ".files-incoming"` with their remarks, and make `project remove --destroy` delete both directories beside the store where it already deletes `StoreFiles.Lock` and `StoreFiles.Owner`, and assert both are gone after a destroy — `src/Parley.Core/StoreFiles.cs`, `src/Parley.Setup/ProjectCommand.cs`, `tests/Parley.Cli.Tests/ProjectDestroyTests.cs`

### Storage (schema 9)

- [X] T017 Write migration tests first: 009 applied to a populated schema-8 store built from the embedded migrations (sequence continuity, `PRAGMA integrity_check`, `foreign_key_check`), then probes that each constraint refuses its malicious case — duplicate name differing only in case, bad name, a disallowed extension, bad hash, `placed_by` naming an unregistered agent, an update changing `placed_by`, a notice whose `sha256_now` equals `sha256_had`, two notices for one agent and file, two conversations given folders differing only in case, an update of a folder name, a file whose conversation has no folder row; and that a rename updating `name` in place keeps `placed_by` and `placed_utc` — `tests/Parley.Store.Tests/FileStoreMigrationTests.cs`, `tests/Parley.Store.Tests/FileStoreConstraintTests.cs`
- [X] T018 Write migration 009 (`conversation_folders`, `shared_files`, `file_fetches`, `file_notices`, `file_placements`, indexes, triggers) matching SPEC.md §9.2 exactly; and update the expected latest version to 9 in `MigrationDiscoveryTests` (`SchemaMigrator.LatestVersion` itself reads the embedded migrations) — `src/Parley.Store/Migrations/009_file_store.sql`, `tests/Parley.Store.Tests/MigrationDiscoveryTests.cs`
- [X] T019 Implement `SharedFileRepository`: the conversation's folder row written with its first file and kept after its last, insert new (unique-index loser reported as placed by the winner), replace content (same placer only), identical-content no-op, remove by ref, remove all, resolve name without conversation (one / none / ambiguous), reconcile row from disk facts, rename in place, placement counts — `src/Parley.Store/SharedFileRepository.cs`
- [X] T020 [P] Write `SharedFileRepositoryTests` for every branch of T019, including the concurrent-new-name race with two connections — `tests/Parley.Store.Tests/SharedFileRepositoryTests.cs`
- [X] T021 Delete a conversation's `conversation_folders`, `shared_files` and `file_fetches` rows when `ConversationRepository.DestroyAsync` destroys it, raising "gone" notices first (the notice write itself lands in T047; leave a clearly named hook here); do the same for every conversation `AgentRepository.PurgeAsync` deletes, counting the files in its toll (the owner's call, H6) — `src/Parley.Store/ConversationRepository.cs`, `src/Parley.Store/AgentRepository.cs`

### The location on disk

- [X] T022 Implement `FileLocationStore`: layout of research.md R2 (folder per conversation chosen once and read from `conversation_folders`, `~N` suffix when the id is not a safe or unique folder name, every folder already on disk counting as taken), staging in `<store>.files-incoming/` beside the location (never inside it), emptied at start, streaming write with incremental SHA-256, atomic commit by move, streaming read opened with `FileShare.Read`, per-file in-memory hold counts with an awaitable "no holds" signal released when a read's cancellation token fires — `src/Parley.Server/FileLocationStore.cs`
- [X] T023 [P] Write `FileLocationStoreTests`: 100 MB streamed write and read byte-identical with memory bounded by one chunk, commit is atomic (a cancelled write leaves nothing), replacement awaits holds, a cancelled read releases its hold, folder naming (`con`, `Pricing` beside `pricing`, `pricing.` and `.pricing` each get `~N`; a conversation named `con~2` cannot exist, a hand-made `pricing/` already on disk gives conversation `pricing` the folder `pricing~2`), and a full disk during a write (through a write seam) refused as `FILE_REJECTION_NO_SPACE` with nothing staged left behind and a replaced file's previous content intact — `tests/Parley.Server.Tests/FileLocationStoreTests.cs`
- [X] T024 Give `BrokerRuntime` a files root: `Open` derives `<store>.files`, `OpenInMemory`/`Over` take an optional root and default to a temporary directory deleted on dispose; the runtime owns one `FileLocationStore` — `src/Parley.Server/BrokerRuntime.cs`

### Wire skeleton

- [X] T025 Add every message, enum and RPC of contracts/wire.md to the proto (fields only; handlers come per story), and extend `TransportEnumTests` for `FileRejection` and `PlaceOutcome` — `proto/parley.proto`, `tests/Parley.Server.Tests/TransportEnumTests.cs`
- [X] T026 Add wire conversions for the new types — `src/Parley.Server/Wire.cs`, `src/Parley.Mcp/WireCodec.cs`

**Checkpoint**: the solution builds; Core, Store, Server and Spec suites pass; the location can hold bytes but nothing exposes it.

---

## Phase 3: User Story 1 — An agent hands a file to another agent (P1) 🎯 MVP

**Goal**: an agent places a file into its conversation's files and any agent in the project fetches it, byte-identical,
into its own repository.

**Independent Test**: two agents on a loopback broker; the first places, the second fetches by name; bytes and hash match
(spec.md US1).

### Tests for User Story 1 (write first, see them fail)

- [X] T027 [P] [US1] Loopback tests for placing: conversation assigned by the Request rule, an agent holding no request starting a new conversation (unnamed, or named and not yet existing) as its participant and able to place a second file into it by name, several conversations without a name refused with the assignable list, closed conversation refused, bad name refused, an extension other than `.md`/`.txt` refused with the advice to add `.txt`, another agent's name refused naming the placer, interrupted stream leaves nothing and frees the name, concurrent new name one winner, a zero-byte file placed and listed with the hash of empty content, a name too long for its conversation refused saying how many characters that conversation leaves — `tests/Parley.Server.Tests/PlaceFileTests.cs`
- [X] T028 [P] [US1] Loopback tests for fetching: byte-identical 100 MB round trip, name alone resolved or refused as ambiguous, no such file, `expect_sha256` mismatch refused with the current hash, a fetch recorded only after `FetchedFile`, and `FetchedFile` refused with `FILE_REJECTION_NOT_FETCHED` for a file this agent never fetched, for a hash other than the one streamed, and after its connection ended; two concurrent fetches of one file, replaced in between, each reportable with its own hash — `tests/Parley.Server.Tests/FetchFileTests.cs`
- [X] T029 [P] [US1] Read-hold tests: a replacement requested during a fetch completes only after it, the fetch receives the content it started on; an agent disconnecting mid-fetch releases the hold; on Windows a write opened by another process during a fetch is refused (skipped elsewhere with the reason); a change under a fetch on any platform ends in `FILE_REJECTION_CHANGED` — `tests/Parley.Server.Tests/FileReadHoldTests.cs`
- [X] T030 [P] [US1] Adapter tests: `place` and `fetch` paths confined to the repository root (`Workspace`, else current directory), a junction or symlink out of the root refused, fetch refuses to overwrite a different file without `replace`, identical existing file reported `already_current` without rewriting, a hash mismatch leaves nothing at the target, tool results contain no file bytes — `tests/Parley.Mcp.Tests/FileToolTests.cs`

### Implementation for User Story 1

- [X] T031 [US1] Implement `AgentsService.PlaceFile`: read the header, validate with `SharedFileName`, assign the conversation via `ExchangeRepository.AssignConversationAsync` (starting one when the agent holds none, and joining the placer through `ConversationRepository.JoinAsync` in the same transaction), stream to `FileLocationStore`, commit through `SharedFileRepository` in one transaction, record placement counts, reply per contracts/wire.md — `src/Parley.Server/AgentsService.cs`
- [X] T032 [US1] Implement `AgentsService.FetchFile` (resolve, header, hold, stream, end-of-stream hash check) and `FetchedFile` (check it against the completed stream held in memory for that agent, then record `file_fetches`) — `src/Parley.Server/AgentsService.cs`, `src/Parley.Store/FileNoticeRepository.cs` (fetch records only)
- [X] T033 [US1] Implement `RepositoryPaths` (root resolution, confinement, link refusal) — `src/Parley.Mcp/RepositoryPaths.cs`
- [X] T034 [US1] Add `parley_file_place` and `parley_file_fetch` tools (temp file in the target directory, verify, move, `replace`, `expect_sha256`, then `FetchedFile`, saying when a refused report leaves the file written but unrecorded), with descriptions saying contents never come back and a file is untrusted data — `src/Parley.Mcp/ParleyTools.cs`
- [X] T035 [US1] Update `ToolSurfaceTests` and any tool-count assertions for the two new tools — `tests/Parley.Mcp.Tests/ToolSurfaceTests.cs`

**Checkpoint**: SC-001, SC-002, SC-007 (place/fetch halves) and SC-013 hold for one conversation over a real socket.

---

## Phase 4: User Story 2 — An agent hears that a file it fetched has changed (P2)

**Goal**: every agent that fetched a file finds one change notice at its next inbox check when the file changes or goes,
and nothing wakes.

**Independent Test**: fetch, change twice, replace; next inbox holds one notice with the hash had and the hash now; no
node command was issued; remove; next inbox says gone (spec.md US2).

### Tests for User Story 2 (write first, see them fail)

- [X] T036 [P] [US2] Store tests for notices: raised for each non-retired fetcher, coalesced to one per agent per file, withdrawn when the content returns to the fetched hash, a re-fetch moves the reference, "gone" on removal, on destroying the conversation and on purging a participant, purge cascades, collection deletes what it returns and no more (at most 200, oldest change first) and counts what is left — `tests/Parley.Store.Tests/FileNoticeRepositoryTests.cs`
- [X] T037 [P] [US2] Loopback tests: notices arrive in `InboxReply.file_notices`, `Waiting` counts them, the inbox `limit` does not bound notices, and 250 waiting notices come back as 200 then 50, oldest change first, with `file_notices_waiting` 50 then 0 and no wake between the checks, **no node command is issued for any change** (count commands on a joined node), an agent not running collects them when it next attaches — `tests/Parley.Server.Tests/FileNoticeTests.cs`
- [X] T038 [P] [US2] Watcher tests: an in-place write, a save-by-rename, a rename, a deletion and a file copied in by hand (ignored unless it ends in `.md`/`.txt`; a rename to another extension counts as gone) are each reflected (hash, size, time; new name keeps placer; gone; placed by the person); a burst of writes raises one notice; a change made while the runtime was stopped is found at start; a rename made while stopped keeps its placer when exactly one missing file in the folder has the same hash, and belongs to the person when none or several do; a hand-copied `con.md`, a name ending in a dot and a name too long for its folder are each ignored, and a rename to such a name counts as gone; a file copied by hand into the folder of a conversation whose last file was removed is traced to that conversation; a folder no conversation owns is ignored; a change the watcher missed is found by the reconcile on list and fetch — `tests/Parley.Server.Tests/FolderWatcherTests.cs`

### Implementation for User Story 2

- [X] T039 [US2] Implement `FileNoticeRepository`: raise for fetchers on hash change or disappearance (skipping retired agents), withdraw when unchanged, collect-and-delete for one agent (at most 200, oldest `changed_utc` first), count waiting — `src/Parley.Store/FileNoticeRepository.cs`
- [X] T040 [US2] Call notice raising from every content change path in `SharedFileRepository` (replace, reconcile, remove, remove all) inside the same transaction — `src/Parley.Store/SharedFileRepository.cs`
- [X] T041 [US2] Return notices from `Broker.DrainInboxAsync` in `Inbox.FileNotices` and map them and `file_notices_waiting` into `InboxReply`; add `file_notices_waiting` to `AgentsService.Waiting`; confirm by inspection and test that no path calls `RequestAttentionForMail` for a file — `src/Parley.Broker/Broker.cs`, `src/Parley.Broker/Inbox.cs`, `src/Parley.Server/AgentsService.cs`
- [X] T042 [US2] Implement `FolderWatcher` (one watcher on the location root, per-file 500 ms debounce, rename carries the placer, hash-based idempotence, start-up reconcile of the whole location) and start it from `BrokerRuntime` when the location first holds a file — `src/Parley.Server/FolderWatcher.cs`, `src/Parley.Server/BrokerRuntime.cs`
- [X] T043 [US2] Add the size-and-time reconcile before every list and fetch (FR-017) — `src/Parley.Server/FileLocationStore.cs`, `src/Parley.Store/SharedFileRepository.cs`
- [X] T044 [US2] Return `file_notices` and `file_notices_waiting` from `parley_inbox` with the notes of contracts/commands.md; describe notices in the inbox tool description and `ParleyInstructions` — `src/Parley.Mcp/ParleyTools.cs`, `src/Parley.Mcp/ParleyInstructions.cs`
- [X] T045 [US2] Show waiting file notices in the plugin's waiting hook when `file_notices_waiting` is non-zero, and extend its tests — `plugins/parley/hooks/parley_waiting.py`, `tests/Parley.Mcp.Tests/WaitingHookTests.cs`
- [X] T046 [P] [US2] Test that a seat's drain ignores `file_notices`: a seat never fetches, so it holds none, and neither the terminal nor the web inbox changes (the owner's call, answering `/speckit-analyze` L1) — `tests/Parley.Client.Tests/SeatFileNoticeTests.cs`
- [X] T047 [US2] Raise "gone" notices when a conversation is destroyed, or purged with an agent, completing the hook left in T021, and delete the folder on disk from the operator's destroy and purge paths once downloads in progress finish, with a loopback test for each — `src/Parley.Store/ConversationRepository.cs`, `src/Parley.Server/OperatorsService.cs`, `tests/Parley.Server.Tests/FileDestroyTests.cs`

**Checkpoint**: SC-004, SC-005, SC-006 hold; US1 still passes.

---

## Phase 5: User Story 3 — An agent or a person sees what is in the location (P3)

**Goal**: one list, on every surface, with conversation, name, size, hash, placer, time and totals.

**Independent Test**: three files across two conversations from two agents list identically on the CLI, terminal and
web UI (spec.md US3).

### Tests for User Story 3

- [X] T048 [P] [US3] Loopback tests for `ListFiles` (agent) and `ListSharedFiles` (operator): filter by conversation, totals, placements only on the operator reply, empty location; paging on both: 50 by default, 200 when 500 are asked, 250 files reached exactly once by following `next_page_token`, the location's total counting all 250 on every page, and each page's totals naming only its conversations — `tests/Parley.Server.Tests/ListFilesTests.cs`
- [X] T049 [P] [US3] CLI report tests: columns, short hash with `--full`, totals, placements and bytes per agent (FR-039), "The file location is empty.", a 250-file location printed whole by following every page, names flattened so a newline cannot forge a row — `tests/Parley.Cli.Tests/FilesListCommandTests.cs`
- [X] T050 [P] [US3] Terminal Files dialog tests (list only, driven harness, a 250-file location reached whole across pages, column-zero rule for names, placements and bytes per agent under the list, and a conversation holding only files shown in the conversation list and transcript without error) — `tests/Parley.Terminal.Tests/FilesDialogTests.cs`
- [X] T051 [P] [US3] Web Files page tests (render, filter, a page of 50 with a working link to the next, names HTML-encoded, placements and bytes per agent, and a conversation holding only files shown in the conversation list and detail page without error) — `tests/Parley.Web.Tests/FilesPageTests.cs`

### Implementation for User Story 3

- [X] T052 [US3] Implement `AgentsService.ListFiles` and `OperatorsService.ListSharedFiles`, paged by `(conversation_id, name_key)` under the bounds `OperatorsService` already applies (`DefaultPageSize` 50, `MaximumPageSize` 200, moved to where both services read them) — `src/Parley.Server/AgentsService.cs`, `src/Parley.Server/OperatorsService.cs`
- [X] T053 [US3] Add `ListSharedFilesAsync` to the operator contract and client — `src/Parley.Core/IParleyBrokerClient.cs`, `src/Parley.Client/BrokerClient.cs`
- [X] T054 [US3] Add `parley_file_list` agent tool with `page_size` and `page_token`, returning `next_page_token` — `src/Parley.Mcp/ParleyTools.cs`
- [X] T055 [US3] Add `parley files list`, following every page — `src/Parley.Cli/FilesCommand.cs`, `src/Parley.Cli/Program.cs`
- [X] T056 [US3] Add the terminal Files dialog (from a project and from a conversation) binding `ListSharedFilesAsync` and fetching every page, showing placements and bytes per agent under the list, and make the conversation list and transcript handle a conversation with no messages — `src/Parley.Terminal/Files.cs`, `src/Parley.Terminal/Shell.cs`
- [X] T057 [US3] Add the web Files page binding `ListSharedFilesAsync`, a page at a time with a link to the next, linked from a project and a conversation, showing placements and bytes per agent, and make the conversation list and detail page handle a conversation with no messages — `src/Parley.Web/Pages/Projects/Files.cshtml`, `src/Parley.Web/Pages/Projects/Files.cshtml.cs`
- [X] T058 [P] [US3] Add the `files-list` plugin command (`disable-model-invocation: true`, runs `parley files list`) — `plugins/parley/commands/files-list.md`

**Checkpoint**: SC-003 holds; `DriftTests` passes with `ListSharedFilesAsync` bound on both surfaces.

---

## Phase 6: User Story 4 — An agent replaces or takes back a file it placed (P4)

**Goal**: the agent that placed a file replaces or removes it; no other agent can.

**Independent Test**: replace with new content (new hash), place identical content (unchanged), remove own (gone),
replace or remove another's (refused naming the placer) — spec.md US4.

### Tests for User Story 4

- [X] T059 [P] [US4] Loopback tests: replacement by the placer returns `REPLACED` with previous and new hash and raises notices; identical content returns `ALREADY_CURRENT`, counts nothing, raises nothing; `RemoveOwnFiles` removes own names, refuses others with the placer named, reports each in a mixed list; a file placed by the person cannot be removed by any agent, a request of 201 names refused with nothing removed — `tests/Parley.Server.Tests/ReplaceAndRemoveOwnTests.cs`
- [X] T060 [P] [US4] Adapter tests for `parley_file_place` replacement results and `parley_file_remove` — `tests/Parley.Mcp.Tests/FileToolTests.cs`

### Implementation for User Story 4

- [X] T061 [US4] Complete replacement in `PlaceFile`: same placer replaces after awaiting read holds, identical hash is a no-op, previous hash in the reply — `src/Parley.Server/AgentsService.cs`, `src/Parley.Store/SharedFileRepository.cs`
- [X] T062 [US4] Implement `AgentsService.RemoveOwnFiles` (placer check from the row, await holds, delete bytes and row, raise notices) — `src/Parley.Server/AgentsService.cs`
- [X] T063 [US4] Add `parley_file_remove` — `src/Parley.Mcp/ParleyTools.cs`

**Checkpoint**: SC-008 holds.

---

## Phase 7: User Story 5 — The person removes specific files (P5)

**Goal**: the person removes any named files after a description and confirmation, on every surface.

**Independent Test**: four files from two agents; remove two; the other two remain (spec.md US5).

### Tests for User Story 5

- [X] T064 [P] [US5] Loopback tests for `RemoveFiles`: any placer, ambiguous name refused listing conversations, missing names reported without failing the rest, bytes freed, notices raised, an ambiguity listing at most 200 conversations with the count of the rest, a request of 201 names refused with nothing removed — `tests/Parley.Server.Tests/OperatorRemoveFilesTests.cs`
- [X] T065 [P] [US5] CLI tests: description then confirmation, `--yes`, report, more than 200 names refused before asking — `tests/Parley.Cli.Tests/FilesRemoveCommandTests.cs`
- [X] T066 [P] [US5] Terminal and web tests for removal with its door, a web remove POST without an antiforgery token refused, and a `SurfaceTests` case holding `RemoveFilesAsync` to `FilesRemoving.cs` with its confirmation text — `tests/Parley.Terminal.Tests/FilesRemovingTests.cs`, `tests/Parley.Terminal.Tests/SurfaceTests.cs`, `tests/Parley.Web.Tests/FilesPageTests.cs`

### Implementation for User Story 5

- [X] T067 [US5] Implement `OperatorsService.RemoveFiles` — `src/Parley.Server/OperatorsService.cs`
- [X] T068 [US5] Add `RemoveFilesAsync` to the operator contract and client — `src/Parley.Core/IParleyBrokerClient.cs`, `src/Parley.Client/BrokerClient.cs`
- [X] T069 [US5] Add `parley files remove` with confirmation — `src/Parley.Cli/FilesCommand.cs`
- [X] T070 [US5] Add terminal removal behind its door, the only file calling `RemoveFilesAsync` — `src/Parley.Terminal/FilesRemoving.cs`, `src/Parley.Terminal/Files.cs`
- [X] T071 [US5] Add the web remove form with its confirmation step — `src/Parley.Web/Pages/Projects/Files.cshtml`, `src/Parley.Web/Pages/Projects/Files.cshtml.cs`
- [X] T072 [P] [US5] Add the `files-remove` plugin command — `plugins/parley/commands/files-remove.md`

**Checkpoint**: `SurfaceTests` and `DriftTests` pass with removal bound on both surfaces.

---

## Phase 8: User Story 6 — The person empties the location (P6)

**Goal**: prune removes every file in every conversation behind the project's name typed back; nothing else changes.

**Independent Test**: files in several conversations; prune; list empty; conversations and messages unchanged (spec.md US6).

### Tests for User Story 6

- [X] T073 [P] [US6] Loopback tests for `PruneFiles`: every file gone, disk space freed, conversations, exchanges, messages and agents unchanged, notices raised, waits for holds; `conversation_folders` rows kept; the message pruner (`Pruner`) leaves files alone, and closing or force-concluding a conversation leaves its files — `tests/Parley.Server.Tests/PruneFilesTests.cs`
- [X] T074 [P] [US6] CLI, terminal and web tests for prune behind the project's name, a web prune POST without an antiforgery token refused, and a `SurfaceTests` case holding `PruneFilesAsync` to `FilesPruning.cs` with "Type the project's name" — `tests/Parley.Cli.Tests/FilesPruneCommandTests.cs`, `tests/Parley.Terminal.Tests/FilesPruningTests.cs`, `tests/Parley.Terminal.Tests/SurfaceTests.cs`, `tests/Parley.Web.Tests/FilesPageTests.cs`

### Implementation for User Story 6

- [X] T075 [US6] Implement `OperatorsService.PruneFiles` — `src/Parley.Server/OperatorsService.cs`
- [X] T076 [US6] Add `PruneFilesAsync` to the operator contract and client — `src/Parley.Core/IParleyBrokerClient.cs`, `src/Parley.Client/BrokerClient.cs`
- [X] T077 [US6] Add `parley files prune` with the typed name — `src/Parley.Cli/FilesCommand.cs`
- [X] T078 [US6] Add terminal prune, the only file calling `PruneFilesAsync` — `src/Parley.Terminal/FilesPruning.cs`, `src/Parley.Terminal/Files.cs`
- [X] T079 [US6] Add the web prune form — `src/Parley.Web/Pages/Projects/Files.cshtml`, `src/Parley.Web/Pages/Projects/Files.cshtml.cs`
- [X] T080 [P] [US6] Add the `files-prune` plugin command — `plugins/parley/commands/files-prune.md`

**Checkpoint**: SC-012 and SC-015 hold.

---

## Phase 9: User Story 7 — The person opens a file (P7)

**Goal**: open a stored file in place in its registered editor on the broker's machine; report machine and path
elsewhere; never execute a file.

**Independent Test**: open, edit, save on the broker's machine — new hash, same placer, notice raised; from another
machine name, machine and path printed, nothing launched (spec.md US7).

### Tests for User Story 7

- [X] T081 [P] [US7] Launcher tests through its seam (nothing is ever launched): a file not ending in `.md`/`.txt`, or not a regular file inside the location, refused with its path (research.md R10); a local path opens with the edit verb where registered; a different machine name, or a path that does not exist locally, reports "on machine X, at Y"; no registered handler reported — `tests/Parley.Client.Tests/LocalLauncherTests.cs`
- [X] T082 [P] [US7] Loopback test for `DescribeFileLocation` (machine name, absolute path of file and folder, an ambiguous name refused with its conversations in `FileRefusal`) — `tests/Parley.Server.Tests/FileLocationTests.cs`
- [X] T083 [P] [US7] CLI, terminal (`o`) and web (Open) tests using the launcher seam, a web Open POST without an antiforgery token refused, including that the suite opens nothing on the machine running it — `tests/Parley.Cli.Tests/FilesOpenCommandTests.cs`, `tests/Parley.Terminal.Tests/FilesOpenTests.cs`, `tests/Parley.Web.Tests/FilesPageTests.cs`

### Implementation for User Story 7

- [X] T084 [US7] Implement `OperatorsService.DescribeFileLocation` and `DescribeFileLocationAsync` on the contract and client — `src/Parley.Server/OperatorsService.cs`, `src/Parley.Core/IParleyBrokerClient.cs`, `src/Parley.Client/BrokerClient.cs`
- [X] T085 [US7] Implement `LocalLauncher.OpenFile` (edit verb, `.md`/`.txt` only, remote rule by `MachineName.Current` and local existence) as a replaceable seam — `src/Parley.Client/Files/LocalLauncher.cs`
- [X] T086 [US7] Add `parley files open` — `src/Parley.Cli/FilesCommand.cs`
- [X] T087 [US7] Add terminal open (`o`) through a `Shell` launcher seam replaced in `Driving.Show` — `src/Parley.Terminal/Files.cs`, `src/Parley.Terminal/Shell.cs`, `tests/Parley.Terminal.Tests/Driving.cs`
- [X] T088 [US7] Add the web Open action, launching on the web UI's machine — `src/Parley.Web/Pages/Projects/Files.cshtml.cs`
- [X] T089 [P] [US7] Add the `files-open` plugin command — `plugins/parley/commands/files-open.md`

**Checkpoint**: SC-010 (file half) and SC-011 hold; no test run launches an editor.

---

## Phase 10: User Story 8 — The person opens a conversation's files folder (P8)

**Goal**: open a conversation's folder (or the whole location) in the file browser; report machine and path elsewhere.

**Independent Test**: open the folder on the broker's machine; from another machine, machine and path printed, nothing
launched (spec.md US8).

### Tests for User Story 8

- [X] T090 [P] [US8] Launcher folder tests through the seam (local, remote, conversation without files, whole location) — `tests/Parley.Client.Tests/LocalLauncherTests.cs`
- [X] T091 [P] [US8] CLI, terminal (`f`) and web (Open folder) tests, a web Open folder POST without an antiforgery token refused — `tests/Parley.Cli.Tests/FilesFolderCommandTests.cs`, `tests/Parley.Terminal.Tests/FilesOpenTests.cs`, `tests/Parley.Web.Tests/FilesPageTests.cs`

### Implementation for User Story 8

- [X] T092 [US8] Implement `LocalLauncher.OpenFolder` — `src/Parley.Client/Files/LocalLauncher.cs`
- [X] T093 [US8] Add `parley files folder` — `src/Parley.Cli/FilesCommand.cs`
- [X] T094 [US8] Add terminal open folder (`f`), using the conversation being viewed — `src/Parley.Terminal/Files.cs`
- [X] T095 [US8] Add the web Open folder action — `src/Parley.Web/Pages/Projects/Files.cshtml.cs`
- [X] T096 [P] [US8] Add the `files-folder` plugin command — `plugins/parley/commands/files-folder.md`

**Checkpoint**: SC-010 holds in full; every story's independent test passes.

---

## Phase 11: Polish & Cross-Cutting Concerns

- [X] T097 [P] Document the file store for agents and people (the five commands, notices, the executable refusal, the platform difference; the README's "What Parley does" opening was already brought into line on 2026-09-14 and marks file sharing as arriving in 0.27.0 — remove that qualifier when it ships) — `README.md`, `plugins/parley/README.md`
- [X] T098 [P] Add the files verbs to the operator skill and keep its verb-parity test green — `plugins/parley/skills/parley-operator/SKILL.md`, `tests/Parley.Mcp.Tests/PluginTests.cs`, `tests/Parley.Cli.Tests/RenameTests.cs`
- [X] T099 [P] Update the node's turn prompt and the Peers block so an agent knows files can be placed and fetched and that a Request should name a file's hash — `src/Parley.Mcp/ParleyInstructions.cs`, the turn prompt at `InvocationCommand.Prompt`, and the `## Peers` section written by `src/Parley.Setup/Enrolment.cs`
- [X] T100 Run `GraphTests` and confirm no store reaches `Parley.Client`, `Parley.Terminal`, `Parley.Web` or `Parley.Setup` after the launcher and page additions — `tests/Parley.Client.Tests/`, `tests/Parley.Web.Tests/`
- [X] T101 Walk quickstart.md end to end against a built deployment and record the result in the ledger — `specs/015-file-store/quickstart.md`
- [X] T102 Run every test suite sequentially on one build (Server, Cli and Top alone), and treat a failure that passes on rerun at the same SHA as a flake only after the rerun — all `tests/Parley.*.Tests`
- [X] T103 Hygiene: case-insensitive search for the originating project's names over tracked **and untracked** files; placeholders are `orders`, `billing`, `inventory` — repository
- [X] T104 Review gate per CLAUDE.md after the last edit: three lenses, three consecutive clean runs each, probes not readings, logged in a 015 section of the untracked ledger — `REVIEW-LEDGER.md`
- [X] T105 Record the built feature in CLAUDE.md's current-plan entry, replacing "Planned, not built" with what the gate found — `CLAUDE.md`

---

## Where the tests landed

Some tasks above name a test file that was never created, because their cases fitted classes that already held the same
fixture. Found by the review gate, lens 3 run 3.8. Each named path, and where its cases are:

| Task | Named | Where the cases are |
|---|---|---|
| T015 | `src/Parley.Core/FileLocation.cs` | the `FileLocation` record is in `src/Parley.Core/SharedFile.cs` |
| T027 | `PlaceFileTests.cs` | `FilesOverTheWireTests` (placing), `FileEdgesOverTheWireTests` (an empty file, a closed conversation, a broken stream, a name too long, conversation ids the store refuses) |
| T028 | `FetchFileTests.cs` | `FilesOverTheWireTests` (fetching and reporting), `FileEdgesOverTheWireTests` (a report after the session ended, two hashes), `FilesUnderRaceAndOnDiskTests` (a fetch racing replacements, one of two sessions ending) |
| T029 | `FileReadHoldTests.cs` | `FileLocationStoreTests` (holds, and a write refused on Windows), `FileEdgesOverTheWireTests` (a download given up releases its hold) |
| T037 | `FileNoticeTests.cs` | `FilesOverTheWireTests` (a notice, nothing woken), `FileEdgesOverTheWireTests` (250 notices over two checks, no node command), `FilesAcrossRestartAndMalformedInputTests` (a notice across a restart) |
| T047 | `FileDestroyTests.cs` | `FilesOverTheWireTests` (destroy and purge delete folders), `FileNoticeRepositoryTests` (gone notices), `FilesUnderRaceAndOnDiskTests` (a junction under a destroy), `FileEdgesOverTheWireTests` (a destroy racing placements) |
| T048 | `ListFilesTests.cs` | `FilesOverTheWireTests` (paging, placements to the person only), `FileEdgesOverTheWireTests` (250 files on both services) |
| T059 | `ReplaceAndRemoveOwnTests.cs` | `FilesOverTheWireTests` (replacing, removing one's own), `FilesAcrossRestartAndMalformedInputTests` (the person's file and a retired placer's) |
| T064 | `OperatorRemoveFilesTests.cs` | `FilesOverTheWireTests` (the person removes anything), `FileEdgesOverTheWireTests` (201 names, an ambiguous name) |
| T073 | `PruneFilesTests.cs` | `FilesOverTheWireTests` (a prune empties the location), `FileEdgesOverTheWireTests` (closing and message pruning leave files, pruning files leaves conversations, a prune racing placements) |
| T082 | `FileLocationTests.cs` | `FilesOverTheWireTests` (where a file is, and an ambiguous name) |

## Dependencies & Execution Order

### Phase dependencies

- **Phase 1 (Setup)** first: the specification leads. T008 depends on T001–T007.
- **Phase 2 (Foundational)** depends on Phase 1 and blocks every story. Within it: T013 after T012 (test first); T018 after
  T017; T019 after T018; T022 before T024; T025 before T026.
- **Phases 3–10** depend on Phase 2. Beyond that:
  - **US1** depends on nothing else and is the MVP.
  - **US2** needs fetch records from US1 (T032) to have anyone to notify.
  - **US3** is independent of US1/US2 apart from having files to list; test it with files placed through the repository.
  - **US4** needs US1's `PlaceFile` (T031); its notices need US2 (T039–T041).
  - **US5** and **US6** are independent of each other; their notices need US2.
  - **US7** needs the watcher from US2 (T042) for the edit-in-place half; the launcher half is independent.
  - **US8** needs T085's launcher shape from US7.
- **Phase 11** after the stories to be delivered. T104 is last before T105, after every edit.

### Within each story

Tests are written and seen to fail, then storage, then broker, then adapter or surface, then plugin command. A story's
checkpoint is its independent test passing with every earlier story still green.

### Parity rule that crosses stories

`DriftTests` requires every `BrokerClient` operation to be bound by **both** the terminal and the web UI. Each operator
operation added to the client (T053, T068, T076, T084) must be bound by both surfaces **in the same phase**, or the
build's tests fail at that checkpoint.

---

## Parallel Example: User Story 1

```text
# Tests together (four files):
T027 tests/Parley.Server.Tests/PlaceFileTests.cs
T028 tests/Parley.Server.Tests/FetchFileTests.cs
T029 tests/Parley.Server.Tests/FileReadHoldTests.cs
T030 tests/Parley.Mcp.Tests/FileToolTests.cs

# Then T031 → T032 (same file, sequential), with T033 in parallel beside them, then T034 → T035.
```

## Parallel Example: Phase 2

```text
T012/T013 SharedFileName    T014 ContentHash    T015 records    T016 StoreFiles + destroy
T020 repository tests (after T018)    T023 location store tests (beside T022)
```

---

## Implementation Strategy

### MVP first

1. Phase 1: SPEC.md, constitution, version.
2. Phase 2: types, schema 9, repositories, the location on disk, the wire skeleton.
3. Phase 3 (US1): place and fetch between two agents.
4. **Stop and validate** with quickstart.md §1 and the US1 tests. A file handed across repositories is the feature.

### Incremental delivery

US2 (notices) next, because a fetched copy that silently goes stale is the failure a shared file invites; then US3 (list)
so people can see what is there; then US4–US6 (replace, remove, prune); then US7–US8 (open file and folder). Each ends at
a checkpoint with every suite green, and none ships as a release until the review gate (T104) is met on the final code.

### Notes

- [P] = different files, no dependency on an unfinished task.
- Commit only when the owner asks.
- C# is edited with the file editing tools, never by scripted text substitution; markdown may be scripted.
- Never `git checkout` over uncommitted work.
- Every launcher path goes through a seam replaced in tests: this repository's suite has opened a person's browser before.
