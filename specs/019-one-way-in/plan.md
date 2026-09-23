# Implementation Plan: One place, and one way in

**Branch**: `015-file-store` | **Date**: 2026-09-22 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `specs/019-one-way-in/spec.md`

## Summary

A machine gets one root. Every project is a folder under it, named by the project, holding its own
configuration and its own store — so naming a project is the whole of creating one, and there is no path
for a person to supply. A port is handed out from a record at the root, claimed under a hold so two adds
cannot be given one. Starting Parley serves everything, or Parley is not running. And the command line
sheds sixteen verbs a person cannot use, keeping what a program calls and what brings a machine up.

**The approach that made this smaller than it looks:** three of the four things the spec was most worried
about turned out to be already solved in this repository, and reading the code rather than reasoning about
it is what found them.

- **The serve watcher already polls**, every two seconds, through a `reread` function. There is no
  `FileSystemWatcher` anywhere near serving, so the whole class of hazard feature 015 recorded — a rename
  raising a creation and a change that each replace the pending look — **does not apply at all**. Only
  `reread` changes.
- **An exclusive cross-platform file hold already exists.** `StoreLock` opens with `FileShare.None`: a
  sharing refusal on Windows, an exclusive `flock` on Linux and macOS, released by the operating system
  when the process goes. The port claim is that, held transiently instead of for a lifetime.
- **What belongs to a store is already enumerated**, by `project remove --destroy`, along with the
  ordering discipline the migration needs — take the lock first, as the check that nothing is serving.

What is genuinely new is the port record, the migration, and the removals.

## Technical Context

**Language/Version**: C# 13 / .NET 10
**Primary Dependencies**: no new ones. `System.Text.Json` and `System.IO` for both new files
**Storage**: SQLite per project, unchanged — **no schema change, no migration** (FR-063)
**Testing**: xUnit v3 on Microsoft.Testing.Platform; host executables run directly, because `dotnet test`
reports "Zero tests ran" on the development machine
**Target Platform**: Windows, Linux, macOS — all three in CI
**Project Type**: CLI tools plus two operator surfaces over a gRPC broker
**Performance Goals**: a project named while Parley runs is reachable within ten seconds (SC-010); the
poll costs N directory entries and N small reads every two seconds
**Constraints**: `Parley.Core` does no I/O and takes no package dependencies; `Parley.Server.Tests` opens
no `Broker` and no `SqliteConnection`; `Parley.Client`, `Parley.Terminal` and `Parley.Web` reach no store,
checked transitively by `GraphTests`; registration stays the adapter's own act on attach
**Scale/Scope**: a handful of projects per machine; a band of 256 ports; ~20 repositories enrolled
**Version**: **0.32.0**, moved in `Directory.Build.props` in the same change as the behaviour

### Unknowns resolved in Phase 0

| | Question | Where |
|---|---|---|
| R1 | How a port is claimed | [research.md](./research.md) |
| R2 | The directory watcher | same |
| R3 | The test rig conversion | same |
| R4 | What `--config` becomes | same |
| R5 | The command removals | same |
| R6 | The migration command | same |

No `NEEDS CLARIFICATION` remains.

## Constitution Check

*GATE: passed before Phase 0, re-checked after Phase 1.*

| Principle | Holds? | Why |
|---|---|---|
| **I. The specification is the product** | Yes, with work | SPEC.md §4.1 illustrates `projects.json` and must move with the code. This is a document change first. `Parley.Spec.Tests` executes every schema fragment, so the two cannot drift silently. |
| **II. No agent has authority over another** | Yes | Nothing here touches messaging, turns or conclusion. |
| **III. Identity is fixed by the launch** | Yes | 018's model is untouched: one catalogue per machine at the root, keyed by identity. Registration stays the adapter's own act (FR-060). |
| **IV. Terseness is a security control** | Yes | No message, payload or roster text changes. The `owns` length bound belongs to 020. |
| **V. Invariants live in storage** | **Argued** | Two invariants leave storage-shaped enforcement, and one arrives. See below. |

### Principle V, argued rather than assumed

`ProjectManifest.Collisions` enforces today, on read and on write, that no two projects share a name, a
store or an endpoint. The single manifest is what made that a whole-file check under one lock.

- **A shared store stops being expressible**, which is stronger than checking for it: the store is derived
  from the root and the name, and names are unique because they are directory entries. The invariant is
  enforced by the shape of the filesystem.
- **A shared name likewise.** Two directories cannot share a name.
- **A shared endpoint becomes a detection rather than a refusal** (FR-022). This is the one genuine
  loss: two projects can be hand-edited into claiming one port, and nothing refuses the write. It is
  reported when they are read together, naming both. Accepted because the alternative is an index file —
  which is what this feature deletes — and because the port record plus its reconciliation means the
  tooling will not *create* the state; only hand-editing can.
- **And one invariant arrives in storage**: a port cannot be handed out twice, enforced by an exclusive
  hold on the record rather than by code that could be routed around. That is Principle V applied to a
  place that previously had nothing.

### Post-Phase-1 re-check

Held. The two new files are read and written by `Parley.Configuration`, which is where `ProjectManifest`
already lives and which both a broker's tool and an agent's MCP server can reference without acquiring the
other's dependencies. `Parley.Core.ProjectSettings` keeps no path rules and does no I/O; what changes is
only where its values are read from.

## Project Structure

### Documentation (this feature)

```text
specs/019-one-way-in/
├── plan.md              # this file
├── spec.md              # 63 requirements, 8 user stories
├── research.md          # R1–R6, one decision each
├── data-model.md        # the layout and the two file shapes
├── contracts/
│   ├── layout.md        # the files, the claim, the move, every refusal
│   └── commands.md      # what stays, what goes, and the build constraint
├── quickstart.md        # the walk measuring the two doubtful claims
└── checklists/
    └── requirements.md
```

### Source code

```text
src/Parley.Core/
├── ProjectSettings.cs          NodeConfig removed; still no I/O, still no path rules
└── StoreFiles.cs               unchanged — the companion list the migration reads

src/Parley.Configuration/
├── ProjectManifest.cs          → reads a directory, not a file; Locate resolves the root
├── ProjectFolder.cs            NEW  one project's folder: where its files are
├── PortLedger.cs               NEW  the claim, the hold, the reconciliation
└── NodeCatalogue.cs            unchanged (018)

src/Parley.Setup/
├── ProjectCommand.cs           add loses --db/--listen/--node-config; migrate added
├── EnrollCommand.cs            three subcommands become one verb
└── ProjectResolution.cs        no store-by-path

src/Parley.Cli/
├── Program.cs                  sixteen verbs become stubs; --db and PARLEY_DB gone
├── ServeCommand.cs             reread lists a directory; serve loses its options
└── AutostartCommand.cs         entry loses its options; status reports the old form

tests/Parley.Cli.Tests/
├── BrokerProcess.cs            takes a root and a project name, not a store path
├── ToolProcess.cs              the clear-list loses PARLEY_DB
└── …                           26 files: 2 harness, 4 delete, 11 mechanical, 9 careful
                                see research.md R3 for the list and the split

plugins/parley/
├── skills/parley-operator/SKILL.md   loses sixteen verbs — or the build fails
└── commands/setup*.md                the enrol verb, and project add without paths

SPEC.md                         §4.1's illustration; §10.2 spelling sweep
Directory.Build.props            0.31.1 → 0.32.0
```

**Structure Decision**: no new projects. The two new readers go in `Parley.Configuration` for the reason
that project exists — two programs read this and cannot otherwise share one reader — and nothing there may
reach a store or the transport, which `GraphTests` already walks.

## Complexity Tracking

| Violation | Why needed | Simpler alternative rejected because |
|---|---|---|
| Endpoint uniqueness leaves write-time enforcement | FR-005 makes the directory listing the set of projects; there is no whole-file write to validate | An index file is what this feature deletes. Keeping one to hold this one check would reinstate the thing being removed, and the lost-add race with it |
| `--db` and `PARLEY_DB` removed, forcing a test-rig rewrite | FR-007 — a store outside a project is what let the layout drift | Keeping them "for testing only" is what the current mess is made of: an escape hatch documented as temporary becomes the ordinary path. Affordable only because a project in a temp root now costs one name |
| Sixteen verbs become stubs rather than vanishing | FR-042 — a script must detect the failure and a person must be told where the thing went | Deleting outright breaks a caller with `unknown command`, which is untrue and unhelpful |
| The test rig writes `project.json` directly instead of calling `project add` | The band is per machine and the root is per test, so parallel test classes would each be handed 19100 and the second broker would fail to bind | Coordinating a band per root is a second allocator to keep correct; and making every broker test depend on the allocator means an allocator bug fails the whole suite rather than the tests about allocation |
| `--db` keeps a read path in `InstalledEntry.Parse` | FR-054 — an entry written by a previous version must go on starting, and `autostart status` must be able to describe it | A hard cut stops a machine serving at its next logon, which is the one moment nobody is watching. 0.22.0 named this rule for `--console`: stop writing, stop accepting, keep reading |
