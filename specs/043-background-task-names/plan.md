# Implementation Plan: Named background tasks

**Branch**: `043-background-task-names` (on top of `042-theme-and-layout-refresh`) | **Date**: 2026-11-26 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `specs/043-background-task-names/spec.md`

## Summary

Each background task an agent runs is shown to the person by name, with a description and a link where the agent gives
them, instead of "Background task 1, 2". From [research.md](research.md):

1. **The node reads each task from the runner** (R1). Claude Code's `background_tasks_changed` already lists every
   running task with its id, its kind and the agent's short description. The node sends that list with the count it
   already sends.
2. **The agent describes its own task** (R2), by a new agent operation and a `parley_describe_background` tool: a name
   of up to 64 characters, a description of up to 300 and a link. The person chose this (exchange #58).
3. **The broker merges and holds both in memory** (R3), beside the count, shown only while the node lists the task, and
   cleared with the count. It neutralises and bounds every word (R4) and keeps only links of four kinds (R5).
4. **Surfaces show them** (R6): the graph's task boxes, the agent's page (a list that scrolls after five, twenty to a
   page) and the terminal's agent row and a read-only list.
5. **Protocol 0.21, version 0.65.0** (R7). Nothing is stored.

## Technical Context

**Language/Version**: C# 14 on .NET 10; Razor Pages; Terminal.Gui v2; protobuf over gRPC
**Primary Dependencies**: existing only (Grpc, ModelContextProtocol, Terminal.Gui, Razor). No new package.
**Storage**: none. In-memory in the node and the broker; no migration.
**Testing**: xUnit v3 on MTP. Headless.Tests (runner lines), Server.Tests (whole exchanges over loopback), Client.Tests
(WorkNow, standings), Web.Tests (pages), Terminal.Tests (drawn screen), Mcp.Tests (tool), Spec.Tests, the surface
boundary tests (`AgentsCannotSeeTurnsTests`, `SurfaceBoundaryTests`, `DriftTests`).
**Target Platform**: Windows, Linux, macOS (CI matrix).
**Project Type**: distributed tool: broker, node, adapter, web console, terminal.
**Performance Goals**: a task appears within 5 seconds of starting (SC-002); a report is no larger than 200 entries.
**Constraints**: agents never see turn information; nothing times out; every word neutralised in one place; no network
bind; nothing persisted.
**Scale/Scope**: a handful of tasks per agent usually; bounded at 200 listed and 200 described per agent.

## Constitution Check

| Principle or boundary | How this feature stands |
|---|---|
| I. The specification is the product | SPEC.md §10.1 (`describe_background`), §10.2 (registry row) and §11.6 (the node's report, the broker's holding) change in the same change (FR-009). |
| II. No agent has authority over another | Held. An agent describes only its own tasks, to the person. It reads no other agent's. |
| III. Identity is fixed by the launch | Held. The broker keys descriptions by the attached caller's name, never by a name in the request. |
| IV. Terseness is a security control | Held. Name 64, description 300, link 500, at most 200 per agent, neutralised in one place (`BackgroundTaskText`). |
| V. Invariants live in storage | Not applicable: nothing is stored, as the count today. Recorded as in-memory by design, like running turns. |
| Agent and operator surfaces apart | New operation on `Agents`; its reply carries no turn fact. Task names reach only `Operators` rows. `AgentsCannotSeeTurnsTests` and `SurfaceBoundaryTests` are extended. |
| Message payloads are inert | Names, descriptions and links are never parsed for meaning. A link is matched against four fixed shapes and otherwise dropped. |
| Anything drawing somebody else's text neutralises it | The broker neutralises; every surface encodes on output, one line, as today. |
| Every operator operation on both surfaces | No new operator operation. Both surfaces show the new row fields (FR-007). |
| Nothing times out | Held. Descriptions go when the count clears, not on a timer. |

No violations.

## Project Structure

### Documentation (this feature)

```text
specs/043-background-task-names/
├── spec.md
├── plan.md            # this file
├── research.md        # R1 to R7
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── wire.md        # proto changes
│   └── mcp-tool.md    # parley_describe_background
└── tasks.md           # next: /speckit-tasks
```

### Source code touched

```text
proto/parley.proto                         BackgroundWork.running, RunningTask, TaskKind, DescribeBackgroundTask, RegistryRow.running_tasks
src/Parley.Core/BackgroundTask.cs          new: BackgroundTask, TaskKind, TaskLink
src/Parley.Core/BackgroundTaskText.cs      new: bounds, neutralising, link shapes
src/Parley.Core/TurnStanding.cs            Tasks list beside BackgroundTasks
src/Parley.Core/ProtocolVersion.cs         0.21
src/Parley.Headless/ResidentAgent.cs       RunnerEvent reads tasks; RunningTasks; BackgroundChanged carries the list
src/Parley.Cli/TurnReporter.cs             sends the list with the count, and on a rejoin
src/Parley.Server/BrokerRuntime.cs         holds running lists and descriptions; merges; clears with the count
src/Parley.Server/NodesService.cs          passes the list
src/Parley.Server/AgentsService.cs         DescribeBackgroundTask
src/Parley.Server/OperatorsService.cs, Wire.cs   registry row
src/Parley.Client/BrokerClient.cs          reads the row into TurnStanding.Tasks
src/Parley.Client/Views/WorkNow.cs         named task boxes, five then "more", link
src/Parley.Mcp/ParleyTools.cs              parley_describe_background; server instructions line
src/Parley.Web/Pages/Graph.cshtml          task names, tooltip, link
src/Parley.Web/Pages/Projects/Agent.cshtml background work list, paged
src/Parley.Terminal/AgentsRegion.cs        first task's name on the row
src/Parley.Terminal/BackgroundWorkView.cs  new: read-only list, a TableDialog, opened by Alt+B
SPEC.md                                    §10.1, §10.2, §11.6
docs/operating.md, plugins/parley README   what the person sees; what an agent is told
```

**Structure Decision**: no new project. Core gains two small types with no I/O. Every other change sits in the project
that already owns that part of the count's path.

## Build order and review points

Each stage ends with a build, its tests, and three reviewers (correctness and concurrency, security and data integrity,
API surface and maintainability) on that stage's diff, findings fixed before the next stage.

1. **Core and wire**: types, text rules, proto, protocol version.
2. **Node to broker**: the runner's list read, reported, held, merged, cleared; registry row; client reading.
3. **Agent operation and tool**: `DescribeBackgroundTask`, `parley_describe_background`, boundary tests.
4. **Web console**: graph and agent page.
5. **Terminal**: row and list.
6. **SPEC.md and docs**, then a screenshot round and the whole suite.

## Complexity Tracking

None.
