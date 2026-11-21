# Feature Specification: A pinned graph of the work, and freezing an agent

**Feature Branch**: built on `035-material-3-web-ui`
**Created**: 2026-11-20
**Status**: Draft
**Input**: User description: "A top-level graph that can be pinned to any page. It shows a visual node representation of the
agents, projects and conversations they are in, including sub-agents. I want to see what is being worked on, by which
agents, for which project, and be able to tell it to pause the work and save its state, so it can be unfrozen after a
restart of the computer." The owner chose **freeze** for pause (the agent's process ends after its current turn and nothing
wakes it until a person unfreezes it; a turn is never cut off mid-way) and a **pinned panel on every page** for the graph.

## What already exists

- Everything the graph shows is already known to the broker: agents and their projects, conversations with who is in
  them, a spin-off's parent, which agents are working, idle or waiting on background work, and how many background tasks
  each has (SPEC.md §11.6). A turn report does not say which conversation a turn is for.
- Work is already durable. An open exchange lives in the store, and a restart wakes every agent that owes one
  (SPEC.md §11.1, the owner's decision of 2026-11-18). Stopping an agent ends its process and never its work.
- **What is missing is a freeze.** Stop does not keep an agent stopped: the next restart or the next message starts it
  again. And nothing shows the whole picture on one screen.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - See what is being worked on, on any page (Priority: P1)

A person on any console page opens a small tab at the edge of the screen and sees a graph: each project, the agents in it,
the conversations those agents are in, and for each agent whether it is working, idle, waiting on background work, or frozen.
They can pin it so it stays open as they move between pages, and unpin it back to the tab.

**Why this priority**: it is the whole of what the person asked to see, and it changes nothing, so it can ship alone.

**Independent Test**: with two projects, three agents and one spin-off conversation, open any page, open the panel, and
read from it alone which agent is working in which conversation of which project.

**Acceptance Scenarios**:

1. **Given** any console page, **When** the person opens the tab, **Then** the graph shows every project this console serves,
   its agents, and its conversations that are open or recently active, with each agent's state in words and not by colour alone.
2. **Given** a spin-off conversation, **When** the graph is drawn, **Then** it is joined to its parent.
3. **Given** an agent with three background tasks running, **When** the graph is drawn, **Then** it shows that agent with three
   sub-nodes, or one node saying "3 background tasks"; it never shows task names it was not told.
4. **Given** the panel is pinned, **When** the person goes to another page and back, **Then** it is still pinned and open.
5. **Given** the panel is collapsed, **When** a page loads, **Then** no graph is drawn and nothing is read for it.
6. **Given** a broker that cannot be reached, **When** the graph is drawn, **Then** that project is shown as unreachable and the
   others are drawn.

---

### User Story 2 - Freeze an agent from the graph, and unfreeze it (Priority: P2)

From an agent's node the person chooses *Freeze*. The node lets the agent finish its current turn, then ends its process.
The agent reads as *frozen* everywhere. Nothing starts it: not a restart of the broker or of the computer, not a new message
to it. Messages for it wait. Its open exchanges stay open. Later the person chooses *Unfreeze*, and the agent is started
if it owes work, as it would be after any restart.

**Why this priority**: it is the pause the person asked for. It needs a stored state, so it follows the read-only graph.

**Independent Test**: freeze an agent that owes an open exchange, restart the broker, and confirm the agent is not started and
the exchange is still open; unfreeze it and confirm it is started and collects the exchange.

**Acceptance Scenarios**:

1. **Given** an agent in the middle of a turn, **When** the person freezes it, **Then** the turn finishes and is recorded, and
   then the process ends. The turn is never cut off.
2. **Given** a frozen agent with an open exchange, **When** the broker or the computer restarts, **Then** the agent is not
   started and the exchange is still open.
3. **Given** a frozen agent, **When** somebody sends it a request, **Then** the request is stored and shown as waiting on a
   frozen agent, and nothing starts the agent.
4. **Given** a frozen agent, **When** the person unfreezes it, **Then** it is started if it owes work, and not otherwise.
5. **Given** a freeze made on one machine, **When** the console is open on another, **Then** both show the agent as frozen.
6. **Given** an agent open outside Parley, **When** the person looks at its node, **Then** Freeze is not offered, because no node
   can end it (SPEC.md §11.7). The graph says so.

---

### User Story 3 - Freeze or unfreeze everything in a project (Priority: P3)

Before shutting down for the night, the person freezes every agent in a project in one act, and unfreezes them in one act in
the morning, without ticking agents one by one.

**Why this priority**: a convenience over story 2.

**Independent Test**: freeze a project with three agents; all three read as frozen and none starts after a restart; unfreeze
returns them.

**Acceptance Scenarios**:

1. **Given** a project, **When** the person freezes all, **Then** each agent is frozen as in story 2 and the result says how many.
2. **Given** an agent already frozen, **When** freeze-all runs, **Then** it is counted once and nothing changes for it.

---

### Edge Cases

- An agent is frozen while a background task it started is still running. The freeze waits for the turn and the process ends;
  the background work ends with it. The graph says so before the person confirms.
- The frozen agent's process does not end at once. The graph shows *freezing* until the node reports it ended, and never
  shows *frozen* for a process still running.
- A freeze on an agent whose node is away. The freeze is recorded and takes effect when the node returns; until then the graph
  says the process could not be reached.
- A freeze on an agent that is not running and owes nothing. It is recorded; Unfreeze clears it.
- The person closes a conversation that a frozen agent owes. Closing works as it always does; the freeze stays.
- A deployment with many agents or conversations. The graph groups by project and says how many it has not drawn, as the lists do.
- The agent's repository is enrolled in several projects. The freeze covers all of them (see Decisions), and the confirmation names them.
- A request is sent to a frozen agent by another agent, which gets no answer for a long time. Nothing times out; the
  exchange is shown to the person as waiting on a frozen agent, not as stalled.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Every console page MUST offer a collapsed tab that opens the graph panel, and the person MUST be able to pin it so it
  stays open across pages. The pin and open state are kept in the browser only.
- **FR-002**: The graph MUST show projects, agents, conversations, spin-off links to their parent, and each agent's state and
  background-task count, drawn from what the brokers already freeze, with no new charting dependency.
- **FR-003**: Every node MUST carry its state in text a screen reader reads, and the graph MUST have a plain list alternative
  carrying the same facts.
- **FR-004**: The collapsed panel MUST read nothing from a broker; the graph is read when it is opened, and again at the
  console's ordinary refresh interval only while it is open.
- **FR-005**: The graph MUST NOT show anything an agent is not allowed to see. It is a person's view, and nothing in it
  is reachable by an agent operation.
- **FR-006**: A person MUST be able to freeze and unfreeze an agent from its node, and a freeze MUST be stored by the broker so that
  every surface shows it and it survives a restart of the broker and of the computer. A freeze covers the agent in every
  project on its machine that it serves, and the act names those projects before it is done.
- **FR-007**: A freeze MUST let the current turn finish, and MUST NOT interrupt or kill a turn.
- **FR-008**: While an agent is frozen, nothing MUST start it: not the restart wake (SPEC.md §11.1), not a new message, not a
  person's Start. Unfreeze is the only way back.
- **FR-009**: Freezing MUST NOT close, move, answer or discard any exchange, and MUST NOT change what any agent is owed.
- **FR-010**: A message to a frozen agent MUST be stored and shown as waiting on a frozen agent. No agent is told the other is
  frozen beyond what the roster already shows of state; the person's freeze is not an instruction to anyone.
- **FR-011**: Freeze and unfreeze MUST be operator operations only, on a surface for a person, listed in SPEC.md §10.2, and MUST
  NOT be available on any agent operation (`Agents` service). Freeze and unfreeze of one agent and of a whole project are the same
  implementation on the CLI, the terminal and the web console.
- **FR-012**: Freeze-all and unfreeze-all for a project MUST report how many agents each changed.
- **FR-013**: The panel MUST be usable at phone width and in dark mode, and MUST NOT be framed or framed into another page.
- **FR-014**: The schema change MUST be a new migration that keeps every row, and SPEC.md, `docs/` and the plugin's prose
  MUST be updated with it.

### Key Entities

- **Freeze**: a person's standing decision that an agent is not started. Belongs to one agent, is recorded in each project it serves, records who
  (the person's seat) and when, and is cleared only by Unfreeze.
- **Graph node**: a project, an agent, a conversation, or an agent's background work; edges are *is in*, *spun off from*
  and *is running*.
- **Panel state**: whether the panel is open and pinned, per browser, never stored by the broker.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A person can say which agents are working, in which conversations and projects, within 10 seconds of opening
  the panel on a deployment of 3 projects and 20 agents.
- **SC-002**: With one agent frozen, 100% of the broker restarts in a test of 20 leave it unstarted with its exchanges still open.
- **SC-003**: Freezing an agent mid-turn never loses the turn's reply: 100% of frozen turns in a test of 20 are recorded.
- **SC-004**: Freezing or unfreezing takes two actions from any page (open the tab, choose the act).
- **SC-005**: Closed, the panel adds no request to a broker; open, it adds one read per refresh per project.

## Assumptions

- The graph is an operator view on the web console. The terminal gets the freeze and unfreeze acts (the surfaces rule), and
  a drawn graph is not required there.
- Every conversation is drawn, closed ones muted, with a control to hide them. A project with very many is drawn in pages
  of conversations, never silently cut: the graph says how many it has not drawn.
- Which conversation an agent's running turn is for is not known (SPEC.md §11.6), so the graph joins an agent to the
  conversations it owes work in, and says *working* on the agent only.
- Sub-agents are the background tasks an agent has running (a count), plus spin-off conversations. Parley has no other
  notion of a sub-agent.
- The freeze is stored in each affected project's store, so it travels with that store's backup and restore.
- Closing a conversation remains the way to stop work for good (SPEC.md §11.1); a freeze is a pause, never an end.
- A freeze is a recorded reduction in the standing rule that a restart wakes whoever owes work, and is argued as such in the plan.
- The branch is `035-material-3-web-ui`, as the owner asked; the git extension's branch hook was not run.

## Decisions (owner, 2026-11-20)

- **A freeze is for a reboot, so a logon lifts it.** Every start of the broker gives it a GUID, stored with each freeze it makes.
  A start that finds a freeze made under another GUID treats it as a new logon or reboot: the freeze lapses and the agent is started
  if it owes work. A freeze the person marked **Do not unfreeze on logon** (`keep_frozen`) is not lifted by a start and must be
  unfrozen by hand. Unfreezing forgets the choice. (The owner chose this over reading the Windows session ID, which Windows
  reuses between logons, and which would have needed a Windows-only reading. The cost is that any restart of the broker, an
  install or a crash included, lifts an unkept freeze.)

- **A freeze is per agent, across every project on the machine that the agent serves.** One Freeze act records the freeze in
  each such project's store, so the process ends and stays ended whichever project's work or restart would have woken it.
  Unfreeze clears it in all of them. Freezing from project A therefore stops the agent's work for project B too, and the
  confirmation says which projects are affected before the person agrees.
- **The graph draws every conversation, closed ones included.** Closed and archived ones are drawn muted and labelled in
  words, and a control in the panel hides them (kept in the browser). The plain list alternative carries them all.
