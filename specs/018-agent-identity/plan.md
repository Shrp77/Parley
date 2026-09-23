# Implementation Plan: An agent is an identity, not a name

**Branch**: `015-file-store` | **Date**: 2026-09-22 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/018-agent-identity/spec.md`

## Summary

An agent becomes a stable identifier; a name becomes a project's label for it. One catalogue per machine,
keyed by that identifier, holding a command and a workspace. The node learns which names an identifier answers
to by reading each catalogued workspace's own launch configuration, announces those names per project,
resolves a requested name back to an identifier when starting, and keys running processes by identifier across
every project one process serves.

**The technical approach is subtraction.** Nothing new crosses the wire and nothing new is stored: the
catalogue's key changes, the file becomes one per machine, and a lookup that was `role → entry` becomes
`name → identifier → entry` with the first hop read from the workspace. What that removes is the ability to
represent the defect, plus the gate, the scan and the deviation that existed to police it.

## Technical Context

**Language/Version**: C# 13 / .NET 10
**Primary Dependencies**: no new ones. `System.Text.Json` for both files; `Guid` for the identifier
**Storage**: none new. The catalogue and the launch configuration are files; the registry is untouched
**Testing**: xUnit v3 on Microsoft.Testing.Platform, run from each project's own host executable
**Target Platform**: Windows and Linux (CI runs both; macOS in the matrix)
**Project Type**: multi-project solution — the change lands in `Parley.Configuration`, `Parley.Cli` and
`Parley.Setup`, with surfaces in `Parley.Terminal` and `Parley.Web`
**Performance Goals**: a node reads N small files at join and on a catalogue change, N being catalogued
workspaces on this machine — tens, not thousands
**Constraints**: no wire change to message addressing; no schema change; no migration
**Scale/Scope**: one machine, tens of checkouts, a handful of projects

## Constitution Check

*GATE: passed before Phase 0, re-checked after Phase 1.*

**III. Identity is fixed by the launch, never by a call — the one that matters, and it holds.**
The identifier is not an identity claim and never travels in a call. An agent's *name* still comes from the
environment its server was started with, and registration is still the adapter's own act on attach (§12.3).
What the identifier does is answer "what does this machine start", which is a question about a launch, not
about a caller. Nothing gains a sender parameter. The test: if the node started the wrong checkout for a
requested name, the adapter that attaches registers what *its* environment says, so the request stays
unsatisfied — a visible failure, never a forged identity.

**V. Invariants live in storage — and this is a deviation, argued rather than assumed.**
"One identifier, one entry" is enforced by a JSON object's key, not by a database constraint. The catalogue is
not the store and must not become it: §4 gives the store to one process, and a node that opened it would break
the boundary the architecture rests on. The honest statement is that this *reduces* the deviation rather than
adding one — 009 recorded a deviation from this principle for the machine-wide enrolment gate, and that gate
is deleted here. What replaces it is a key that cannot repeat.

**I. The specification is the product.** SPEC.md §12 describes the node and its catalogue. The catalogue's key
and scope are described there and must change with the code, in the same change. §4.1's manifest illustration
loses its per-project catalogue binding.

**IV. Terseness is a security control.** Nothing new crosses between agents. The identifier is machine-side;
the agent-facing roster is untouched.

**II. No agent has authority over another.** Untouched. Starting an agent remains an operator operation and a
consequence of being addressed, never an instruction from a peer.

**Boundaries**: `Parley.Core` gains nothing. The node still opens no store. Operator and agent surfaces are
unchanged.

## Project Structure

### Documentation (this feature)

```text
specs/018-agent-identity/
├── plan.md              # This file
├── research.md          # Phase 0: the eight decisions
├── data-model.md        # Phase 1: the two files and the resident table
├── quickstart.md        # Phase 1: the walk
├── contracts/
│   └── catalogue.md     # Phase 1: the file shapes, and what the node announces and starts
├── checklists/
│   └── requirements.md  # From /speckit-specify
└── tasks.md             # Phase 2, from /speckit-tasks
```

### Source Code (repository root)

```text
src/Parley.Core/
└── AgentIdentity.cs        # new: what an identifier is, and how one is minted and read. **Core, not
                            # Configuration as this plan first said** — it is vocabulary, it has no I/O and
                            # no dependencies, and it sits with AgentName and MachineName, which is also
                            # where its tests can reach it

src/Parley.Configuration/
└── NodeCatalogue.cs        # keyed by identifier; gains the workspace-side name lookup

src/Parley.Setup/
├── Enrolment.cs            # mints the identifier, writes one catalogue entry, adds membership
├── Catalogues.cs           # the machine-wide gate and cross-project scan: deleted
└── LaunchEntries.cs        # reads and writes the per-project entries and the identifier

src/Parley.Cli/
├── NodeCommand.cs          # announces names per project; resolves name to identifier on start
├── Program.cs              # one catalogue per machine rather than one per project
└── ServeCommand.cs         # the resident table is shared across the projects one process hosts

src/Parley.Configuration/ProjectManifest.cs   # the per-project catalogue binding goes

tests/Parley.Cli.Tests/          # the node: announcing, resolving, one process per identifier
tests/Parley.Core.Tests/         # the identifier itself
tests/Parley.Terminal.Tests/     # what a person is told about an agent this machine cannot start
```

**Structure decision**: the change is confined to the three assemblies that already own the catalogue, the
node and enrolment. Nothing moves between projects, and no project gains a reference. `Parley.Configuration`
is where both file readers live because two programs read them and cannot otherwise share one reader — the
same argument that put the manifest there.

## Phases

### Phase 0 — Research

`research.md`. Eight decisions, each with what was rejected: what the identifier is and how it is spelled;
where it is written and what that means for a repository checked out twice; how the node learns a name for a
project; what happens when two workspaces claim one name; how residents are shared without a static; what
becomes of the manifest's catalogue binding; what enrolment stops doing; and how a person is told about an
agent this machine cannot start.

### Phase 1 — Design

`data-model.md` — the catalogue, the membership already in each repository, and the resident table.
`contracts/catalogue.md` — the two file shapes, what the node announces for a project, what a start resolves
to, and every refusal. `quickstart.md` — the walk: enrol one checkout in two projects, ask it from both.

### Phase 2 — Tasks

`/speckit-tasks`, organised by the four user stories so P1 is a working slice on its own.

## Complexity Tracking

No constitutional violation requires justification. The one deviation (Principle V) is pre-existing, is
reduced rather than added to, and is argued in the Constitution Check above.
