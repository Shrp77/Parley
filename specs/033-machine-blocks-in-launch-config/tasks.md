# Tasks: One launch configuration for every machine

**Input**: [spec.md](spec.md), [plan.md](plan.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/launch-configuration.md](contracts/launch-configuration.md), [quickstart.md](quickstart.md)

**Tests**: Included. `plan.md` sets the testing rules, and the owner asked for a mutation check on each new guard.

**Format**: `[P]` can run in parallel (different files, nothing unfinished in its way). `[US1]` to `[US5]` are the spec's user stories.

**Rules for every task**

- Read `SPEC.md` before changing protocol behaviour. If code and spec disagree, stop and ask which is right.
- Every test class is `[Unit]` or `[Integration]`, and passes `TestContext.Current.CancellationToken` to awaited calls.
- A test that starts a tool clears every `PARLEY_` variable, runs from a folder in no repository, and passes paths in as seams. Machine names reach child processes through `PARLEY_MACHINE`.
- Probes bind loopback only. Never touch the live deployment or `.claude/outbox/`, and never touch `specs/027-session-owned-delivery/`.
- **Mutation checks:** copy the file with `mktemp` first, never `git checkout` to restore, use a change that compiles, `touch` the restored file before rebuilding, and confirm with `git diff --stat` that it is gone. A mutation that does not compile proves nothing.
- Use `Edit` or `Write` on C#, never scripted regex.

## Phase 1: Setup

- [ ] T001 Bump `VersionPrefix` to 0.53.0 in `Directory.Build.props`, and record protocol 0.13 at the top of `SPEC.md`
- [ ] T002 Update the State section of `CLAUDE.md`: version 0.53.0, protocol 0.13, schema still 6, feature 033 listed, and 007 still reserved for feature 027

## Phase 2: Foundational

**Goal**: The spec says what will be built, and one reader computes a machine's effective settings. Nothing uses the reader yet. No story can start until this is done.

### The specification first

- [ ] T003 Rewrite the launch configuration part of `SPEC.md` §4.1.1 and §13: the `parleyMachines` section, its two allowed settings, an empty block's meaning, precedence (block, then environment or `env`), and that the workspace is the folder the file was found in when the file has a section (`research.md` R2, R3, R4, R4a)
- [ ] T004 Add the infrastructure example to `SPEC.md` §4.1.1, with the statement that an ownership line describes an instance and does not limit it, and that the real limit is that machine's permissions (FR-021)
- [ ] T005 State in `SPEC.md` §4.1 and §12 the exception for `enroll` and `up`: a project this machine does not describe is accepted with an explicit address once the broker confirms it serves that project (`research.md` R7). State the agent-only machine and the node autostart entry in `SPEC.md` §12 (`research.md` R11)
- [ ] T006 Add to `SPEC.md` §10.1 and the refusals table in §7 the `Attach` refusal for `expected_project`, and to §11 the `Join` refusal for another protocol version. Add `project` on `Nodes.Describe`. Run `tests/Parley.Spec.Tests` and fix what it names

### The wire

- [ ] T007 Add `expected_project` to `AttachRequest`, `project` to `DescribeNodeReply` and `protocol_version` to `JoinRequest`, with comments, in `proto/parley.proto`

### The reader

- [ ] T008 Write the shared test table `tests/LaunchTable.cs`, linked into `tests/Parley.Mcp.Tests/Parley.Mcp.Tests.csproj` and `tests/Parley.Cli.Tests/Parley.Cli.Tests.csproj`: file text, machine, key, environment, and expected owns, broker, whether the broker came from a block, or the problem. Cover: no section, block for this machine, block for another machine only, empty block, two projects, a value in both block and `env`, every invalid case in `data-model.md`
- [ ] T009 Write failing tests over that table in `tests/Parley.Cli.Tests/LaunchSettingsTests.cs`, including that reading writes nothing (SC-004) and that a machine name is matched after `MachineName.Sanitise`
- [ ] T010 Add to `src/Parley.Configuration/LaunchConfiguration.cs`: the `MachinesMember` constant, parsing and validation of the section through `TryRead` (a broken section is `false` with a problem naming file, machine, key and setting, per the Messages table in `contracts/launch-configuration.md`), and the entry's key on `LaunchMembership`
- [ ] T011 Add the one effective-settings method to `src/Parley.Configuration/LaunchConfiguration.cs`, taking the machine, the key and a variable reader, returning owns, broker, workspace and whether the broker came from a block (`data-model.md`). Make T009 pass
- [ ] T012 Add finding the file to `src/Parley.Configuration/LaunchConfiguration.cs`: `PARLEY_WORKSPACE`'s folder if it holds a matching entry, then the start folder and each parent up to the first holding `.git` or `.mcp.json`, each step checking `OffMachineLinks` before looking (`research.md` R3). The start folder is a parameter. Tests in `tests/Parley.Cli.Tests/FindingTheLaunchFileTests.cs`, including an old entry's path from another machine, and a file with no matching entry
- [ ] T013 **Mutation check:** make the allow-list accept `PARLEY_AGENT`, then `PARLEY_WORKSPACE`, and watch the T009 cases fail. Restore

**Checkpoint**: `dotnet build` is green and `pwsh scripts/test.ps1` passes. `NodeCatalogue` still announces what it did for every file with no section.

## Phase 3: User Story 1 - One file gives each machine its own line (P1)

**Goal**: A hand-written file with two blocks gives two agents, each with its own line and folder.

**Independent test**: Two checkouts sharing one file, the adapter started under each machine name against one broker. The roster shows each agent's own line and folder.

- [ ] T014 [P] [US1] Write failing tests in `tests/Parley.Mcp.Tests/ServerOptionsTests.cs`: block values win over the environment; another machine's block is ignored; another project's block is ignored; no section gives exactly today's result; a section makes the workspace the found folder and sends the identity
- [ ] T015 [US1] Change `ServerOptions.Read` in `src/Parley.Mcp/ServerOptions.cs` to find the file, find its entry from the launch's role and project (`research.md` R5) and overlay the block on `read`. The start folder is a seam, as `Read` already takes `read`. Make T014 pass
- [ ] T016 [US1] Confirm the file tools' root in `src/Parley.Mcp/RepositoryPaths.cs` and the storage check in `src/Parley.Mcp/Program.cs` use the effective workspace, with a test in `tests/Parley.Mcp.Tests/`
- [ ] T017 [US1] Write `tests/Parley.Cli.Tests/TwoMachinesOneFileTests.cs`, integration: one broker on loopback, two checkouts, the adapter launched with `PARLEY_MACHINE` as `laptop` and `server`. Assert both rows, their own lines and folders, that a full name reaches one, and that a bare role opens one exchange with each (FR-019, FR-020)
- [ ] T018 [US1] Test attaching with an identity against an existing row registered with none, in `tests/Parley.Cli.Tests/AnIdentityArrivesLateTests.cs`. Report the result to the owner before changing broker behaviour if the agent gets stuck
- [ ] T019 [US1] Assert in `tests/Parley.Cli.Tests/TwoMachinesOneFileTests.cs` that each agent's *pending* comparison uses its own block's line (FR-022)

**Checkpoint**: US1 works with a hand-written file.

## Phase 4: User Story 2 - A server joins through a tunnel (P1)

**Goal**: A machine describing no projects can enrol and run an agent by naming the project and the address, and the wrong project is refused.

**Independent test**: On a root with no projects, enrol, start the node and open the repository. The agent attaches and reads `startable`. The root still has no project and no store.

- [ ] T020 [US2] Write failing tests in `tests/Parley.Server.Tests/AttachNamesItsProjectTests.cs`: a mismatched `expected_project` fails with `FailedPrecondition` naming both, and writes no row; an empty one attaches as before; `Nodes.Describe` says the project
- [ ] T021 [US2] Implement the refusal before any row in `src/Parley.Server/AgentsService.cs`, and `project` on `Describe` in `src/Parley.Server/NodesService.cs`
- [ ] T022 [US2] In `src/Parley.Mcp/ServerOptions.cs` and `src/Parley.Mcp/BrokerLink.cs`: a block's address skips the manifest and the notice and sends `expected_project`; report the refusal by the route other attach refusals use. Both set outside a block keeps today's notice (FR-009). Tests in `tests/Parley.Mcp.Tests/ServerOptionsTests.cs`
- [ ] T023 [US2] **Mutation check:** make the broker skip the `expected_project` comparison and watch T020 fail. Restore
- [ ] T024 [US2] Write failing tests extending `tests/Parley.Cli.Tests/ExplicitBrokerSelectionTests.cs`: `enroll` and `up` with an undescribed project and an address that serves it go ahead; another project is refused naming both, with nothing written; every other verb is still refused; no project, store or port appears in the root
- [ ] T025 [US2] Implement the broker-confirmed project in `src/Parley.Setup/ProjectResolution.cs` and `src/Parley.Cli/Program.cs`, for `enroll` and `up` only
- [ ] T026 [US2] Make `enroll --broker` with no project exit 78 with a sentence asking for `--project`, in `src/Parley.Cli/Program.cs`, replacing the crash on `projects/.lock`. Test in `tests/Parley.Cli.Tests/EnrolNeedsAProjectTests.cs`
- [ ] T027 [US2] Make `src/Parley.Setup/EnrollmentPlan.cs` write new entries as `data-model.md` says: shared `command`, `PARLEY_AGENT` and `PARLEY_PROJECT`; this machine's block with owns and the address, created even if empty; no folder path. `node.json` still gets the folder. Correct the out-of-date comment on the workspace. Tests in `tests/Parley.Cli.Tests/EnrollmentPlanTests.cs`, updating expected shapes only where FR-014 changes them, and reporting each one changed
- [ ] T028 [US2] Make `src/Parley.Cli/NodeCommand.cs` take the project from `Nodes.Describe` when none is named, so `up --broker` announces only that project's names. Test with a file naming two projects in `tests/Parley.Cli.Tests/ANodeByAddressTests.cs`
- [ ] T029 [US2] Test in `tests/Parley.Cli.Tests/ANodeByAddressTests.cs` that a node joined by address **starts an agent** when a Request arrives, using the fake command the existing node tests use, and that its working directory is the `node.json` folder
- [ ] T030 [US2] Resolve the address from the repository's block in `src/Parley.Setup/ProjectResolution.cs` and `src/Parley.Setup/RepositoryProjects.cs`, for a command run in a repository with nothing else named. Add `--launch-key` to `src/Parley.Cli/Program.cs`. Tests in `tests/Parley.Cli.Tests/ExplicitBrokerSelectionTests.cs`
- [ ] T031 [US2] Change `launches` and `waiting` in `plugins/parley/hooks/parley_waiting.py` to pass `--launch-key` with no block logic of their own. Tests in `tests/Parley.Mcp.Tests/WaitingHookTests.cs` and `tests/Parley.Mcp.Tests/WaitingHookRunTests.cs`, including a block address that differs from a shared one

**Checkpoint**: Run `research.md` R1's table again by hand. Every row is either correct or deliberately unchanged.

## Phase 5: User Story 5 - An agent-only machine stays reachable (P1)

**Goal**: With no broker on the machine, the server's agent stays startable across reboots and dropped tunnels.

**Independent test**: Agent-only install, node entry installed. Stop and restart the broker: the node rejoins and its agent is the same process.

### Node

- [ ] T032 [US5] Write failing tests in `tests/Parley.Cli.Tests/ANodeKeepsTryingTests.cs`, integration: with `--keep-trying`, a broker that starts late is joined; one that stops and returns is rejoined within a minute, with the resident the same process (SC-008); another project and another protocol exit 78 with no retry; without the flag, `Unavailable` still exits 69
- [ ] T033 [US5] Implement `--keep-trying` in `src/Parley.Cli/NodeCommand.cs` and `src/Parley.Cli/Program.cs`: wait from 2 seconds doubling to at most 30, with no limit, keeping residents
- [ ] T034 [US5] Send `protocol_version` in `src/Parley.Cli/NodeCommand.cs` and refuse a difference, naming both, in `src/Parley.Server/NodesService.cs`. Test in `tests/Parley.Server.Tests/AJoinNamesItsProtocolTests.cs`
- [ ] T035 [US5] **Mutation check:** make a refusal retry instead of exit, then make `Unavailable` exit under `--keep-trying`, then make `Join` skip the protocol comparison. Watch each fail T032 or T034. Restore each

### Autostart

- [ ] T036 [P] [US5] Write failing tests in `tests/Parley.Cli.Tests/AutostartNodePlanTests.cs` on rendered text for a scheduled task, a systemd user unit and a launch agent: label `parley-node-<project>`, arguments `up --project <p> --broker <a> --keep-trying --log <path>`, no `serve`, no restart after exit 78, and correct quoting
- [ ] T037 [US5] Add `AutostartPlan.DescribeNode` in `src/Parley.Setup/AutostartPlan.cs`, plus the check for lingering and the notices in the Messages table
- [ ] T038 [US5] Add install, status and remove for node entries in `src/Parley.Cli/AutostartCommand.cs` and `src/Parley.Cli/Program.cs`: confirm the broker first, allow one entry per project, `remove` needs the project when there are several. Extend `tests/Parley.Cli.Tests/AutostartSaysWhatIsTrueTests.cs` and `tests/Parley.Cli.Tests/AutostartConsoleTests.cs`
- [ ] T039 [US5] **Mutation check:** make the entry skip confirming the broker, and make its rendered unit restart after exit 78. Watch each fail. Restore each

### Install scripts

- [ ] T040 [US5] Add `--agent-only` to `install.sh`: tools `parley`, `parley-mcp` and the terminal unless `--surfaces` says otherwise; no broker entry and no broker start; refuse with `--only` or `--reset`, saying why
- [ ] T041 [US5] In `install.sh`: stop each `parley-node-*` before replacing tools and start it again afterwards; stop a plain install on a machine with such entries and ask for `--agent-only` or `--force`; make `--uninstall` remove them. Do the same removal in `reset.sh`
- [ ] T042 [US5] Make the same changes as T040 and T041 in `install.ps1` as `-AgentOnly`, and in `reset.ps1`
- [ ] T043 [US5] **By hand, on Windows, on a throwaway root and only with the owner's agreement, since `-Force` ends the live broker on this machine:** run `install.ps1 -AgentOnly`, a second run, a plain run and `-Uninstall`. Write what each did in the PR body
- [ ] T044 [US5] **By hand, in a Linux container (podman):** run `install.sh --agent-only`, a second run, a plain run and `--uninstall`, each with the same check. Write what each did in the PR body

**Checkpoint**: US5 holds in tests. The reboot and lingering checks wait for T058.

## Phase 6: User Story 3 - Each machine changes only its own block (P2)

**Goal**: Enrolling, accepting a line and withdrawing change only this machine's block.

**Independent test**: A two-block file in the writer's format. Enrol, accept and withdraw on one machine. The diff touches only that machine's block.

- [ ] T045 [US3] Write failing tests in `tests/Parley.Cli.Tests/OneMachineOneBlockTests.cs`: each act in `data-model.md`'s Writes table changes only this machine's block; another machine's values and order survive; the file's byte-order mark survives; shared values before this feature stay (FR-018); a file in the writer's format diffs only in the block (SC-003)
- [ ] T046 [US3] Change `WriteAsync` in `src/Parley.Setup/OwnershipLine.cs` to write this machine's block, keeping `NotHere`. Removing a line keeps the block. Extend `tests/Parley.Cli.Tests/OwnershipLineTests.cs`. Confirm the Auto Accept route in `src/Parley.Cli/ServeCommand.cs` goes through it
- [ ] T047 [US3] Change an existing entry's enrolment in `src/Parley.Setup/EnrollmentPlan.cs` to write this machine's block only, and refuse a role change while another machine has a block for that key, naming both roles and the machine
- [ ] T048 [US3] Change withdrawal in `src/Parley.Setup/Enrolment.cs`: remove this machine's block; keep the entry while another machine's block names the key; the identifier goes with the last entry; prune a machine and a section left empty. Extend `tests/Parley.Cli.Tests/EnrolmentTests.cs`
- [ ] T049 [US3] Give a `this-machine` window the real project for a line, from `Nodes.Describe`, in `src/Parley.Terminal/Ownership.cs`, `src/Parley.Terminal/Responsibility.cs` and `src/Parley.Web/Pages/Projects/Act.cshtml.cs`. Check what enrolling from such a window does in `src/Parley.Terminal/Adding.cs` and `src/Parley.Web/Pages/Projects/Register.cshtml.cs`, and refuse it, naming `parley enroll`. Tests in `tests/Parley.Terminal.Tests/` and `tests/Parley.Web.Tests/`, driving the screen. `DriftTests`, `SurfaceTests` and `EveryActionHasADoorTests` stay green
- [ ] T050 [US3] **Mutation check:** make withdrawal remove the entry regardless, make it remove another machine's block, make `WriteAsync` write the shared `env`, and remove the role-change refusal. Watch each fail. Restore each

**Checkpoint**: Quickstart steps 1 to 4 hold with two local roots.

## Phase 7: User Story 4 - Mistakes in the file are named (P3)

**Goal**: Every mistake is named at the moment it matters.

**Independent test**: A misspelt machine and a disallowed setting are reported by machine and setting from every reader.

- [ ] T051 [P] [US4] Test every row of the Messages table in `contracts/launch-configuration.md` in `tests/Parley.Cli.Tests/MistakesInTheFileAreNamedTests.cs`, through the adapter, the node, enrolment and the CLI (SC-006)
- [ ] T052 [US4] Add this machine's name and the blocks the file has to the refusal in `src/Parley.Mcp/ServerOptions.cs`, and fix any row T051 finds wrong
- [ ] T053 [US4] Extend `tests/Parley.Cli.Tests/OneBadLaunchConfigurationTests.cs`: a broken section reports that repository's problem, and the node carries on announcing the others
- [ ] T054 [US4] Hold that every program reads alike (SC-005): run `tests/LaunchTable.cs` through the adapter, Setup and the CLI's `--launch-key` route, in `tests/Parley.Cli.Tests/EveryReaderAgreesTests.cs` and `tests/Parley.Mcp.Tests/EveryReaderAgreesTests.cs`

## Phase 8: Polish

- [ ] T055 [P] Update `docs/configuration.md`: the block format, the "same committed file" paragraph, the tunnel section, node catalogue notes, an agent-only machine's prerequisites and setup, the limits in `plan.md`, and what a committed file shows. Update `docs/launch-configuration.md`: the shape, what every reader and writer owes, withdrawal, and the setting table. Sweep so each rule has one statement and no superseded wording remains
- [ ] T056 [P] Update `docs/operating.md` and `README.md` for the agent-only install, keeping `README.md` free of spec section references, and `plugins/parley/skills/parley-operator/SKILL.md` and `plugins/parley/commands/status.md` for the new commands
- [ ] T057 Update `CLAUDE.md`'s Layout and Behaviours for blocks, the agent-only machine and the exception in T005, and `.specify/memory/constitution.md` if it restates anything changed
- [ ] T058 Run all of `quickstart.md` with two roots on this machine and a Linux container, **including a node starting a real agent over an address**. Tell the owner which steps need a real second machine: the reboot, lingering and a real SSH tunnel. Ask whether to run those before merging, and report which were and were not run
- [ ] T059 Run `pwsh scripts/test.ps1 -Category All` and read every failure. Check the run included the new files by their test counts
- [ ] T060 Run the hygiene check on tracked and brand-new files against the names of the sibling repositories, and read `git status` by name before staging, leaving `.claude/outbox/` and `specs/027-session-owned-delivery/` out
- [ ] T061 Open the pull request with a Conventional Commits title and the design rationale in the body, label it `full-tests`, and run the review gate in `review/README.md`, reading the gate's file and each lens's file first

## Dependencies

```text
Setup -> Foundational -> US1 -> US2 -> US5
                         US1 ------> US3 (T047 builds on T027 in US2)
                         US1 -> US4 (T054 needs T030 in US2)
all -> Polish
```

- **US2 needs US1:** the adapter's overlay.
- **US5 needs US2:** the broker-confirmed project (T025) and the node by address (T028).
- **US3 needs T027** for the block shape. Otherwise independent of US5, so the two can run side by side.
- **Within a story:** the test task comes before what it tests. Watch it fail first.
- **Mutation checks** come after their guard and its test.

## Parallel examples

```text
Foundational: T003-T006 are one file, in order. T007 can run beside them.
US5: T036 (autostart tests) beside T032 (node tests).
     T040-T041 (install.sh) beside T042 (install.ps1).
After US2: US3 and US5 can run side by side, except that both touch src/Parley.Cli/Program.cs.
Polish: T055 and T056 together.
```

Mutation checks rebuild and rerun, so never run two at once or beside another test run. They also load the machine, so timings measured during one mean nothing.

## Implementation strategy

**This feature ships as one pull request.** The increments are checkpoints, not releases:

1. **After US1:** hand-written files work.
2. **After US2:** a server can join by tunnel, though only while somebody keeps `parley up` running.
3. **After US5:** the owner's example holds with nobody watching. This is the first point that meets the request.
4. **After US3:** the tools keep the file right, rather than a person.
5. **After US4 and Polish:** ready for the gate.

Bump nothing further while the feature is open: T001 sets the version for the whole of it.
