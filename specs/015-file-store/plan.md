# Implementation Plan: Parley file store

**Branch**: `015-file-store` | **Date**: 2026-09-14 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `specs/015-file-store/spec.md`

## Summary

Let files cross between agents without letting their contents into any model's context. Only `.md` and `.txt` files are
accepted; code travels as `Name.cs.txt` (the owner's rule). An agent places a file from its
repository into its conversation's folder in the project's file location; the bytes stream through the broker on the
connection the agent already holds, land beside the store, and carry a broker-computed SHA-256. Any agent in the project
fetches a file to disk in its own repository, verified against that hash. The agent that placed a file may replace or
remove it; the person may remove anything and prune everything, open files in place and open a conversation's folder,
from the command line, the terminal, the web UI and five plugin commands. A folder watcher re-hashes changes made outside
Parley, a download holds the stored file read-only, and every agent that fetched a file finds a change notice in its inbox
when the file later changes or goes — waking nothing.

Protocol **0.6**, `VersionPrefix` **0.27.0**, schema **9**. SPEC.md changes first.

## Technical Context

**Language/Version**: C# on .NET 10 (`global.json`), warnings as errors, documentation enforced
**Primary Dependencies**: Grpc.AspNetCore / Grpc.Net.Client (wire, streaming RPCs), Microsoft.Data.Sqlite + Dapper (store),
ModelContextProtocol SDK (agent tools), Terminal.Gui v2 (terminal), ASP.NET Core Razor Pages (web UI) — no new packages
**Storage**: SQLite store for conversation folders, file metadata, fetch records, notices and counts (migration 009); file bytes on disk under
`<store>.files/`
**Testing**: xUnit v3 on Microsoft.Testing.Platform, run as executables; `Parley.Spec.Tests` executes SPEC.md's schema;
probes written before guards are trusted
**Target Platform**: Windows, Linux and macOS (CI on all three); the read-only hold is enforced by the OS on Windows only
(`research.md` R5)
**Project Type**: protocol reference implementation — broker service, agent adapter, CLI, terminal UI, web UI
**Performance Goals**: a 100 MB file placed and fetched byte-identical with streaming memory bounded by one chunk (256 KiB)
per call; at most 200 change notices per inbox call, and file lists paged at 50 by default and 200 at most (R17)
**Constraints**: `Parley.Core` has no I/O; no store on the client, setup, terminal or web graphs (`GraphTests`); gRPC's
default 4 MiB message limit is left alone; no file size limit (owner); nothing about a file wakes an agent
**Scale/Scope**: tens of agents per project, hundreds of files per conversation, files up to hundreds of MB

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked after Phase 1 design.*

| Principle / boundary | Verdict | How |
|---|---|---|
| I. The specification is the product | Pass | SPEC.md §1, §2.2, §3, §4.2, §5, §5.0, §9.2–9.3, §10.1–10.2, §13, §14, §16, §18 change before any code (R15); the schema fragment is executed by `Parley.Spec.Tests`. |
| II. No agent has authority over another | Pass | Replace and remove are limited to the agent recorded as placer (`placed_by`, R3); removing any file and pruning are operator operations (R12). |
| III. Identity is fixed by the launch | Pass | No file operation takes a sender; the placer and fetcher are the attached agent (R9). |
| IV. Terseness is a security control | Pass | A file's path inside the location bounded to 255 characters by schema; lists and notices carry names, sizes, hashes and times only; file lists are paged (50 by default, 200 at most) and an inbox check carries at most 200 notices, an ambiguity refusal at most 200 conversations and a removal request at most 200 names (R7, R17); bytes never enter a context (R4, R7). |
| V. Invariants live in storage | Pass | Uniqueness per conversation (a case-folded key for every letter), name rules, hash shape, placer immutability, one notice per agent per file, no no-op notice — all constraints or triggers (data-model.md). |
| "What crosses between agents is messages and placed files, never contexts" | Pass | The boundary was amended to constitution **2.0.0** on 2026-09-14, before implementation, after `/speckit-analyze` found the previous "never files" wording a blocking conflict. Files cross through the location; their contents never enter a context (research.md R9, R10). SPEC.md §1 and principle 2.2 were brought into line the same day. |
| Agent and operator operations are different surfaces | Pass | Agent file operations on `Agents`, operator ones on `Operators`; plugin commands are `disable-model-invocation` (R12, R13). |
| Credentials are references | N/A | No credentials involved. |
| The transport is disposable | Pass | Chunked place/fetch is expressible over any stream-capable transport; nothing binds file semantics to gRPC. |
| `Parley.Core` has no I/O | Pass | Core gains only pure types and validation (`SharedFileName`, `ContentHash`, `SharedFile`, `FileNotice`, `FileLocation`); hashing of streams and the watcher live in Store and Server. |
| Quality gates | Pass | Probes before guards for the name rules, the placer check, the read-only hold, the executable refusal and notice coalescing; the review gate's three lenses after the last edit. |

**Re-check after Phase 1**: unchanged. The design added one finding the gate did not anticipate — opening a file with the
registered handler can execute it (R10) — and resolves it inside Principle IV's and FR-041's intent rather than
contradicting either. The owner then narrowed the location to `.md` and `.txt` files (analysis H2), which removes the
hazard at placement; the launcher keeps the extension check as a second guard.

## Project Structure

### Documentation (this feature)

```text
specs/015-file-store/
├── spec.md
├── plan.md              # this file
├── research.md          # R1–R17
├── data-model.md        # schema 9, on-disk layout, domain types
├── quickstart.md        # end-to-end check
├── contracts/
│   ├── wire.md          # proto additions (protocol 0.6)
│   └── commands.md      # agent tools, CLI, plugin commands, terminal, web UI
├── checklists/
│   └── requirements.md
└── tasks.md             # /speckit-tasks
```

### Source Code (repository root)

```text
SPEC.md                                   §1, §2.2, §3, §4.2, §5, §5.0, §9.2–9.3, §10.1–10.2, §13, §14, §16, §18
.specify/memory/constitution.md           already amended to 2.0.0; verified, not edited (T009)
Directory.Build.props                     0.27.0
proto/parley.proto                        Agents + Operators additions, InboxReply.file_notices, Notice.file_notices_waiting

src/Parley.Core/
├── ProtocolVersion.cs                    0.6
├── StoreFiles.cs                         + Files and FilesIncoming suffixes
├── SharedFileName.cs                     name rules (R4)
├── ContentHash.cs, SharedFile.cs, FileNotice.cs, FileLocation.cs
└── IParleyBrokerClient.cs                + ListSharedFiles, RemoveFiles, PruneFiles, DescribeFileLocation

src/Parley.Store/
├── Migrations/009_file_store.sql         five tables, indexes, triggers
├── SharedFileRepository.cs               rows, folders, placer checks, reconcile, renames in place, counts
└── FileNoticeRepository.cs               fetch records, notice upsert/withdraw/collect

src/Parley.Broker/
├── Broker.cs                             DrainInboxAsync returns file notices
└── Inbox.cs                              + FileNotices

src/Parley.Server/
├── FileLocationStore.cs                  disk layout, staging beside the location, atomic commit, streaming hash, read holds (R2, R5)
├── FolderWatcher.cs                      watcher, debounce, start-up reconcile (R6)
├── AgentsService.cs                      PlaceFile, FetchFile, FetchedFile, ListFiles, RemoveOwnFiles
├── OperatorsService.cs                   ListSharedFiles, RemoveFiles, PruneFiles, DescribeFileLocation
├── BrokerRuntime.cs                      owns the location, holds and watcher; files root for in-memory runtimes
└── Wire.cs                               conversions

src/Parley.Mcp/
├── RepositoryPaths.cs                    confinement to the repository root (R9)
└── ParleyTools.cs                        parley_file_place, parley_file_fetch, parley_file_list, parley_file_remove; inbox notices

src/Parley.Client/
├── BrokerClient.cs                       operator file operations
└── Files/LocalLauncher.cs                open file / folder, .md/.txt only, remote rule (R10, R11)

src/Parley.Cli/
└── FilesCommand.cs                       parley files list|remove|prune|open|folder

src/Parley.Terminal/
├── Files.cs                              Files dialog
├── FilesRemoving.cs                      the only file calling RemoveFilesAsync, behind its door
└── FilesPruning.cs                       the only file calling PruneFilesAsync, behind the project's name

src/Parley.Web/Pages/Projects/
└── Files.cshtml(.cs)                     list, remove, prune, open, open folder

src/Parley.Setup/                         project remove --destroy deletes <store>.files/ (via StoreFiles)

plugins/parley/commands/                  files-list.md, files-remove.md, files-prune.md, files-open.md, files-folder.md
plugins/parley/skills/parley-operator/SKILL.md, README.md

tests/
├── Parley.Core.Tests/                    SharedFileName, ContentHash
├── Parley.Store.Tests/                   migration 009 on a schema-8 store; constraints and triggers probed
├── Parley.Broker.Tests/                  notices in the inbox, collection deletes them
├── Parley.Server.Tests/                  place/fetch over a loopback socket, 100 MB round trip, concurrent new names,
│                                         read hold vs replacement, watcher re-hash, notices wake nothing
├── Parley.Mcp.Tests/                     path confinement, tool results carry no contents
├── Parley.Client.Tests/                  launcher refusal of executable types, remote rule (seam, launches nothing)
├── Parley.Cli.Tests/                     files commands and their confirmations
├── Parley.Terminal.Tests/                Files dialog, SurfaceTests doors, DriftTests parity
├── Parley.Web.Tests/                     Files page, forms, confirmation
└── Parley.Spec.Tests/                    schema 9 executes; the wire against the document
```

**Structure Decision**: No new project. Every addition goes into the project that already owns its layer, which keeps the
graph walks (`GraphTests`) true without changes: disk and watcher in `Parley.Server` (the one process that owns the store),
metadata in `Parley.Store`, pure rules in `Parley.Core`, the launcher in `Parley.Client` (shared by terminal, web UI and
CLI, and free of any store).

## Order

Each phase ends green, every test project run directly.

1. **Specification.** SPEC.md sections of R15, including schema 9 and the §10 tables; confirm constitution 2.0.0.
2. **Storage and core.** `Parley.Core` types, migration 009 against a populated schema-8 store, repositories, and probes
   of every constraint before trusting it.
3. **Broker and wire.** Proto, location store, streaming RPCs, read holds, watcher, notices in the inbox; loopback tests
   including the 100 MB round trip and the concurrency edges.
4. **Agents.** MCP tools, repository confinement, inbox notices, `ParleyInstructions` and tool descriptions.
5. **People.** Launcher, CLI, terminal dialog with doors, web UI page, plugin commands, skill, README — parity held by
   `DriftTests`.
6. **Review gate** per CLAUDE.md, after the last edit; `REVIEW-LEDGER.md` section for 015.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|---|---|---|
| A second source of change beside the protocol (the folder watcher) | The owner chose to let the person edit files in place and have agents told (spec.md stories 2, 7) | Opening a copy would make the person's correction change nothing shared; forbidding outside edits was offered and declined |
| Platform-dependent read-only hold | The owner's rule that a file being downloaded is read-only; Windows can enforce it | POSIX has no mandatory locks; a snapshot per download costs a full copy per concurrent fetch. The hash check keeps the guarantee that matters on every platform (R5) |
