# Implementation Plan: One launch configuration for every machine

**Branch**: `033-machine-blocks-in-launch-config` | **Date**: 2026-11-04 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `specs/033-machine-blocks-in-launch-config/spec.md`

## Summary

A repository's `.mcp.json` gains `parleyMachines`: what differs per machine, for each project entry. One method in `LaunchConfiguration` works out a machine's effective settings, and every reader uses it. Writers change only this machine's block. A machine with only a tunnel can enrol and run a node by naming the project and the address, once the broker confirms it serves that project. An agent-only install puts the tools on such a machine with no broker, and a node autostart entry keeps its agents startable across reboots and dropped tunnels. The approach and its evidence are in [research.md](research.md).

## Technical Context

**Language/Version**: C# on .NET 10; Python 3 for the plugin hook
**Primary Dependencies**: System.Text.Json through `StrictJson`; gRPC (`proto/parley.proto`). No new packages.
**Storage**: No schema change. The data is in the repository's `.mcp.json`.
**Testing**: xUnit v3 on Microsoft.Testing.Platform, `[Unit]` or `[Integration]`; `pwsh scripts/test.ps1`
**Target Platform**: Windows, Linux, macOS. The example's server is most likely Linux, so path spelling is tested for both.
**Project Type**: CLI tools, broker, MCP adapter
**Performance Goals**: The hook keeps its existing time budget. Reading the section adds one parse of a small file already read.
**Constraints**: Parley.Configuration reaches neither a store nor the transport. Parley.Mcp references neither Broker nor Setup. Loopback only. A green build is the lint gate.
**Scale/Scope**: About 14 source files, 3 wire fields, 2 install scripts and their reset scripts, 1 hook, 3 documents.

## Constitution Check

| Premise | Result |
|---|---|
| I. The specification is the product | **Pass**, provided SPEC.md changes first (T-order below). |
| II. No agent has authority | **Pass.** An ownership line describes. Nothing enforces (FR-021). |
| III. Identity is fixed by the launch | **Pass.** A block cannot hold the role or machine. The file is part of the launch, as today. |
| Terseness | **Pass.** The line keeps its character rule. |
| Credentials are references | **Pass.** A block holds none, and the allow-list keeps it so. |
| No network bind; tunnel only | **Pass.** A block names an address to connect to. Nothing binds. |
| A store has one broker | **Pass.** A tunnel-only machine creates no store (FR-011). |
| The plugin ships no MCP server configuration | **Pass.** |
| No surface writes for another machine | **Pass.** `NotHere` stays, and writes go to `agent.Machine`. |

**A rule this changes, on purpose:** "A project named that the manifest does not describe is still refused, address or not" now has an exception for `enroll` and `up`, and only once the broker confirms the name. `ExplicitBrokerSelectionTests` keeps every other verb.

Re-checked after design: no violations.

## Project Structure

### Documentation

```text
specs/033-machine-blocks-in-launch-config/
├── spec.md
├── plan.md
├── research.md
├── data-model.md
├── contracts/launch-configuration.md
├── quickstart.md
└── tasks.md             # next, /speckit-tasks
```

### Source code

```text
proto/parley.proto                          expected_project, project, a node's protocol_version
install.sh, install.ps1, reset.sh, reset.ps1  agent-only mode; stop, restart and remove node entries
src/Parley.Setup/AutostartPlan.cs           DescribeNode; the lingering and sign-in notices
src/Parley.Cli/AutostartCommand.cs          install, status and remove for node entries
src/Parley.Configuration/LaunchConfiguration.cs   the section, validation, effective settings, finding the file
src/Parley.Mcp/ServerOptions.cs             overlay the block on the launch's variables; no notice when a block gave the address
src/Parley.Mcp/BrokerLink.cs                send expected_project; report the refusal
src/Parley.Server/AgentsService.cs          refuse a mismatch, before any row
src/Parley.Server/NodesService.cs           say the project in Describe; refuse another protocol on Join
src/Parley.Setup/EnrollmentPlan.cs          write blocks, empty ones included; no folder path in a new entry; refuse a role change another machine uses; fix the out-of-date comment on the workspace
src/Parley.Setup/Enrolment.cs               withdraw this machine's block only
src/Parley.Setup/OwnershipLine.cs           write this machine's block
src/Parley.Setup/LaunchEntries.cs           effective values on an entry
src/Parley.Setup/ProjectResolution.cs       broker-confirmed project; address from the repository's block
src/Parley.Cli/Program.cs                   enroll and up by address; --launch-key; the crash
src/Parley.Cli/NodeCommand.cs               announce only the project the broker serves; --keep-trying; send the protocol
src/Parley.Terminal/, src/Parley.Web/       a this-machine window: real project for a line, refusal for enrolling
plugins/parley/hooks/parley_waiting.py      --launch-key, no block logic
SPEC.md, docs/configuration.md, docs/launch-configuration.md, CLAUDE.md
Directory.Build.props                       0.53.0

tests/Parley.Core.Tests or Parley.Mcp.Tests  effective settings, validation, finding the file
tests/Parley.Cli.Tests                       enrol, up and refusals with a real broker on loopback
tests/Parley.Mcp.Tests                       the hook, through WaitingHookRunTests
tests/Parley.Server.Tests                    attach refusal and Describe over a socket
```

**Structure decision:** no new project. Effective settings go in Parley.Configuration because the adapter and Setup can both reach it, and the dependency rules forbid anywhere else they would share.

## Order of work

1. **SPEC.md first:** §4.1.1, the launch configuration in §13, the Attach refusal in §10.1, the exception in §4.1.
2. **The reader:** section, validation, effective settings, finding the file. Unit tests on a table of files. Nothing uses it yet.
3. **The adapter and the wire**, which delivers US1 for hand-written files and most of US2.
4. **Writers:** enrolment, ownership line, withdrawal. Delivers US3.
5. **The tunnel route:** enroll and up by address, the crash, the hook, the window. Finishes US2.
6. **The agent-only machine:** `--keep-trying` and the node's protocol check, then the autostart entry, then both install scripts. Delivers US5.
7. **Messages and docs**, then the quickstart for real, including a node starting an agent over the address, a reboot and a dropped tunnel. Run the Linux half in a container if no second machine is at hand. A container cannot prove lingering, so that step needs a real login session.

## Testing notes

- **Test every reader on one table of files (SC-005).** One shared fixture of files and expected effective settings, run from the adapter, Setup and CLI test projects.
- **A no-block file stays the same (SC-004).** Run the existing launch tests unchanged, and add a test that reading writes nothing.
- **Mutation-check each new guard:** the project check, the allow-list, the withdrawal rule, the role-change refusal. Each must fail once with a mutation that compiles.
- **A moved checkout:** one test holds that the found folder is what the adapter reports, and that a node starts from `node.json` with no folder in the block.
- **A row already registered with no identity:** a machine section makes an agent present an identity where it may have sent none. Test that attaching this way against an existing row with no identity gives a clear result, not a stuck agent.
- **Tests that run a tool** clear every `PARLEY_` variable and pass the catalogue as a seam, as `CLAUDE.md` requires. Machine names reach child processes by `PARLEY_MACHINE`.
- **Linux:** path spelling and the workspace comparison, in a container if CI is down.
- **Autostart text is testable without installing:** `AutostartPlan` already renders the unit, task and property list, so node entries are held on the rendered text for all three platforms.
- **Retrying:** a node with `--keep-trying` against a broker that starts late, stops and comes back on a loopback socket. Hold that the resident is the same process throughout, and that a refusal ends it with exit 78.
- **Install scripts:** run both for real on a throwaway root. The scripts have no test project, so `tasks` must name this step by hand and report what it saw.

## Complexity Tracking

| Addition | Why it is needed | Simpler option rejected because |
|---|---|---|
| Three wire fields | The broker is the only thing that knows its project for certain, and machines can now differ in build. | Parsing `human-user@<project>` from a roster leaves a row in the wrong store. |
| A second kind of autostart entry | The server's agent must stay startable with nobody there. | Leaving `parley up` to the person fails silently at the first reboot. |
| `--launch-key` | The hook must not hold a second copy of the rules (FR-005). | A Python copy would need the name normalisation too, and would drift. |

## Known limits

- **The tunnel is still the person's.** The node waits for it, but nothing brings it up. The docs name `autossh`, a systemd unit or a scheduled task.
- **User-level autostart:** on Windows and macOS the entry starts at logon, so a machine nobody signs in to will not run it. On Linux, lingering fixes that. The install says which applies.
- **One node per broker** on such a machine: a repository in two projects there has two entries and two processes, so the "one process per identifier" rule is not shared between them. Not reachable in the owner's example. To be stated in the docs.
