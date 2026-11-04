# Feature Specification: One launch configuration for every machine

**Feature Branch**: `033-machine-blocks-in-launch-config`
**Created**: 2026-11-04
**Status**: Built
**Input**: Owner, 2026-11-04: "We're going to add the ability for the .mcp.json to support agents being registered on different machines. Example; an infrastructure agent that is registered both on the local machine and on the server that hosts the infrastructure implementation. The local instance can answer questions about the system while the remote agent can execute changes."

**Decided by the owner, 2026-11-04**:

- The protocol does not learn about projects (SPEC.md §4.1). A project is a broker's way of grouping agents, and the address is the membership, so an adapter makes no check of which project it reached. An earlier draft added one to the wire and it was taken out.

- One committed `.mcp.json` describes every machine. What is shared stays where it is. What differs goes in a block per machine.
- Parley describes the difference between instances and does not enforce it. Each machine's ownership line says what that instance does, and the real limit is that machine's own permissions and credentials. There is no new protocol field for it.
- A block holds no workspace. Each machine works its folder out from where the file sits, and a node starts an agent from the folder in that machine's own `node.json`, so a path in the block would only repeat one or the other.

**Not a change to who an agent is.** SPEC.md §4.1.1 already makes one repository on two machines two agents, `infra@laptop` and `infra@server`, with the same identifier. This feature changes what the file can say. It does not change naming, identity or how a role is addressed.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - One file gives each machine its own line (Priority: P1)

A person keeps an infrastructure repository on their laptop and on the server that hosts the infrastructure. The laptop's agent answers questions about the system. The server's agent makes changes. One committed file says so, and each agent reads as its own line to everybody who looks at the roster.

**Why this priority**: It is the feature. Today the two agents can exist, but one file can hold only one ownership line per project, so one of them describes itself wrongly.

**Independent Test**: Give one file blocks for two machine names, in two checkouts. Start the adapter under each name against one broker. The roster shows two agents, each with its own line, folder and machine.

**Acceptance Scenarios**:

1. **Given** a file with blocks for `laptop` and `server`, **When** each machine opens the repository, **Then** the project lists `infra@laptop` and `infra@server`, each with the ownership line from its own block and the folder that machine's checkout is in.
2. **Given** a file with a block for `laptop` only, **When** a third machine opens the repository, **Then** it uses the shared values, as a file with no blocks does today.
3. **Given** a file with no blocks at all, **When** any machine opens the repository, **Then** it behaves exactly as it does today, and nothing rewrites the file.
4. **Given** a repository in two projects, **When** a machine opens it, **Then** each project's agent takes that machine's values for that project, and a value for one project never reaches the other.
5. **Given** both agents are listed, **When** a sender wants the change made, **Then** it can tell from the roster which instance to ask, and asks it by full name. A Request to bare `infra` still reaches both (§5.5).

---

### User Story 2 - A server joins through a tunnel (Priority: P1)

The server does not run the broker. The person has set up a tunnel to the broker on their laptop. The server's block names the tunnel's local address, while the shared entry still names the project. The agent joins that project and nothing is created on the server.

**Why this priority**: The example has no second instance without it. Today the adapter stops on a project the machine does not describe, and enrolment refuses one even when given an address.

**Independent Test**: On a machine that describes no projects, with a broker reachable at a local address, enrol the repository, start the node and open the repository. The agent attaches, can be woken, and the machine still has no project and no store.

**Acceptance Scenarios**:

1. **Given** a block naming a broker address and a shared entry naming a project, **When** the adapter starts, **Then** it connects to that address with no notice about two settings disagreeing, because the file now says this on purpose.
2. **Given** the address reaches another project's broker, **When** the adapter starts, **Then** it joins that broker, because the address is the membership (§4.1). The agent shows in that project's roster.
3. **Given** a machine that describes no projects and an address for the broker, **When** the person enrols the repository there, **Then** it succeeds under the project's name, writes that machine's block, and creates no project, store or broker on that machine.
4. **Given** the node on that machine, **When** a Request arrives for its agent while none is running, **Then** the node starts it, as a node on the broker's own machine would.
5. **Given** the tunnel is down, **When** the adapter starts, **Then** it reports that the broker at that address cannot be reached. The agent stays registered, and its open work stays flagged as waiting on an unreachable agent.

---

### User Story 3 - Each machine changes only its own block (Priority: P2)

The person enrols on each machine, and each machine's ownership line gets accepted or corrected over time. Neither machine disturbs what the other has written.

**Why this priority**: Without it, the shared file goes wrong the first time somebody accepts a line.

**Independent Test**: Take a file with two blocks, in the writer's format. Enrol, accept a line and withdraw on one machine. Compare the file: the diff touches only that machine's block.

**Acceptance Scenarios**:

1. **Given** a file with a block for `server`, **When** the person enrols on `laptop`, **Then** a block for `laptop` is added and `server`'s block and the identifier are kept.
2. **Given** an ownership line accepted for `infra@laptop` on `laptop`, **When** the file is written, **Then** only `laptop`'s block changes, for that project only.
3. **Given** Auto Accept is on and the agent is on the broker's machine, **When** its answer is accepted, **Then** the line goes to that machine's block, written first and recorded second as today.
4. **Given** `infra@server` is viewed from `laptop`, **When** the person tries to accept its line, **Then** it is refused as it is today and they are told to do it on `server`. The line is accepted from a surface running on `server`, which writes only `server`'s block.
5. **Given** blocks for two machines, **When** the person withdraws on `laptop`, **Then** only `laptop`'s block for that project goes. The shared entry and the identifier stay while any other machine's block still names that project.
6. **Given** a withdrawal removes the last block naming a project, **When** the file is written, **Then** the project's entry goes as it does today, and the identifier goes only with the last entry.

---

### User Story 4 - Mistakes in the file are named (Priority: P3)

The person makes a typing mistake in a machine name, or puts a setting in a block that cannot go there. They are told what is wrong and where, at the moment it matters, instead of finding an agent with the wrong line later.

**Why this priority**: A file edited by hand and shared between machines will drift. A silent fallback is the failure this project keeps removing.

**Independent Test**: Give a file a block with a misspelt machine name, and another with a setting that is not allowed. Each surface and tool that reads the file reports the problem by machine and setting.

**Acceptance Scenarios**:

1. **Given** a machine with no block in a file that has blocks for other machines, **When** the adapter starts and cannot reach a broker from the shared values, **Then** the failure names this machine and lists the blocks the file has.
2. **Given** a block holding a setting that may not be there, **When** any program reads the file, **Then** it is refused as unreadable, naming the machine, the project and the setting. It is not ignored.
3. **Given** a block whose ownership line has a line break or control character, **When** the adapter starts, **Then** it is refused as a shared line is today.
4. **Given** a malformed block section, **When** the node reads the file, **Then** the node reports that repository's problem and carries on announcing the others.

---

### User Story 5 - An agent-only machine stays reachable (Priority: P1)

The server only runs agents. The person installs Parley there with no broker, tells it which project to join and at what address, and from then on the server's agent can be started by a Request. That holds after a reboot and after the tunnel drops and comes back, with nobody logged in to do anything.

**Why this priority**: The server's agent is the one that makes changes. If it stops being startable after a reboot, the example fails in the way nobody notices until a change is needed.

**Independent Test**: Install in agent-only mode on a machine with no projects. Install the node's autostart entry. Stop the broker, or the address it is reached at, and bring it back: the node rejoins by itself. Restart the machine: the node is running and the agent reads `startable`, and no broker has run on that machine at any point.

**Acceptance Scenarios**:

1. **Given** a machine with the prerequisites, **When** the person installs in agent-only mode, **Then** the tools and one surface are installed, no broker is started, and no broker autostart entry exists.
2. **Given** an agent-only install, **When** the person installs the node's autostart entry naming a project and an address, **Then** a node runs for that project, starts at each logon, and creates no project, store or port.
3. **Given** a running node, **When** the broker becomes unreachable, **Then** the node keeps its running agents and keeps trying, and rejoins when the broker answers.
4. **Given** a node started while the tunnel is not up yet, **When** the tunnel comes up, **Then** the node joins with no further action, so the order of the two at boot does not matter.
5. **Given** the address reaches another project's broker when the entry is installed, **When** the person installs it, **Then** it is refused, naming both, and nothing is installed.
6. **Given** Linux with lingering off, **When** the entry is installed, **Then** the person is told the node stops at logout and is given the exact command that keeps it running. Nothing changes that setting for them. On Windows, the person is told the entry starts at logon and that a machine nobody logs in to needs someone to sign in.
7. **Given** a node's autostart entry, **When** the tools are upgraded, **Then** the install stops the node, replaces the tools and starts it again.
8. **Given** a node on a different protocol version from the broker, **When** it tries to join, **Then** it is refused, naming both versions.
9. **Given** an uninstall, **When** it runs, **Then** every node autostart entry is removed along with the tools.

### Edge Cases

- **The machine name is overridden.** A block is chosen by the machine name the agent will actually carry, after `PARLEY_MACHINE` and after a host name is normalised, so a host called `LAPTOP_1` finds the block for its normalised name.
- **A block is named in a form a machine name can never take.** It can never be chosen, so it is reported as a mistake, not left to sit there.
- **The host is renamed.** The agent becomes a new agent, as today (§4.1.1), and its old block is unused until somebody removes it. Nothing renames a block automatically.
- **Both machines enrol before either pulls.** Each mints an identifier and the two files conflict in git. The person resolves it. Two identifiers on two machines do no harm, because those are different agents anyway.
- **One machine has not pulled yet.** It runs on the file it has. Its agent carries what that copy says until it pulls and restarts.
- **A shared value and a block value disagree.** The block wins for that machine. The shared value stays for machines with no block.
- **A person sets `PARLEY_BROKER` or `PARLEY_OWNS` in their own environment.** A block beats it, with one rule across every reader. The adapter cannot tell a person's variable from an old entry's value (research R2).
- **A person sets `PARLEY_WORKSPACE` in their own environment.** It says where to look for the file first. If a matching file is there, that folder is the workspace.
- **An old entry holds another machine's path as a shared `PARLEY_WORKSPACE`.** If no matching file is at that path, it is ignored and the file is found from the folder the adapter started in.
- **The checkout moved on one machine.** The attach is refused, naming both folders (§4.1.1): a name belongs to one repository, and the broker cannot tell a moved checkout from a clone. The person gives it a different role or purges the old name, and enrols again from the new folder.
- **A row was registered with no folder.** It attaches, and the folder it now reports is recorded, so a line can be accepted for it.
- **A machine is enrolled and has no ownership line yet.** It has an empty block. The block is what says this machine uses that project entry, so a withdrawal elsewhere does not remove the entry from under it.
- **The adapter is started in a subfolder.** It looks in the folder it was started in and each parent, up to the repository's root. If it finds no file, it uses the launch environment alone, as an entry with no blocks does.
- **Enrolling under a different role while another machine has a block for that project.** Refused, naming both roles and the other machine, because the role is shared and changing it would rename the other machine's agent.
- **The file is gitignored,** as this repository's own is. Blocks still work, and the file simply is not shared.
- **A machine is switched off.** Its agent stays registered and a Request to the role still includes it (§5.5).

## Requirements *(mandatory)*

### Functional Requirements

**The file**

- **FR-001**: A launch configuration MUST be able to hold, beside its Parley server entries and its identifier, a section of blocks keyed by machine name and, within a machine, by project entry.
- **FR-002**: A block MUST be able to hold the ownership line and the broker address, and MUST NOT be able to hold anything else. In particular it cannot change the role, the project, the machine name, the workspace or the identifier, so the block chosen cannot depend on the block. A block may be empty, which says this machine is enrolled in that project and differs in nothing.
- **FR-003**: For one machine and one project entry, a value in the block MUST win over the shared value, and a value the block does not give MUST come from the shared entry.
- **FR-004**: A file with no blocks MUST behave exactly as it does today, with no migration and no rewrite on read.
- **FR-005**: Every program that reads the launch configuration MUST reach the same answer for a machine and a project: the adapter, the node, enrolment, the ownership writer, the surfaces, the command line and the plugin's waiting hook. A hook that reads a shared address when the machine's block names another would ask the wrong broker.
- **FR-005a**: When a file has a machine section, an agent's workspace MUST be the folder the file was found in. Its identity MUST come from that file. A file with no machine section MUST keep today's behaviour.
- **FR-006**: A block's values MUST be held to the rules the shared values are held to, including the ownership line's character rule. A section that cannot be read MUST be reported as unreadable, never read as saying nothing.

**Reaching a broker**

- **FR-007**: When the block for this machine names a broker address and the shared entry names a project, the adapter MUST connect to the address, MUST NOT require this machine to describe the project, and MUST NOT give the notice about both being set.
- **FR-008**: The wire MUST NOT gain any notion of a project (SPEC.md §4.1). Tools a person uses to set a machine up MAY compare a name with the `human-user@<project>` in a broker's registry, which adds nothing to the protocol.
- **FR-009**: Setting both a project and an address outside a block MUST keep today's behaviour, notice included.
- **FR-010**: A broker address in a block MUST be treated as one anywhere else is: nothing new binds to the network, and the existing warnings still apply. The tunnel stays the person's to set up.

**Enrolling and running on a machine with only a tunnel**

- **FR-011**: A person MUST be able to enrol a repository on a machine that does not describe the project, by giving the project's name and the broker's address, without any project, store or broker being created there.
- **FR-012**: A machine's node MUST be able to serve an agent in a project this machine reaches only by address: announce it, and start it when asked.
- **FR-013**: Each machine MUST enrol itself. No surface may write a block for a machine other than the one it runs on.

**An agent-only machine**

- **FR-024**: Both install scripts MUST offer an agent-only mode that installs `parley`, `parley-mcp` and one surface, starts no broker and registers no broker autostart entry. Its default surface MUST be the terminal on every platform, because the machine is reached over SSH and no broker is there to host a web UI. `--surfaces` still overrides it.
- **FR-025**: `parley autostart install --project <name> --broker <address>` MUST install an entry that runs this machine's node for that project and no broker, having first confirmed the broker serves that project. A machine MUST be able to hold one such entry per project. `status` MUST list them, and `remove` MUST take the project when there is more than one.
- **FR-026**: A node started from such an entry MUST keep trying when the broker cannot be reached, at first join and after losing it, and MUST keep its running agents meanwhile. It MUST stop, saying why, when the broker answers and refuses it for a reason that retrying cannot fix: another protocol version. It MUST keep trying when told another node is joined for this machine, and say so, because a broker that has not noticed this machine's last connection die reads exactly the same. `parley up` typed by a person keeps today's behaviour unless asked to keep trying.
- **FR-027**: Installing such an entry MUST say plainly when it will stop at logout or need a sign-in to start, and give the exact remedy. It MUST NOT change that system setting itself.
- **FR-028**: An install, an `--only` of the `parley` tool and an uninstall MUST stop node entries' processes before replacing or removing the tools. An install MUST start them again, and an uninstall MUST remove the entries.
- **FR-029**: A node MUST state its protocol version when it joins, and the broker MUST refuse a different one, naming both, as it does for an adapter.
- **FR-031**: A checkout reached by address MUST be in one project on that machine. Enrolling into another MUST be refused while the first is by address, in either order, because a node is one process per broker and could not wake an agent another node started.
- **FR-030**: Agent-only mode MUST NOT be combinable with `--only` or `--reset`, and MUST say why when asked.

**Writing**

- **FR-014**: Enrolment MUST write this machine's values into this machine's block, creating the block even when it would be empty, and MUST NOT write a machine's absolute path into a new entry or a block. The path a node starts from stays in that machine's own `node.json`.
- **FR-015**: Accepting, editing or removing an ownership line, by any route including Auto Accept, MUST write only this machine's block for that project. A surface on another machine MUST still refuse, as it does today.
- **FR-016**: Every write MUST keep the values and member order of other machines' blocks, the shared values and unknown members, and MUST keep the byte-order mark. A file already in the writer's format MUST be byte-identical outside what changed. A file formatted by hand is reformatted, as it is today.
- **FR-017**: Withdrawing MUST remove only this machine's block for that project. The project's entry MUST stay while another machine's block names it, and the identifier MUST stay while any entry remains.
- **FR-018**: Re-enrolling a file that has shared values from before this feature MUST keep them, and the new block takes precedence for this machine. There are two exceptions, which it MUST remove. A shared `PARLEY_WORKSPACE`, because a machine's path is no use to any other and, once the file has a section, would only send a clone looking in the original. A shared `PARLEY_BROKER`, as enrolling has always done, because the project is named and a port moves.

**Unchanged, and held by tests**

- **FR-019**: Agents MUST keep the name `role@machine`, and the same identifier on another machine MUST stay a different agent.
- **FR-020**: A Request to a bare role MUST still open one exchange with every agent registered under it.
- **FR-021**: Parley MUST NOT enforce what an instance may do. No operation, field or surface may suggest that an ownership line limits what an agent can do.
- **FR-022**: The recorded ownership line and the *pending* comparison MUST work per agent as they do today, with each agent's launch line taken from its own machine's block.

**Documentation**

- **FR-023**: SPEC.md, `docs/configuration.md` and `docs/launch-configuration.md` MUST state the block format and its precedence, the tunnel route, an agent-only machine's prerequisites and setup, and this example, as the only statement of each. A line saying an ownership line describes an instance and does not limit it MUST appear with the example.

### Key Entities

- **Machine block**: What one machine says differently for one project entry: ownership line and broker address, either or both or neither. Its presence says that machine is enrolled in that project. Lives in the repository's launch configuration, is keyed by machine name, and is written only from that machine.
- **Shared entry**: What every machine has in common for one project: the role, the project and how to launch the adapter. Unchanged in form.
- **Effective settings**: What one machine ends up with for one project after the block is laid over the shared entry. Every reader of the file must compute the same thing.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: One committed file, checked out unchanged on two machines, gives two agents in one project, each showing its own ownership line and workspace, with no per-machine edit to the file after each machine has enrolled.
- **SC-002**: A person with a working tunnel can enrol a machine that describes no projects and have its agent answer a Request, using only documented steps, and that machine still holds no project or store afterwards.
- **SC-003**: After any enrolment, line acceptance or withdrawal on one machine, every value of every other machine's block is unchanged in 100% of the tested operations, and a file in the writer's format shows a diff only in this machine's block.
- **SC-004**: Every existing launch configuration with no machine section gives the same agent, line, workspace and broker as before, and is not rewritten by being read.
- **SC-005**: For a set of test files, every program that reads the launch configuration reports identical settings for each machine and project.
- **SC-007**: On an agent-only machine, after a restart with nobody acting on it beyond what the install told them to set up once, a Request to its agent starts it. No broker process has run on that machine.
- **SC-008**: After the broker has been unreachable for any length of time, the node rejoins within a minute of it answering, and an agent it was running is still the same process.
- **SC-006**: Every malformed block or misnamed machine in the test set produces a message naming the machine and the setting, and none is silently ignored.

## Assumptions

- The person shares the file between machines with git. Parley does not copy it, sync it or resolve its conflicts.
- The tunnel is the person's to set up and keep up (`docs/configuration.md`, *Reaching a broker from another machine*). This feature adds no tunnel management and no network bind. The node tolerates the tunnel being down.
- An agent-only machine needs the .NET 10 SDK, Git and a checkout of Parley to build from (Parley is not on nuget.org), and the agent's client, signed in. The Claude Code plugin is needed only for the waiting hook. It holds no broker, project or store.
- Autostart stays user-level, because the node starts agents that must be signed in as the person. A system service is out of scope.
- A committed file now shows machine names and a tunnel port to anybody who can read the repository. It shows fewer folder paths than today, because new entries hold none. The docs must say this, and a repository that should not show them keeps its file gitignored.
- A tunnel-only machine was tried on 2026-11-04 (research R1). The adapter and node connect by address, and enrolment does not. A node actually starting an agent over an address has not been run yet.
- Feature 027 (session-owned delivery, uncommitted) is separate and untouched. No schema change is expected, so this feature should not take migration 007.
- Out of scope: writing a launch configuration for another machine, provisioning a machine over SSH, a recorded answers/executes label, and blocking write tools on an instance.
