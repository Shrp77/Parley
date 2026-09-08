# Implementation Plan: The surfaces are their own tools

**Branch**: `007-split-terminal-tool` | **Date**: 2026-09-08 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/007-split-terminal-tool/spec.md`

## Summary

Ship the terminal and the web UI as their own tool packages, so changing either does not stop the
broker and end every agent conversation on the machine.

Four steps, each independently shippable. The first is a defect fix that stands alone: the terminal
reaches SQLite today through `NodeCatalogue` in `Parley.Headless`, and no check could catch it.

Decisions are in [research.md](./research.md). The short version:

- `NodeCatalogue` moves to `Parley.Configuration`. It uses only `System.Text.Json` and
  `Parley.Core`, so the move costs nothing.
- The broker starts the web UI as a child process, because it is the only option that keeps the
  bound address flowing on a command line.
- `ConsoleHost.LoopbackAddress` moves to `Parley.Configuration` so both tools read one constant.
- `parley console` and `--with-console` keep working for one minor version, because the owner's
  machine has that flag in a scheduled task right now.
- `GraphTests` walks five roots after this, one of which asserts the broker's tool cannot reach
  `Parley.Web`.

## Technical Context

**Language/Version**: C# / .NET 10 (`net10.0`), SDK band pinned by `global.json`
**Primary Dependencies**: Terminal.Gui 2.4.17 (terminal only), ASP.NET Core (web UI only), Grpc.Net,
Dapper + Microsoft.Data.Sqlite (broker only)
**Storage**: SQLite, one store per project, opened only by the broker process
**Testing**: xUnit v3 on Microsoft.Testing.Platform; host executables run directly
**Target Platform**: Windows, Linux, macOS. Headless Linux is a first-class target for this feature
**Project Type**: Multi-project solution producing .NET tool packages
**Performance Goals**: None new. The child-process launch must not delay broker startup
**Constraints**: Broker startup must not wait on the web UI; the broker's package must not carry
ASP.NET Core; no surface may reach a store
**Scale/Scope**: 2 new projects, 2 new tool packages, 1 type moved, 1 constant moved, ~5 files in
`install.ps1`. Judged against 21 agents in one project on the owner's machine

## Constitution Check

*GATE: passed before Phase 0. Re-checked after Phase 1 — see the second table.*

| Principle / boundary | Applies? | Assessment |
|---|---|---|
| I. The specification is the product | Yes | No protocol change. `SPEC.md` is untouched; this is packaging. §16 and §17 q15 are cited, not amended |
| II. No agent has authority over another | No | Nothing here touches agent-to-agent behaviour |
| III. Identity is fixed by the launch | Yes | Unchanged. The adapter and `PARLEY_AGENT` are out of scope |
| IV. Terseness is a security control | No | No message path changes |
| V. Invariants live in storage | Yes | No schema change. Migration 004 is the last |
| Only question and answer text crosses | No | No new disclosure |
| Agent and operator operations are different surfaces | Yes | **Strengthened.** The operator surfaces leave the broker's package entirely |
| Credentials are references | Yes | Unchanged |
| Transport is disposable | Yes | Unchanged |
| `Parley.Core` has no I/O | Yes | **Watch item.** `NodeCatalogue` moves to `Parley.Configuration`, not Core. Configuration may have I/O; Core may not |
| Green build is the lint gate | Yes | Two new projects inherit `Directory.Build.props`; docs and warnings-as-errors apply |
| Every claim the documents make is checked | Yes | FR-008/SC-006 require it. README, startup block, help text and plugin prose all name the surfaces |
| Probe; do not reason | Yes | R5: extend `GraphTests` and watch it fail on the existing leak **before** moving anything |
| Three consecutive clean runs per lens | Yes | Applies per shipped step, not once at the end |
| Public repository, no private traces | Yes | Placeholders stay `orders`, `billing`, `inventory` |

**No violations. Nothing to record in Complexity Tracking.**

Two gates deserve naming because this change could quietly breach them:

- **`Parley.Core` has no I/O.** `NodeCatalogue` reads a file. It must land in
  `Parley.Configuration`, which is allowed I/O, and not drift into Core because both are
  "low-level". R5 adds a walk from `Parley.Configuration` to keep it honest.
- **Agent and operator operations are different surfaces.** This makes the separation physical.
  After step 3, a machine can hold the broker and the adapter and have no operator surface
  installed at all.

## Project Structure

### Documentation (this feature)

```text
specs/007-split-terminal-tool/
├── plan.md              # This file
├── research.md          # Phase 0 — seven decisions
├── data-model.md        # Phase 1 — the tool set and what each carries
├── quickstart.md        # Phase 1 — how an operator installs and upgrades after this
├── contracts/
│   └── commands.md      # Phase 1 — the command surface each tool exposes
├── checklists/
│   └── requirements.md  # Spec quality checklist
└── tasks.md             # Phase 2 — /speckit-tasks, not created here
```

### Source Code (repository root)

```text
src/
├── Parley.Core/                 # unchanged. No I/O, no packages
├── Parley.Configuration/        # + NodeCatalogue.cs, + WebUiAddress
├── Parley.Transport/            # unchanged
├── Parley.Client/               # unchanged refs; AgentStanding's remark reworded
├── Parley.Store/                # unchanged
├── Parley.Broker/               # unchanged
├── Parley.Server/               # unchanged
├── Parley.Headless/             # − NodeCatalogue.cs. Keeps Store for AgentFleet
├── Parley.Web/                  # pages and ConsoleHost. Reads the address from Configuration
├── Parley.Cli/                  # − Top/, − ProjectReference to Parley.Web        → tool: parley
│   └── Top/                     #   moves out wholesale
├── Parley.Top/                  # NEW: the moved Top/                             → tool: parley-top
├── Parley.WebUi/                # NEW: thin host over Parley.Web                  → tool: parley-web-ui
└── Parley.Mcp/                  # untouched                                       → tool: parley-mcp

tests/
├── Parley.Cli.Tests/            # − Top/ tests
├── Parley.Top.Tests/            # NEW: the moved terminal suite
├── Parley.WebUi.Tests/          # NEW: hosting and the child-process launch
├── Parley.Client.Tests/         # GraphTests extended to five roots
└── ...                          # others unchanged

install.ps1                      # tool table grows to four; -Surfaces selects
```

**Where the terminal's tests go.** `tests/Parley.Cli.Tests/Top/` moves to `Parley.Top.Tests`. It is
the largest test project in the repository and it tests a thing that will no longer be in
`Parley.Cli`. `Driving`, `Walking` and `Drawn` move with it; `Loopback` and `ToolProcess` are shared
with the CLI suite and stay, with the terminal suite referencing them.

## Complexity Tracking

No constitutional violations. This section is retained empty deliberately: the feature *removes*
coupling rather than adding any, and the two new projects are thinner than the code they take out of
`Parley.Cli`.

One cost is worth stating without calling it a violation: an operator now has up to four tools
rather than two. That is the point of the feature, and FR-008's wrapper keeps the everyday
experience at one command.

## Phase 1 — post-design constitution re-check

| Concern raised in design | Resolution |
|---|---|
| `Parley.Configuration` becomes referenced by every tool; a store or socket added there would poison all four | R5 adds a `GraphTests` walk from it, asserting no store and no transport |
| The broker launching a child process could grow into supervising it | R2 scopes it: start, do not wait, do not restart. The child ends when the broker does |
| The address constant could be duplicated across packages | R3 moves it to `Parley.Configuration`. `ConsoleAddressTests` already exists for exactly this defect and is extended |
| Renaming could spread to `OperatorConsole` and break §10.2's vocabulary | FR-019 names the three meanings and forbids two of them changing. A test asserts the two survive |
| The moved terminal suite could lose coverage in the move | Test count before and after must match, and the review gate runs per step |

**Still passing. No new violations.**
