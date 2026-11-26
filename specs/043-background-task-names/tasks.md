# Tasks: Named background tasks

**Input**: `specs/043-background-task-names/` (plan.md, spec.md, research.md, data-model.md, contracts/)
**Tests**: required. This repository builds every behaviour test first, and a guard is watched failing before it holds.

Stages follow the plan's build order. **Each stage ends with a gate**: a build, the stage's tests, and three reviewers
(correctness and concurrency, security and data integrity, API surface and maintainability) on the stage's diff, with
their findings fixed before the next stage begins.

## Phase 1: Setup

- [x] T001 Bump `VersionPrefix` to 0.65.0 in Directory.Build.props and `ProtocolVersion.Current` to "0.21" in src/Parley.Core/ProtocolVersion.cs

## Phase 2: Foundational (stage 1, Core and wire)

- [x] T002 [P] Write tests for the bounds, neutralising and link shapes (name 64, description 300, link 500; `exchange #N`, `#N`, `conversation:<id>`, `file:<name>`, `https` only with a host and no user info; everything else dropped) in tests/Parley.Core.Tests/BackgroundTaskTextTests.cs
- [x] T003 [P] Add `TaskKind`, `TaskLink` and `BackgroundTask` records in src/Parley.Core/BackgroundTask.cs (data-model.md)
- [x] T004 Add `BackgroundTaskText` (Name, Description, Link parse into `TaskLink`, Id grammar, Neutralise) in src/Parley.Core/BackgroundTaskText.cs, making T002 pass
- [x] T005 Add `Tasks` (`IReadOnlyList<BackgroundTask>?`, null when none, read through `Named`) to `TurnStanding` in src/Parley.Core/TurnStanding.cs
- [x] T006 Add `RunningTask`, `TaskKind`, `BackgroundWork.running`, `DescribeBackgroundTask` (request, reply), `BackgroundTaskRow` and `RegistryRow.running_tasks` to proto/parley.proto (contracts/wire.md)
- [x] T007 **Gate 1**: build, run every unit test (`pwsh scripts/test.ps1`), three-lens review of the stage's diff, fix findings

## Phase 3: User Story 1, the person sees what each task is doing (stage 2, node to broker) 🎯 MVP

**Goal**: the runner's own labels reach the person's registry rows.
**Independent test**: a resident fed recorded `background_tasks_changed` lines reports each task; the operator registry lists them by name; they clear with the count.

- [x] T008 [P] [US1] Write tests reading the recorded lines of research R1 (ids, kinds, labels; an entry with a bad id counted and not named; more than 200 entries counted and listed to 200; a label of 10,000 characters cut) in tests/Parley.Headless.Tests/RunnerBackgroundTasksTests.cs
- [x] T009 [US1] Read `tasks[].task_id`, `task_type` and `description` in `RunnerEvent.Read`, keep `RunningTasks` beside the count, and notify `BackgroundChanged` with the list whenever the ids or labels change, in src/Parley.Headless/ResidentAgent.cs
- [x] T010 [US1] Send the list with the count in `BackgroundWork.running`, and on a rejoin (`ReannounceAsync`), in src/Parley.Cli/TurnReporter.cs
- [x] T011 [P] [US1] Write whole-exchange tests over loopback: a node's report shows on the registry row by name; a report from another machine stores nothing; a node leaving and a new launch clear the names with the count; the agent roster carries none, in tests/Parley.Server.Tests/BackgroundTaskNamesTests.cs
- [x] T012 [US1] Hold each agent's running list beside the count in `BrokerRuntime` (`BackgroundReportedAsync` takes the list, re-checks every entry through `BackgroundTaskText`, clears it wherever `_background` is cleared), and expose `RunningTasks(agent)` in src/Parley.Server/BrokerRuntime.cs
- [x] T013 [US1] Pass `request.Background.Running` through in src/Parley.Server/NodesService.cs
- [x] T014 [US1] Fill `RegistryRow.running_tasks` in src/Parley.Server/OperatorsService.cs and src/Parley.Server/Wire.cs
- [x] T015 [US1] Read `running_tasks` into `TurnStanding.Tasks` in src/Parley.Client/BrokerClient.cs
- [x] T016 **Gate 2**: build, Headless, Server, Cli and Client tests, three-lens review, fix findings

## Phase 4: User Story 2, each task says plainly what it is doing, with a link (stage 3, agent operation and tool)

**Goal**: an agent names, describes and links its own tasks.
**Independent test**: an agent describes a task by id; the row shows the agent's name, description and link once the node lists the id, in either order; a bad link is dropped and the reply says so.

- [x] T017 [P] [US2] Write tests: describe before and after the node's report; another agent's id is never touched (descriptions keyed by caller); a 201st id drops the oldest; a bad task id or empty name refused; a bad link dropped with `link_dropped`; descriptions cleared with the count, in tests/Parley.Server.Tests/DescribingBackgroundTasksTests.cs
- [x] T018 [US2] Hold descriptions keyed by (agent, task id), at most 200 per agent, merged into `RunningTasks` (agent's name over the runner's label), cleared with the count, in src/Parley.Server/BrokerRuntime.cs
- [x] T019 [US2] Add `DescribeBackgroundTask` to src/Parley.Server/AgentsService.cs, keyed by the attached caller and answering with no turn fact
- [x] T020 [P] [US2] Extend tests/Parley.Server.Tests/AgentsCannotSeeTurnsTests.cs (or the test holding it) to the new reply, declare the operation in tests/Parley.Server.Tests/TheAgentsServiceIsDeclaredTests.cs, pin the reply's refusal texts so none states a turn fact, and confirm `AgentsCannotSetTheirOwnLineTests` and `SurfaceBoundaryTests` still pass unchanged
- [x] T021 [P] [US2] Write tool tests (parameters, the result shapes of contracts/mcp-tool.md, the description's wording) in tests/Parley.Mcp.Tests/DescribeBackgroundToolTests.cs
- [x] T022 [US2] Add `parley_describe_background` to src/Parley.Mcp/ParleyTools.cs and its client call, and the server instructions line (contracts/mcp-tool.md)
- [x] T023 **Gate 3**: build, Server and Mcp tests, three-lens review, fix findings

## Phase 5: User Story 1 and 2 on the web console (stage 4)

**Goal**: the graph and the agent's page show names, descriptions and links, paged.
**Independent test**: with a scripted registry, the graph draws named task boxes with links, five then "N more" linked to the agent's page; the agent's page lists all with description and link, twenty to a page.

- [x] T024 [P] [US1] Write tests: named task boxes, tooltip with description, link kinds drawn as console links or https with host and `rel="noopener noreferrer"`, five then a "more" box linking to the agent's page, in tests/Parley.Client.Tests/WorkNowTests.cs and tests/Parley.Web.Tests/BackgroundWorkOnTheConsoleTests.cs
- [x] T025 [US1] Carry `BackgroundTask` items on `WorkingAgent`, draw five task boxes then `MoreTasks`, in src/Parley.Client/Views/WorkNow.cs
- [x] T026 [US1] Draw each task's name, its description in the tooltip and its link, and the "more" box as a link to the agent's page, in src/Parley.Web/Pages/Graph.cshtml; list them in the graph's text list
- [x] T027 [P] [US2] Write tests for the agent page's *Background work* list: names, descriptions, links, a scrolling list after five, page 2 of 3 with 47 tasks, a page past the end shows the last, in tests/Parley.Web.Tests/BackgroundWorkOnTheConsoleTests.cs
- [x] T028 [US2] Add the *Background work* section to src/Parley.Web/Pages/Projects/Agent.cshtml and its `tasks` page (`[BindProperty(SupportsGet = true, Name = "tasks")]`) in Agent.cshtml.cs, styles in src/Parley.Web/Brand/components.css
- [x] T029 **Gate 4**: build, Web and Client tests, three-lens review, fix findings

## Phase 6: User Story 1 and 2 in the terminal (stage 5)

**Goal**: the terminal's agent row names the first task, and a read-only list shows them all.
**Independent test**: on the drawn screen, the row reads "waiting on 3 background tasks — Checking VAT…, +2"; *Background work* opens a list of all three, scrolling, with Page Up and Page Down.

- [ ] T030 [P] [US1] Write drawn-screen tests in tests/Parley.Terminal.Tests/BackgroundWorkTests.cs
- [ ] T031 [US1] Append the first task's name and how many more to the row in src/Parley.Terminal/AgentsRegion.cs, neutralised with `Safe.Text`
- [ ] T032 [US2] Add `BackgroundWorkDialog` (a `TableDialog<T>`, its key table listed in `CommandTables`, a `ListView` made with `HotKeySpecifier = Plain.NoHotKey`) in src/Parley.Terminal/BackgroundWorkDialog.cs, and its entry in the agent's row menu
- [ ] T033 **Gate 5**: build, Terminal unit tests (including `CommandTableTests`, `KeysTakenBeforeTheTableTests`, `DriftTests`), three-lens review, fix findings

## Phase 7: User Story 3 and 4 hold across the stack; spec and docs (stage 6)

- [ ] T034 [US3] Confirm by test that no agent operation's reply or roster row carries a task name, count or description (extend the boundary scans if a field could slip through) in tests/Parley.Server.Tests
- [ ] T035 [US4] Confirm by test that names leave with the count on node leaving, relaunch and broker restart (covered by T011 and T017; add any missing case)
- [x] T036 Update SPEC.md §10.1 (`describe_background_task`), §10.2 (`registry()` carries running tasks) and §11.6 (the node's list, the broker's holding and clearing, bounds, link shapes), and any Spec.Tests that pin them
- [ ] T037 [P] Update docs/operating.md (what the person sees), docs/concepts.md (the tool list) and plugins/parley README (what an agent is told)
- [ ] T038 Take a screenshot round `043-background-tasks` with named tasks in tools/Parley.Screens/rounds/043-background-tasks/ and a review page
- [ ] T039 **Gate 6**: full build, `pwsh scripts/test.ps1 -Category Smoke`, hygiene grep, three-lens review of the whole feature, fix findings

## Dependencies

- Phase 1 and 2 before everything.
- Stage 2 (US1) before stage 3 (US2): descriptions merge into the running list.
- Stages 4 and 5 need stages 2 and 3. They are independent of each other.
- Stage 6 last.

## Parallel opportunities

- T002 and T003; T008 and T011; T017, T020 and T021; T024 and T027; the docs in T037.

## Implementation strategy

MVP is stage 2: the runner's own labels on the registry row, which the graph already draws as names once T025 lands.
Each later stage adds one surface or one source of words, and is reviewed before the next begins.
