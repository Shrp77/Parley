# Feature Specification: Named background tasks

**Feature Branch**: `043-background-task-names`
**Created**: 2026-11-25
**Status**: Draft
**Input**: User description: "Named background tasks. Today a node reports only how many background tasks an agent has
running (SPEC.md §11.6). The person wants to see what each background task (a sub-agent or a background command) is
doing, so the web console's live graph of what Parley is doing now (feature 042) can show each working agent's sub-agents
as named children, such as 'Reading the VAT rules' or 'Running the tax tests', instead of 'Background task 1, 2'." The
person chose this as option (b) in exchange #54 of the conversation UI-refresh-and-fixes, and asked in exchange #58 for a
name, a description and a link for each task, in plain words, and for a paged list.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The person sees what each sub-agent is doing (Priority: P1)

An agent working on the person's request starts four sub-agents to look into different parts of it, then ends its turn
while they run. Today the person sees "waiting on 4 background tasks". With this feature they see the four, each by what
it was started to do: "Reading the VAT rules", "Checking the invoice schema", "Running the tax tests", "Drafting the
reply". As each finishes, it leaves the list.

**Why this priority**: it is the whole of what the person asked for. The count says that work is under way; the names say
whether it is the work they expected.

**Independent Test**: with a node running an agent that starts named background work, the person's graph and the agent's
page list each running task by its name, and the list follows as tasks start and finish.

**Acceptance Scenarios**:

1. **Given** an agent with no background work, **When** it starts a sub-agent described as "Reading the VAT rules",
   **Then** within a few seconds every surface that shows the agent lists one background task, "Reading the VAT rules".
2. **Given** an agent with three named background tasks running, **When** one finishes, **Then** the surfaces list the
   other two, by their names, and the count says two.
3. **Given** an agent whose runner gives a background task no description, **When** it starts, **Then** the surfaces list
   it with a plain stand-in ("Background task") rather than leaving it out, so the count and the list always agree.

---

### User Story 2 - Each task says plainly what it is doing, with a link to more (Priority: P1)

The person reads a task at a glance. Its **name** is short and says what is being done, in the person's terms and not
the agent's: "Checking VAT on gift cards", not "Investigating the tax-calculation module's handling of stored-value
instruments". Its **description** says a little more, in one or two plain sentences, for when the person wants it. And
where something says more still (the exchange the work is for, a document, a ticket or work item), the task links to it.

**Why this priority**: the person asked for it (exchange #58). A name in the agent's own long, prosaic words is as hard
to read as no name, and a link saves the person guessing what the work belongs to.

**Independent Test**: an agent starts a sub-agent with a name, a description and a link to an exchange. The person's
graph shows the name with the link; the agent's page shows the name, the description and the link.

**Acceptance Scenarios**:

1. **Given** an agent starting a background task, **When** it gives a name, a description and a link, **Then** the graph
   shows the name with the link, and the agent's page shows all three.
2. **Given** a task with a name only, **When** it is shown, **Then** it shows the name, with no empty description and no
   dead link.
3. **Given** a link that is not one of the kinds Parley shows (FR-012), **When** the task is reported, **Then** the link is
   dropped and the name and description are still shown.
4. **Given** an agent told how to describe its work (FR-013), **When** it names a task, **Then** the name says what is
   being done, verb first, in at most 64 characters.

---

### User Story 3 - Names stay the person's, never another agent's (Priority: P1)

The names say what an agent's own work is about, in the agent's own words. They are for the person running the deployment,
like the count and the turn standing they sit beside, and no agent reads them.

**Why this priority**: §10.1 withholds turn information from agents on purpose; a list of what another agent's sub-agents
are doing is the same kind of fact and would leak the same way.

**Independent Test**: an agent's view of the roster carries no background task names, before or after the feature; only
the operator's registry does.

**Acceptance Scenarios**:

1. **Given** an agent with named background tasks, **When** another agent asks who exists and what they own, **Then** its
   answer carries no task names and no count, as today.
2. **Given** a name that holds control characters, line breaks, markup or a very long description, **When** it is
   shown, **Then** it is drawn as plain text, on one line, cut to its bound, with nothing in it acted on.

---

### User Story 4 - Names go when the work does (Priority: P2)

The names live exactly as long as the count does: they are what the node last said, held in the broker's memory with the
node that said it.

**Why this priority**: a name left behind after its task ended, or after the agent was restarted, would tell the person
something false about what is being worked on.

**Independent Test**: stop the node, or launch a new process under the agent's name, and the names clear with the count.

**Acceptance Scenarios**:

1. **Given** an agent with named background tasks, **When** its node leaves, **Then** the names and the count clear
   together.
2. **Given** an agent with named background tasks, **When** a new process is launched under its name, **Then** the old
   names are gone, and only what the new process reports is shown.
3. **Given** a broker restart, **When** it comes back, **Then** no names are shown until a node reports them again, as
   with the count today.

---

### Edge Cases

- **Two tasks with the same name** are both listed; the list is of tasks, not of distinct names.
- **A name re-reported unchanged** changes nothing on screen and sends nothing more than the count's report does today.
- **More tasks than a surface has room for**: on the agent's page the tasks are in a list that scrolls after five and
  is paged twenty at a time, saying which page of how many and how many tasks in all; the terminal's list scrolls and
  the graph counts what it does not draw (FR-014). No task is dropped and the count is never lost.
- **A runner that never describes its tasks** behaves exactly as today, with every task a plain stand-in.
- **A node older than this feature** reports a count and no names; surfaces show the count's stand-ins, as today.
- **A name in a language or script the console does not expect** is shown as given, bounded by characters, not bytes.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: A node MUST report, for each background task its agent has running, a short name taken from the runner's
  own output: the description the agent gave the tool call that started the task (a sub-agent's, or a background
  command's). Where the runner gives none, the task's name is absent and surfaces show a plain stand-in. A name the
  agent gave the task itself (FR-011) takes the place of the runner's.
- **FR-002**: A node MUST report the names whenever the set of running tasks or their names change, in the same report
  that carries the count, so the count and the names never disagree.
- **FR-003**: The broker MUST hold the names beside the count, in memory, with the node that reported them, and MUST
  clear them when the count clears: when that node leaves, when a new process is launched under the agent's name, and on
  a broker restart. Nothing about them is written to the store.
- **FR-004**: The operator registry (SPEC.md §10.2, `registry()`) MUST carry each agent's running task names in its turn
  standing. The agent operations (§10.1) MUST NOT carry them, as they carry no turn information today.
- **FR-005**: A name MUST be at most 64 characters and a description at most 300, each cut at a character boundary and
  marked as cut, with control characters and line breaks neutralised before it is held. Neither is ever parsed,
  interpreted or used to address anything.
- **FR-006**: The broker MUST accept names only from a joined node for an agent on that node's machine, as it accepts
  the count today; anything else is ignored and nothing is stored.
- **FR-007**: The web console MUST show the names wherever it shows an agent's background work: the live graph's children
  under each working agent (feature 042), and the agent's own page. The terminal's agent row MUST show them too, so every
  surface the person uses says the same (CLAUDE.md, surfaces).
- **FR-008**: How an agent's state is derived (working, waiting on its background work, stopped, idle) MUST NOT change;
  the names describe the background work, never decide it.
- **FR-009**: SPEC.md §11.6 and §10.2 MUST say all of the above, in the same change that builds it.
- **FR-010**: A task MAY carry a description: one or two plain sentences saying what is being done, for the person.
  Surfaces show it where there is room (the agent's page, and the graph's tooltip and list), never in place of the name.
- **FR-011**: An agent MAY give a task's name, description and link itself (the person's choice, exchange #58), by an
  adapter operation it calls as it starts the work, matched to the task by the runner's task id or the
  tool call that started it. The runner gives only a short label, so a description and a link can come only from the
  agent. What it says is shown to the person only (FR-004).
- **FR-012**: A link MUST be one of: an exchange or conversation in the same project (drawn as `exchange #N` and opened in
  the console), a file placed in Parley, or an `https` address shown with its host, so the person sees where it goes
  before following it. Anything else is dropped. A link is at most 500 characters, and nothing follows one but the person.
- **FR-013**: The adapter's instructions MUST tell an agent how to describe background work: the name short, verb first,
  in the person's terms, saying what is being done rather than how; the description plain and brief; a link where one
  says more. Parley bounds what is said (FR-005) but cannot judge its wording; the person reads it.
- **FR-014**: Where a surface lists an agent's background tasks, the list MUST scroll once it holds more than five and be
  paged twenty to a page, saying which page of how many and the total, on the agent's page in the web console. The
  terminal's list of them (Alt+B on the agent's row) scrolls, Page Up and Page Down moving a screen at a time, with the
  total in its title, and the row names the first task and how many more. *This is a change from the person's twenty
  to a page (exchange #58), made while building, not yet agreed:* a terminal list already takes Page Up and Page Down
  itself, and paging it twenty at a time would need keys of its own on a surface where every key is declared. Every
  task is still reachable. To be put to the person. The graph, a picture the person drags, draws
  five under an agent and a box saying how many more, which opens the agent's page (research R6).

### Key Entities

- **Background task**: one piece of work an agent's runner started in the background for the current task (a sub-agent
  or a background command). It has a name (or none), and may have a description and a link. It lives from the runner's
  report that it started to the report that it ended.
- **Turn standing**: what a node last said about an agent's turns and background work, held by the broker in memory. It
  gains the list of running tasks' names beside the count it already carries.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The person can name what each of an agent's running sub-agents is doing from the graph alone, with no other
  page open.
- **SC-002**: A task's name appears on the person's surfaces within 5 seconds of the agent starting it, and leaves within
  5 seconds of it finishing.
- **SC-003**: The count and the number of names shown agree at every moment a surface is drawn: no task is shown without
  a name or stand-in, and no name outlives its task.
- **SC-004**: No agent, by any operation open to it, can read another agent's (or its own) background task names.
- **SC-005**: A name of any length or content draws on one line within its bound on every surface, with nothing in it
  acted on.
- **SC-006**: With fifty tasks running, the person can reach every one on every surface, and the total shown is fifty.

## Assumptions

- **To be verified first, before planning.** Whether the runner's `background_tasks_changed` event lists each task with a
  description of its own, or with an id only. If an id only, the node matches each id to the tool call it read earlier in
  the same output, whose input carries the description (the agent describes a sub-agent and a background command when it
  starts one). This is checked against the runner as feature 039 was, by running it as a node runs it and recording the
  lines, and the plan is written from what is found.
- The runner already describes most background work: an agent gives a sub-agent a short description when it starts one,
  and a background command usually carries one. A task without a description is the exception the stand-in covers.
- The bounds are the person's (exchange #58): 64 characters for a name, short enough for one line of the graph, and 300
  for a description. They are the owner's to change.
- **Settled by the person (exchange #58).** An agent gives a task's name, description and link itself (FR-011), since
  the runner's own label is all the runner gives. A task the agent says nothing about shows the runner's label.
- The wire, enums and protocol change freely before release (CLAUDE.md, state), so the node and broker change together and
  no older peer needs a fallback beyond FR-001's "no names, count only".
- Feature 042's graph page is the first surface to show the names. It is built to show the count's stand-ins until this
  feature lands, and names as soon as they arrive.
