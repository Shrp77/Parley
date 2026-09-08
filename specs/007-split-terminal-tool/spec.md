# Feature Specification: The surfaces are their own tools

**Feature Branch**: `007-split-terminal-tool`
**Created**: 2026-09-08
**Status**: Draft
**Input**: Split `parley top` out of the `parley` tool so terminal changes do not force a broker
restart. Extended by the owner: split the web UI out as well, and stop calling it "the console".

## The problem

One executable holds the broker, the node, the web UI, the terminal, and every one-shot command.
Windows cannot replace a running executable. So installing a terminal change means stopping the
broker.

Stopping the broker stops the node. SPEC.md §12: *"The node stops, and tells every agent it started
to quit."* And *"The process holds everything the agent has learned."*

So changing a status line ends every agent conversation on the machine.

The terminal is also the most-changed part of the repository. Source files changed on `main` since
`c85a271`: 29 in the terminal, 1 in the broker, 2 in the server, 2 in the store, 0 in the agent
adapter. The thing that changes most forces the most expensive restart.

In-flight work can be lost, not just paused. A question already drained into an agent's inbox is
marked delivered, so it is in §4.2's four-hour Working window. Kill the agent holding it and the
exchange stays open and owed with nothing running to answer it. It closes `ghosted`, which §5.3
defines as "nobody replied" — recorded against an agent that was killed.

## The decision this reverses

`CLAUDE.md` records the opposite call for the web UI:

> *a second tool would have kept ASP.NET Core off the machines that only run an agent, and one
> command is worth more than that saving.*

The owner's reasons for reversing it:

1. The scope has outgrown one console application.
2. Version drift cannot happen. Everything is one solution, built together on every change.
3. The dependency chain does not shift. The terminal is downstream of the protocol and the server.
   Nothing upstream depends on it. The split makes packaging match the existing graph.

The saving that decision named is still real, and this feature takes it.

## Three surfaces, four tools

| Tool | Contains |
|---|---|
| broker | broker, node, one-shot commands |
| agent adapter | unchanged |
| terminal | `parley top` |
| web UI | the browser surface |

A machine installs what it needs.

### On a headless machine

The web UI is an HTTP server. It runs with no display. It binds loopback, so nothing off the
machine reaches it directly — but an SSH port-forward is what §16 permits, and on a headless box
that beats the terminal: a forward you leave up, rather than an interactive session you hold open.

So the web UI is not useless on a headless machine. It should be a **choice**. Today it is not: the
broker's binary carries the whole web stack whether or not anyone will open a page.

### What splitting the web UI gains

`serve --with-console` runs the web UI inside the broker's process today. `CLAUDE.md` records the
containment that makes that safe: *"a console that cannot bind reports it and the broker keeps
serving."* Separate processes make that true without code that has to remember it.

### What it must not lose

`autostart install` adds `--with-console`, so the web UI starts at logon. The reason is §17
question 15: an agent attached through an editor window cannot be handed a turn, so a surface that
must be started by hand is not there when it is needed. That default must survive the move
(FR-018).

## Naming

"Console" means three things in this repository. Only one is renamed.

| Term | Meaning | Renamed? |
|---|---|---|
| `parley console`, `--with-console` | the web UI | **Yes** → `web-ui` |
| `OperatorConsole`, SPEC.md §10.2 | the operator's protocol operations | No |
| `ConsolePicker` | whether a terminal is available | No |

"Console" is a poor name for a browser surface in a console application.

## A store leak the split exposes

The terminal reaches SQLite today. `Parley.Cli/Top` uses `NodeCatalogue`, which is in
`Parley.Headless`, which references `Parley.Store`. It uses three members: `NodeCatalogue`,
`.Empty`, `.TryReadFrom`.

`GraphTests` checks that `Parley.Client` and `Parley.Web` cannot reach a store, walking the graph
transitively. It does not check the terminal, because the terminal lives inside `Parley.Cli`, which
owns the store legitimately. So this was never checkable. Splitting the terminal out makes it
checkable, and needs a store-free home for the catalogue reader.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Update a surface without ending a conversation (Priority: P1)

An operator has agents mid-exchange. They install a terminal change. The broker, the node and every
agent carry on.

**Why this priority**: this is the feature. Everything else follows from it.

**Independent Test**: leave an exchange open and delivered, install a terminal-only change, and
check the broker was not restarted and the exchange did not change state.

**Acceptance Scenarios**

1. **Given** a running broker with agents and an open delivered exchange, **When** a terminal-only
   change is installed, **Then** the broker is not restarted, no agent is told to quit, and no
   exchange changes state.
2. **Given** the same, **When** the terminal is started, **Then** it shows the same deployment,
   including the open exchange.

---

### User Story 2 - Be told what a broker change costs (Priority: P2)

A change touches the broker. The restart is unavoidable. The install says so first, and says what
is in flight.

**Why this priority**: the split cannot remove this cost, but it can stop it being paid by
surprise, which is what happens today.

**Independent Test**: with an open delivered exchange, install a broker change and check the
operator is warned before anything stops.

**Acceptance Scenarios**

1. **Given** a broker change and an open exchange, **When** the install runs, **Then** it names the
   consequence and the open exchange before stopping anything.
2. **Given** a broker change and nothing in flight, **When** the install runs, **Then** it proceeds
   without ceremony.

---

### User Story 3 - Choose which surfaces a machine carries (Priority: P3)

A headless Linux machine hosts agents. Its operator installs the web UI to reach over an SSH
tunnel, and not the terminal. Another machine installs neither.

**Why this priority**: this is the saving the original decision declined, and it makes the web UI
optional rather than mandatory.

**Independent Test**: install the broker alone on one machine and broker plus web UI on another.
Confirm agents work on both and the first carries no web stack.

**Acceptance Scenarios**

1. **Given** a machine with only the broker tool, **When** an agent is asked a question, **Then**
   it starts, answers, and the exchange concludes.
2. **Given** that machine, **When** its installed files are inspected, **Then** no part of the web
   stack is present.
3. **Given** a machine with the broker and the web UI, **When** an SSH port-forward is opened to
   the web UI's address, **Then** the deployment is readable in a browser at the far end.
4. **Given** either machine, **When** an absent surface is invoked, **Then** its absence is stated
   plainly.

---

### Edge Cases

- A surface and a broker at different versions. §10.2 is a versioned contract, and separate tools
  make a mismatch possible for the first time.
- A surface that is not installed, when something names it. The broker's startup block does.
- Installing while a terminal is open. The terminal now holds its own executable.
- A broker install during an open exchange (User Story 2).
- A catalogue file the terminal can read but the node cannot, once the reader moves.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The terminal and the web UI MUST each ship as a tool package separate from the broker
  and from each other.
- **FR-002**: Installing a change confined to a surface MUST NOT stop or interrupt a running
  broker, node, or agent.
- **FR-003**: Both surfaces MUST remain clients. They open no store and reach everything over the
  transport.
- **FR-004**: Each surface's package MUST have no route to a store, checked transitively, under the
  rule `Parley.Client` and `Parley.Web` are already held to.
- **FR-005**: The catalogue reader MUST be available to the terminal without a dependency on the
  store, and the node MUST keep reading the same file by the same rules.
- **FR-006**: The agent adapter and the §10.1 operations MUST NOT change.
- **FR-007**: The broker MUST remain the only component that opens the store.
- **FR-008**: Each surface MUST be startable by a documented command, and every place naming it —
  README, startup block, help text, plugin prose — MUST name the same thing.
- **FR-009**: The install path MUST treat the tools as a set, so adding one is data rather than
  code.
- **FR-010**: The install path MUST report which tools it changed.
- **FR-011**: When a surface and the broker disagree on the §10.2 version, the surface MUST say so,
  naming both versions, and MUST NOT present a partial view as a complete one.
- **FR-012**: An install that requires stopping the broker MUST say so first, and MUST name any
  open exchange.
- **FR-013**: A machine MUST be able to install any combination of surfaces, including none.
- **FR-014**: Uninstalling MUST remove every tool it installed and leave no scheduled task pointing
  at a missing path.
- **FR-015**: The broker's tool MUST NOT carry the web stack.
- **FR-016**: An absent surface MUST NOT affect the broker's behaviour.
- **FR-017**: A surface that cannot start MUST NOT stop, delay, or degrade the broker.
- **FR-018**: Where the web UI is installed, it MUST start by default and start again at logon, as
  `autostart install` gives today.
- **FR-019**: The user-facing name for the web UI MUST become `web-ui`. The command, the broker
  flag, and every document naming them MUST change together. `OperatorConsole` and `ConsolePicker`
  MUST NOT change.
- **FR-020**: The broker flag that starts the web UI MUST keep working, starting the separate tool
  rather than hosting it.
- **FR-021**: Each surface MUST be startable and stoppable independently of the other.

### Key Entities

- **Tool package**: a separately installable executable from this solution. Four after this change.
- **Catalogue reader**: reads a machine's node catalogue. Needed by the node, which starts agents
  from it, and by the terminal, which shows what a machine could start. Currently reachable only
  through a project carrying the store.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A surface-only change installs with zero broker restarts, zero agents ended, and zero
  exchanges changing state.
- **SC-002**: Replaying the previous release cycle, the share of changes needing a broker restart
  falls from 100% to under 20%.
- **SC-003**: An operator about to interrupt a delivered exchange is warned first, in 100% of
  cases.
- **SC-004**: A surface cannot reach a store, proved by a check that fails when it can.
- **SC-005**: A broker-only machine runs agents and carries no part of the web stack.
- **SC-006**: Everything naming how to start a surface names the same command, checked
  mechanically.
- **SC-007**: A surface pointed at a broker on a different §10.2 version reports it, naming both
  versions.
- **SC-008**: A web UI that cannot bind its port leaves the broker serving.
- **SC-009**: Where the web UI is installed, it is reachable after a logout and logon with nobody
  starting it by hand.
- **SC-010**: No user-facing text calls the web UI a console.

## Assumptions

- **One command is kept by a thin wrapper.** The broker's tool forwards `parley top` and
  `parley web-ui` to the separate executables when installed. The owner named this; it is recorded
  here rather than left to the plan.
- **The web UI stays on loopback.** §16 is unchanged. Remote access is an SSH tunnel the operator
  sets up, not a binding this feature changes.
- **Surface and broker versions normally match**, because both come from one build and one install
  path. FR-011 covers the case where they do not, which is newly possible.
- **The catalogue file format does not change.** Only where its reader lives.
- Scale for judging this: 21 agents registered in one project on the owner's machine.

## Out of scope

- Any change to the protocol, wire contract, or schema.
- Any change to the agent adapter, or to how agents are launched or ended.
- Removing the broker restart for changes that touch the broker.
- Hot-reload, in-place upgrade, or draining a broker without stopping it.
- What either surface shows. This moves where they live, not what they do.
- Exposing the web UI beyond loopback.
