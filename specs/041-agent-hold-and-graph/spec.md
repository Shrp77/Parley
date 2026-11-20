# Feature Specification: A pinned graph of the work, and holding an agent

**Feature Branch**: built on `035-material-3-web-ui`
**Created**: 2026-11-20
**Status**: Draft
**Input**: User description: "A top-level graph that can be pinned to any page. It shows a visual node representation of the
agents, projects and conversations they are in, including sub-agents. I want to see what is being worked on, by which
agents, for which project, and be able to tell it to pause the work and save its state, so it can be resumed after a
restart of the computer." The owner chose **hold** for pause (the agent's process ends after its current turn and nothing
wakes it until a person resumes it; a turn is never cut off mid-way) and a **pinned panel on every page** for the graph.

## What already exists

- Everything the graph shows is already known to the broker: agents and their projects, conversations with who is in
  them, a spin-off's parent, which agents are working, idle or waiting on background work, and how many background tasks
  each has (SPEC.md §11.6). A turn report does not say which conversation a turn is for.
- Work is already durable. An open exchange lives in the store, and a restart wakes every agent that owes one
  (SPEC.md §11.1, the owner's decision of 2026-11-18). Stopping an agent ends its process and never its work.
- **What is missing is a hold.** Stop does not keep an agent stopped: the next restart or the next message starts it
  again. And nothing shows the whole picture on one screen.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - See what is being worked on, on any page (Priority: P1)

A person on any console page opens a small tab at the edge of the screen and sees a graph: each project, the agents in it,
the conversations those agents are in, and for each agent whether it is working, idle, waiting on background work, or held.
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

### User Story 2 - Hold an agent from the graph, and resume it (Priority: P2)

From an agent's node the person chooses *Hold*. The node lets the agent finish its current turn, then ends its process.
The agent reads as *held* everywhere. Nothing starts it: not a restart of the broker or of the computer, not a new message
to it. Messages for it wait. Its open exchanges stay open. Later the person chooses *Resume*, and the agent is started
if it owes work, as it would be after any restart.

**Why this priority**: it is the pause the person asked for. It needs a stored state, so it follows the read-only graph.

**Independent Test**: hold an agent that owes an open exchange, restart the broker, and confirm the agent is not started and
the exchange is still open; resume it and confirm it is started and collects the exchange.

**Acceptance Scenarios**:

1. **Given** an agent in the middle of a turn, **When** the person holds it, **Then** the turn finishes and is recorded, and
   then the process ends. The turn is never cut off.
2. **Given** a held agent with an open exchange, **When** the broker or the computer restarts, **Then** the agent is not
   started and the exchange is still open.
3. **Given** a held agent, **When** somebody sends it a request, **Then** the request is stored and shown as waiting on a
   held agent, and nothing starts the agent.
4. **Given** a held agent, **When** the person resumes it, **Then** it is started if it owes work, and not otherwise.
5. **Given** a hold made on one machine, **When** the console is open on another, **Then** both show the agent as held.
6. **Given** an agent open outside Parley, **When** the person looks at its node, **Then** Hold is not offered, because no node
   can end it (SPEC.md §11.7). The graph says so.

---

### User Story 3 - Hold or resume everything in a project (Priority: P3)

Before shutting down for the night, the person holds every agent in a project in one act, and resumes them in one act in
the morning, without ticking agents one by one.

**Why this priority**: a convenience over story 2.

**Independent Test**: hold a project with three agents; all three read as held and none starts after a restart; resume
returns them.

**Acceptance Scenarios**:

1. **Given** a project, **When** the person holds all, **Then** each agent is held as in story 2 and the result says how many.
2. **Given** an agent already held, **When** hold-all runs, **Then** it is counted once and nothing changes for it.

---

### Edge Cases

- An agent is held while a background task it started is still running. The hold waits for the turn and the process ends;
  the background work ends with it. The graph says so before the person confirms.
- The held agent's process does not end at once. The graph shows *holding* until the node reports it ended, and never
  shows *held* for a process still running.
- A hold on an agent whose node is away. The hold is recorded and takes effect when the node returns; until then the graph
  says the process could not be reached.
- A hold on an agent that is not running and owes nothing. It is recorded; Resume clears it.
- The person closes a conversation that a held agent owes. Closing works as it always does; the hold stays.
- A deployment with many agents or conversations. The graph groups by project and says how many it has not drawn, as the lists do.
- The agent's repository is enrolled in several projects. The hold covers all of them (see Decisions), and the confirmation names them.
- A request is sent to a held agent by another agent, which gets no answer for a long time. Nothing times out; the
  exchange is shown to the person as waiting on a held agent, not as stalled.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Every console page MUST offer a collapsed tab that opens the graph panel, and the person MUST be able to pin it so it
  stays open across pages. The pin and open state are kept in the browser only.
- **FR-002**: The graph MUST show projects, agents, conversations, spin-off links to their parent, and each agent's state and
  background-task count, drawn from what the brokers already hold, with no new charting dependency.
- **FR-003**: Every node MUST carry its state in text a screen reader reads, and the graph MUST have a plain list alternative
  carrying the same facts.
- **FR-004**: The collapsed panel MUST read nothing from a broker; the graph is read when it is opened, and again at the
  console's ordinary refresh interval only while it is open.
- **FR-005**: The graph MUST NOT show anything an agent is not allowed to see. It is a person's view, and nothing in it
  is reachable by an agent operation.
- **FR-006**: A person MUST be able to hold and resume an agent from its node, and a hold MUST be stored by the broker so that
  every surface shows it and it survives a restart of the broker and of the computer. A hold covers the agent in every
  project on its machine that it serves, and the act names those projects before it is done.
- **FR-007**: A hold MUST let the current turn finish, and MUST NOT interrupt or kill a turn.
- **FR-008**: While an agent is held, nothing MUST start it: not the restart wake (SPEC.md §11.1), not a new message, not a
  person's Start. Resume is the only way back.
- **FR-009**: Holding MUST NOT close, move, answer or discard any exchange, and MUST NOT change what any agent is owed.
- **FR-010**: A message to a held agent MUST be stored and shown as waiting on a held agent. No agent is told the other is
  held beyond what the roster already shows of state; the person's hold is not an instruction to anyone.
- **FR-011**: Hold and resume MUST be operator operations only, on a surface for a person, listed in SPEC.md §10.2, and MUST
  NOT be available on any agent operation (`Agents` service). Hold and resume of one agent and of a whole project are the same
  implementation on the CLI, the terminal and the web console.
- **FR-012**: Hold-all and resume-all for a project MUST report how many agents each changed.
- **FR-013**: The panel MUST be usable at phone width and in dark mode, and MUST NOT be framed or framed into another page.
- **FR-014**: The schema change MUST be a new migration that keeps every row, and SPEC.md, `docs/` and the plugin's prose
  MUST be updated with it.

### Key Entities

- **Hold**: a person's standing decision that an agent is not started. Belongs to one agent, is recorded in each project it serves, records who
  (the person's seat) and when, and is cleared only by Resume.
- **Graph node**: a project, an agent, a conversation, or an agent's background work; edges are *is in*, *spun off from*
  and *is running*.
- **Panel state**: whether the panel is open and pinned, per browser, never stored by the broker.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A person can say which agents are working, in which conversations and projects, within 10 seconds of opening
  the panel on a deployment of 3 projects and 20 agents.
- **SC-002**: With one agent held, 100% of the broker restarts in a test of 20 leave it unstarted with its exchanges still open.
- **SC-003**: Holding an agent mid-turn never loses the turn's reply: 100% of held turns in a test of 20 are recorded.
- **SC-004**: Holding or resuming takes two actions from any page (open the tab, choose the act).
- **SC-005**: Closed, the panel adds no request to a broker; open, it adds one read per refresh per project.

## Assumptions

- The graph is an operator view on the web console. The terminal gets the hold and resume acts (the surfaces rule), and
  a drawn graph is not required there.
- Every conversation is drawn, closed ones muted, with a control to hide them. A project with very many is drawn in pages
  of conversations, never silently cut: the graph says how many it has not drawn.
- Which conversation an agent's running turn is for is not known (SPEC.md §11.6), so the graph joins an agent to the
  conversations it owes work in, and says *working* on the agent only.
- Sub-agents are the background tasks an agent has running (a count), plus spin-off conversations. Parley has no other
  notion of a sub-agent.
- The hold is stored in each affected project's store, so it travels with that store's backup and restore.
- Closing a conversation remains the way to stop work for good (SPEC.md §11.1); a hold is a pause, never an end.
- A hold is a recorded reduction in the standing rule that a restart wakes whoever owes work, and is argued as such in the plan.
- The branch is `035-material-3-web-ui`, as the owner asked; the git extension's branch hook was not run.

## Decisions (owner, 2026-11-20)

- **A hold is per agent, across every project on the machine that the agent serves.** One Hold act records the hold in
  each such project's store, so the process ends and stays ended whichever project's work or restart would have woken it.
  Resume clears it in all of them. Holding from project A therefore stops the agent's work for project B too, and the
  confirmation says which projects are affected before the person agrees.
- **The graph draws every conversation, closed ones included.** Closed and archived ones are drawn muted and labelled in
  words, and a control in the panel hides them (kept in the browser). The plain list alternative carries them all.
