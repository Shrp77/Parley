# Implementation Plan: Machine-level project manifest

**Branch**: `002-project-manifest` | **Date**: 2026-08-28 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/002-project-manifest/spec.md`

## Summary

One machine can already host several independent Parley projects — SPEC.md §4.1 says so, and the
code allows it — but nothing records what they are. Each path is supplied separately, and the node
catalogue falls back to the working directory, so nothing is in a position to notice when the
three disagree. This adds a per-user manifest at a known location listing named projects, each
carrying a store, an endpoint and a node catalogue; commands take a project by name and derive
every path from it.

The approach is additive at every point: the manifest is consulted only where the previous default
would have applied, so a machine without one behaves exactly as it does today. The protocol is not
touched. Nothing about a project crosses the wire, no schema changes, and identity still comes from
the launch environment — a project supplies where to connect, never who is connecting.

## Technical Context

**Language/Version**: C# / .NET 10
**Primary Dependencies**: none new. `System.Text.Json` is in the runtime; a YAML parser was
rejected in [research.md](./research.md) R2 rather than added.
**Storage**: no change. The manifest is a configuration file, not a store; no schema migration,
no `user_version` change.
**Testing**: xUnit v3 on Microsoft.Testing.Platform, as the rest of the repository. New suites in
`tests/Parley.Cli.Tests`; path-resolution assertions must run on all three CI platforms.
**Target Platform**: Windows, Linux, macOS — the existing CI matrix, which is what verifies R1's
unresolved per-platform path mapping.
**Project Type**: CLI plus a long-running host process; the manifest is read by `Parley.Cli` and
by the MCP adapter, and written only by `Parley.Cli`.
**Performance Goals**: none meaningful. Reading one small file at startup; the listing's
reachability probe is a connection attempt per project and must be bounded so a dead project does
not stall the report.
**Constraints**: no wire or schema change; no new package; existing deployments keep working with
no manifest and no migration; a manifest that cannot be read is refused rather than treated as
empty.
**Scale/Scope**: single machine, a handful of projects. Three of a person's own is the realistic
upper bound; the design should not fall over at twenty but need not be tuned for it.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked after Phase 1 design.*

| Principle | Verdict | Note |
|---|---|---|
| I — The specification is the product | **Pass, with an obligation** | SPEC.md §4.1 illustrates this as `parley.yaml`; the plan uses JSON. That is a code-and-spec disagreement unless §4.1 is amended **in the same change**. Recorded as a task, not left to a follow-up. |
| II — No agent has authority over another | **Pass** | Projects partition deployments; they introduce no relationship between agents. Two agents in one project are exactly as related as before. |
| III — Identity is fixed by the launch | **Pass** | A project supplies an endpoint, never a name or an `owns` line. `PARLEY_AGENT` and `PARLEY_OWNS` still come from the launch environment and no operation accepts either. |
| IV — Terseness is a security control | **Not engaged** | Nothing new crosses between agents. The manifest is read by one operator's tools on one machine and is never returned by an operation. |
| V — Invariants live in storage | **Pass, by exception argued** | The manifest's invariants — unique name, no shared store, no shared endpoint — cannot live in the schema, because the file is deliberately outside the database the broker owns. They are enforced on write and on read, which is the strongest available placement, and each gets a test. Noted in Complexity Tracking. |
| Only question and answer text crosses | **Pass** | No operation is added or changed. |
| Agent and operator surfaces are separate | **Pass** | `project add/remove/list` are operator operations on `Parley.Cli`, which the MCP server does not reference. The adapter *reads* the manifest to resolve an endpoint; it gains no ability to write one. |
| Credentials are references | **Pass** | Paths and endpoints only. This is what keeps a well-known location safe. |
| `Parley.Core` has no I/O | **Pass, and load-bearing** | The manifest's *shape* and its validation rules are domain; reading the file is not. Parsing and locating live in `Parley.Cli`; anything shared with the adapter goes in Core as pure types with no file access. |
| A green build is the whole lint gate | **Pass** | No new package, so no new advisory surface; documentation on every public member as usual. |
| Every claim the documents make is checked | **Pass, with an obligation** | The §4.1 amendment must keep `Parley.Spec.Tests` green, and the quickstart is documentation that tells someone what to type, so it is held to the same standard. |
| Probe; do not reason | **Applied in Phase 0** | R1 verified on Windows and explicitly left open elsewhere; R3 verified against `ParleyServer.StartAsync` rather than assumed, which is what surfaced the console-lifetime risk. |
| Review runs to convergence | **Deferred to implementation** | Three lenses after the last edit, repeated until a full rotation finds nothing above low severity. |
| No trace of any originating private project | **Pass, with a caution** | Placeholders are `orders`, `billing`, `inventory`. Worth stating because the enrollment change nearly shipped two real names, and `git grep` did not catch them: it searches tracked files only, and a new feature's files are untracked until the first commit. |

**Gate: PASS.** One obligation (amend §4.1) and one argued exception (Complexity Tracking).

## Project Structure

### Documentation (this feature)

```text
specs/002-project-manifest/
├── spec.md              # written by /speckit-specify
├── research.md          # Phase 0 — R1..R7
├── plan.md              # this file
├── data-model.md        # Phase 1
├── quickstart.md        # Phase 1
├── contracts/
│   ├── manifest.md      # the file's shape and its rules
│   └── commands.md      # the command surface and its exit codes
└── checklists/
    └── requirements.md
```

### Source Code (repository root)

```text
src/Parley.Core/
├── ProjectName.cs             # NEW — a validated name, no I/O
└── ProjectSettings.cs         # NEW — one project's values as a pure type

src/Parley.Cli/
├── ProjectManifest.cs         # NEW — locate, read, validate, write
├── ProjectCommand.cs          # NEW — add / remove / list
├── ProjectResolution.cs       # NEW — precedence: argument, variable, project, default
├── Program.cs                 # CHANGED — --project and --config; resolution before dispatch
└── ServeCommand.cs            # CHANGED — start one broker per project, contained

src/Parley.Mcp/
└── (adapter startup)          # CHANGED — resolve PARLEY_PROJECT to an endpoint, or refuse

tests/Parley.Core.Tests/
└── ProjectNameTests.cs        # NEW

tests/Parley.Cli.Tests/
├── ProjectManifestTests.cs    # NEW — shape, validation, refusals, round-trip
├── ProjectResolutionTests.cs  # NEW — precedence, and the no-manifest path
├── MultiProjectServeTests.cs  # NEW — partial start, containment, one interrupt stops all
└── ProjectCommandTests.cs     # NEW — add/remove/list, and removal deleting nothing

tests/Parley.Mcp.Tests/
└── ProjectReferenceTests.cs   # NEW — endpoint wins; unknown project refuses, never falls back

SPEC.md                        # CHANGED — §4.1 illustration: parley.yaml -> projects.json
```

**Structure decision**: the split follows the existing rule rather than inventing one. A project's
*shape* and its validation are domain and go in `Parley.Core` as pure types with no file access,
so the adapter and the CLI cannot disagree about what a valid project is. *Locating and reading*
the file is I/O and stays in `Parley.Cli`; the adapter needs only the read path and gets it
without any ability to write. `ProjectResolution` is separated from `ProjectManifest` for the same
reason `AutostartPlan` is separated from `AutostartCommand` and `EnrollmentPlan` from
`EnrollCommand`: precedence is a pure function of four inputs and is where the defects will be, so
it must be testable without a filesystem.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|---|---|---|
| Manifest invariants enforced in code, not in storage (constitution V) | The manifest is deliberately outside the database — it names the store, so it cannot live in it. Uniqueness of name, store and endpoint has nowhere else to go. | Putting projects in a database needs a database to find first, which is the problem this solves. Enforcement is therefore on write *and* on read, so a hand-edited file is refused with the same rule that `add` applies, and each rule carries a test. |
| Three new types in `Parley.Cli` where one class would compile | Locating, validating and resolving fail differently and are tested differently; resolution in particular must be a pure function to be testable at all. | One class mixing filesystem access with precedence logic could only be tested through the filesystem — the shape that left `EnrollCommand` untested until a data-destroying defect was found by hand. |
