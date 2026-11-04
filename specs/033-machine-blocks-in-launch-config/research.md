# Research: One launch configuration for every machine

## R1. What a machine with only a tunnel does today

Tried on 2026-11-04 with build 0.52.0 (6acd90b): a broker for project `alpha` in one root, and a second root with no projects and `PARLEY_MACHINE=server`. Loopback only. The probe was not committed.

| Step | Result |
|---|---|
| `parley enroll <dir> --all --project alpha` | Refused, exit 78: "No project named 'alpha'". |
| The same with `--broker <address>` | Refused the same way. `ExplicitBrokerSelectionTests` holds this rule. |
| `parley enroll <dir> --all --broker <address>` | **Crashes**: unhandled `DirectoryNotFoundException` on `<root>/projects/.lock`. Also with `PARLEY_BROKER`. An existing bug. |
| `parley up --broker <address>` | Works: "Node server joined … Can start: infra", and the agent reads `startable`. |
| Adapter, `PARLEY_PROJECT` only | Stops, exit 78, and says to set `PARLEY_BROKER`. |
| Adapter, project and address | Connects, with the notice. |
| Adapter, address only, no workspace | Connects as `infra@server`. **No identity is sent**, because the file is only looked for in `PARLEY_WORKSPACE`. |
| Adapter, wrong project name and an address | **Connects to the other project**, with only the notice. |

**Not tried:** a node actually starting an agent over the address. The probe's command did not exist on purpose. The test in `tasks` covers it.

**From the code, not run:** a node with no project "accepts every claim" (`NodeCommand.RunAsync`), so `up --broker` on a repository in two projects announces the other project's names too.

**Conclusion.** The adapter and node need small changes. Enrolment needs a real route. The wrong-project hazard is real.

## R2. Does a person's own variable beat a block?

**Decision: a block beats the environment, for the two settings a block can hold (`PARLEY_OWNS`, `PARLEY_BROKER`).**

**Why.** A client merges the entry's `env` into the adapter's environment before the adapter runs. So the adapter sees one value and cannot tell a person's variable from an old entry's shared value. "A person's variable wins" would therefore also mean "an old shared value wins", which breaks FR-003 on every file enrolled before this feature.

**What a person does instead.** Edit that machine's block, or set `PARLEY_MACHINE` to use another block.

**`PARLEY_WORKSPACE` is not in a block (R4), so this question no longer applies to it.** It says where to look for the file first (R3). That also covers the override it offers today: point it at a checkout, and that checkout is the workspace.

**Alternatives rejected.** The environment wins: fails as above. A separate override variable: another setting for a case nobody has asked for.

## R3. How the adapter finds the file

New entries have no `PARLEY_WORKSPACE` at all (FR-014). So the file must be found another way.

**Decision.** In order:

1. The folder in `PARLEY_WORKSPACE`, if it holds a `.mcp.json` with a matching entry. An old entry's path from another machine fails this and is ignored.
2. The folder the adapter was started in, then each parent, stopping at the first that holds `.git` or `.mcp.json`.

Each step checks `OffMachineLinks` before looking, as every other lookup does. A file found this way counts only if one of its entries names this launch's role and project (R5). Otherwise the launch has no block.

**A file with no `parleyMachines` member changes nothing.** Workspace and identity come out as they do today (FR-004). That includes the address-only case in R1, which keeps registering by name alone.

**To confirm while building:** the folder Claude Code starts an MCP server in, when it was launched from a subfolder. The parent walk exists for that case. A node always starts agents in the catalogue's folder (`ResidentAgent`).

## R4. No workspace in a block

**Decision (owner, 2026-11-04): a block holds no workspace.** The first draft allowed one. The adapter must find the file before it can read a block, so it already knows the folder. A block's value could only repeat that or be out of date.

**Auto-launch does not use it.** The broker's `StartAgent` carries a name, a reason and an identity, and no folder. The node takes the folder from its own `node.json` (`NodeCommand`, `NodeCatalogue`) and starts the agent there (`ResidentAgent`). `node.json` is per machine and already holds an absolute path.

**Every other use is still served.** When a file has a machine section, the found folder is the workspace. It sets the file tools' root (`RepositoryPaths`), and the adapter reports it on attach. The registry records it, and `OwnershipLine` uses that record to find the file.

**What this gains.** A moved checkout keeps working when opened by hand, the committed file shows no folder paths, and each machine has no second copy of a path to drift.

**Also to fix:** the comment at `EnrollmentPlan.cs:460` says a node uses `PARLEY_WORKSPACE` as its working directory. The code does not.

## R4a. An empty block

**Decision: enrolment writes a block even when it would be empty.** Withdrawal keeps a project's entry while another machine's block names it (FR-017), so a block must mean "this machine is enrolled". Without this, a server with no line and no address would have no block, and withdrawing on the laptop would remove the entry the server depends on. It also means a freshly enrolled file always has a section, so FR-005a applies from the start.

## R5. Which block a launch uses

**Machine:** `MachineName.Sanitise` of the entry's `PARLEY_MACHINE` if it has one, otherwise `MachineName.Current`. This is the name the agent will carry.

**Project entry:** the adapter is not told its key. It finds the entry whose `PARLEY_AGENT` and project (environment first, then key, as `LaunchConfiguration.Declared` already reads them) match its own launch. Other readers already hold the key.

## R6. Telling the adapter and the broker apart on the project check

**Decision: a new `expected_project` field on `AttachRequest`, sent only when a block supplied the address.** The broker knows its project (`BrokerRuntime.Project`) and refuses a mismatch before any row is written, naming both. A refused adapter reports it by the route other attach refusals use.

**Rejected.** Reading `human-user@<project>` from the roster: the row would already exist in the wrong store, and it parses meaning out of a display name.

## R7. Enrolling on a tunnel-only machine

**Decision.** `parley enroll <dir> --project <name> --broker <address>`, and the same for `parley up`. If the machine does not describe the project and an address was given explicitly, these two verbs ask the broker which project it serves and go ahead only if it is the one named. Every other verb keeps today's refusal, which `ExplicitBrokerSelectionTests` still holds.

The broker's name comes from a new `project` field on `Nodes.Describe`'s reply. That is one additive field. A new `Operators` call would oblige both screens to bind it (`DriftTests`), and none of them need it directly.

`up --broker` with no `--project` also uses this answer, so it announces only that project's names (R1).

**The crash in R1 is fixed in the same place:** enrolling with an address and no project stops with a sentence asking for `--project`.

**Terminal and web enrolment stay as they are.** On a machine with only a tunnel, enrolment is `parley enroll`. A surface showing `this-machine` refuses to enrol, naming that command, because it would otherwise write `parley-this-machine`. To be checked while building: what they do today.

**A line accepted from a `this-machine` window** must reach the real entry (spec, US3 scenario 4). Today it would look for project `this-machine` and find no entry. The surface asks `Nodes.Describe` for the name before writing.

## R8. One implementation for the waiting hook

`parley_waiting.py` reads the file itself and passes `--broker` or `--project`. Adding block logic in Python would be a second implementation of FR-005, and would need a second copy of the name normalisation that the hook already avoids.

**Decision.** The hook passes `--launch-key <key>` and no address, and the CLI works out the effective address from `LaunchConfiguration`. The same resolution serves a person running `parley registry` inside a repository whose block names a broker.

**Rollout.** The plugin cache is a copy keyed by commit, so the hook changes only when the plugin is reinstalled. An old hook on a machine using blocks reports nothing waiting, which is the direction it already treats as safe.

## R9. "Every byte unchanged" is not achievable as written

`OwnershipLine` and `Enrolment` parse the file and write it back with `ToJsonString(Indented)` and a final `\n`. Hand formatting and CRLF are normalised today. Keeping them would need a format-preserving JSON editor, which is a large addition for little gain.

**Decision: correct the spec.** Other machines' blocks and the shared values keep their **values and member order**. A file already in the writer's format is byte-identical outside what changed, which is what keeps git merges small. FR-016 and SC-003 are changed to say so. The byte-order mark is still kept.

## R10. Versions

No schema change, so no migration, and 007 stays with feature 027. Three additive wire fields: protocol 0.13. Package 0.53.0, since the protocol moves and enrolment writes a new shape.

## R11. An agent-only machine (added by the owner, 2026-11-04)

**What exists, from reading the code. None of this was run for this section.**

| Thing | Today | Consequence |
|---|---|---|
| `install.sh`, `install.ps1` | Always register autostart and start a broker. | A broker on the server, serving nothing. |
| `AutostartPlan` | One entry, `parley-broker`, which runs `serve`. | It has no way to run only a node. With no project, its node serves nothing. |
| `NodeCommand` | Returns `Unavailable` on `StatusCode.Unavailable`. | A dropped tunnel ends the node. It also matters at boot, if the tunnel is not up yet. |
| `Nodes.Join` | I found no protocol check in `NodesService`. | A node on an old build may join. Across machines, "one build" is no longer guaranteed. |
| Autostart scope | User-level by design: logon to logout. | Wrong for a server nobody logs in to, unless Linux lingering is on. |

**Decisions.**

- **Entry:** a new `AutostartPlan.DescribeNode`, labelled `parley-node-<project>`, running `up --project <p> --broker <a> --keep-trying --log <path>`. One entry per project, since each runs one node. It has its own label, so it never collides with `parley-broker` on a machine that has both.
- **Retrying:** `--keep-trying` on `up`. `Unavailable` then waits and tries again, from 2 seconds doubling to at most 30, for ever: nothing times out. Agents the node runs are kept, as they already are across a rejoin. A refusal is not retried, because the broker answered: another project, another protocol, or a second node. The supervisor must not restart a node after those, so they exit with `Config` and the unit restarts only on failure codes other than that.
- **Without the flag,** `parley up` still exits on `Unavailable`, because a person typing it wants to know.
- **Lingering:** the install checks `loginctl show-user` and prints `loginctl enable-linger <user>` when it is off. It does not run it. It changes a system setting, and may need privilege.
- **Why not a system service:** the node starts agents that must be signed in as the person, which is why autostart is already user-level.
- **Default surface is the terminal:** lines must be accepted on this machine (`OwnershipLine.NotHere`), the machine is reached over SSH, and no broker exists to start a web UI as its child. `docs/operating.md` already recommends the terminal for SSH.
- **Upgrades:** the scripts stop `parley-broker` by name today. They also stop each `parley-node-*` and start it again afterwards. To be checked while building: an agent the node was running ends with it, as it does when a broker stops today.
- **Remembering the mode:** the scripts do not record it. If `parley-node-*` entries exist and `--agent-only` was not passed, the install stops and asks for the flag or `--force`. Otherwise a routine upgrade would quietly add a broker.

**Rejected.** A separate `parley-node` tool: `up` already exists, and the install already has four packages. A restart loop left to the supervisor alone: a restart ends the node's agents, and the scheduled task and launchd entries would each need it done differently.
