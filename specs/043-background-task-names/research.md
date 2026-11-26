# Research: Named background tasks

## R1. What the runner says about each background task

**Decision**: read each task's identity, kind and description from the runner's own `background_tasks_changed` event.
No matching to tool calls is needed.

**Found** (2026-11-26, Claude Code 2.1.332). The runner was started as a node starts it (`-p --input-format stream-json
--output-format stream-json --verbose`), in an empty folder outside any repository with every `PARLEY_` variable
cleared. Its one prompt asked it to start a background shell command and a background sub-agent, then end its turn.
Excerpt of what it wrote, with session ids removed:

```jsonl
{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"af7da04eab7667f03","run_id":"0mwxtqj6h-ec48e1d9","task_type":"local_agent","subagent_type":"general-purpose","description":"Count to three"}]}
{"type":"system","subtype":"task_started","task_id":"af7da04eab7667f03","tool_use_id":"toolu_01HsR4UPmXdp9WaTCJpbaWoY","description":"Count to three","is_backgrounded":true,"task_type":"local_agent", ...}
{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"af7da04eab7667f03","task_type":"local_agent","description":"Count to three"},{"task_id":"b8n705a3e","task_type":"local_bash","description":"Wait twenty-five seconds"}]}
{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"b8n705a3e","task_type":"local_bash","description":"Wait twenty-five seconds"}]}
{"type":"system","subtype":"background_tasks_changed","tasks":[]}
```

- Every running task is listed whole, each time the set changes: `task_id`, `task_type` (`local_agent` for a sub-agent,
  `local_bash` for a background command), and `description`, the words the agent gave the tool call that started it.
- `task_started`, `task_updated` and `task_notification` carry the same `task_id`. The node needs none of them: the
  list is complete in each `background_tasks_changed`.
- The agent learns the id from its own tool result (`Command running in background with ID: b8n705a3e`, or the
  sub-agent's id), so it can name a task to Parley by that id.

**Alternatives considered**: matching `task_started.tool_use_id` to the `tool_use` block that started it. That is not
needed, because the description is already on the task.

## R2. Where a description and a link come from

**Decision**: from the agent, by a new agent operation, `describe_background(task_id, name, description, link)` (the adapter's
`parley_describe_background`, the wire's `DescribeBackgroundTask`).
The person chose this (exchange #58).

**Rationale**: the runner's description is a short label (the Agent tool asks for 3 to 5 words). It carries no longer
description and no link. Only the agent knows what the work is for, and which exchange, document or ticket says more.

**How it fits the boundaries**:
- **It is the agent's own work, described to the person.** It adds no authority over anybody. It is shown only on
  operator surfaces (FR-004), like the count.
- **The reply carries no turn information.** It says only whether the description was kept, and whether a link was
  dropped. It never says whether the task is running: a description may arrive before the node's report of the task,
  and is shown once the node lists that id (R3). `AgentsCannotSeeTurnsTests` holds this.
- **It is kept only for the agent's own ids.** The broker keys it by the attached agent's name, so an agent can
  describe only its own tasks.

**Alternatives considered**:
- The first 300 characters of the sub-agent's prompt. Rejected: the prompt is written to a model, long and prosaic,
  the opposite of what the person asked for. It may also hold what the person did not mean to show.
- A marker the agent writes into its tool description, such as `name | description | link`. Rejected: addressing and
  meaning are never parsed out of an agent's text (CLAUDE.md, message payloads are inert).

## R3. Holding descriptions until the task shows, and when they go

**Decision**: the broker holds each agent's descriptions in memory keyed by task id, at most 200 per agent, the oldest
dropped first. A description is shown only while the node's latest report lists its id. The words for the tasks a report of
none ends go with it; all of them go when a new process is launched under the name (the launch token differs) or the
broker restarts. A node that leaves takes its list and leaves the words: when it comes back with the same process, its
next report names the same tasks and the words show again, since the agent will not describe them a second time.

**Rationale**: the agent's call and the node's report race, and either can land first. Holding the description until
the count clears covers both orders with no timer (nothing times out). The 200 bound stops an agent describing ids
without end. A real session runs a handful of tasks at a time.

## R4. Bounds and neutralising

**Decision**: one Core type, `BackgroundTaskText`, cuts and neutralises. A name is at most 64 characters and a
description at most 300 (both the person's numbers). A link is at most 500. Each is cut at a character boundary and
marked with an ellipsis, with control, format and line-separator characters made spaces. The runner's own label is
held to the name's bound.

**Rationale**: the broker is the one place every word passes, so it neutralises there (FR-005). Surfaces still encode
on output as they do for any text. Core already holds the name grammar; this has no I/O.

## R5. Which links are shown

**Decision**: the broker keeps a link only when it is one of:
- `exchange #N`, an exchange of the same project;
- `conversation:<id>`, a conversation of the same project, held to the conversation-name grammar;
- `file:<name>`, a file placed in Parley, held to the placed-name grammar;
- an absolute `https` address of a named host with a dot in it (no address in numbers, no `localhost`), no user info,
  at most 500 characters as given and as followed, kept with its host in punycode.

Anything else is dropped and the reply says so. Surfaces draw an https link with its host beside it, open it in a new
tab with `rel="noopener noreferrer"`, and draw the others as console links.

**Rationale**: the console already draws agents' messages as live links. A task's link sits in the person's own
view, so it is held tighter: no `javascript:`, `data:` or `http:` address, and no address that hides where it goes.

## R6. More tasks than room

**Decision**:
- **The graph** (an SVG picture) draws up to five tasks under an agent, then a box saying how many more, linked to
  the agent's page.
- **The agent's page** lists every task in a list that scrolls after five, paged twenty to a page (`?tasks=2`), and
  says "page 2 of 3, 47 tasks".
- **The terminal**: the agent's row shows the first task's name and how many more. *Background work* (Alt+B on the
  agent's row) opens a read-only list of them all, which scrolls, Page Up and Page Down moving a screen at a time.
  The plan had them move twenty; the list takes those keys itself, so twenty would need keys of its own, and the
  change is to be put to the person (spec FR-014).

**Rationale**: a picture cannot scroll a list inside a box without becoming a second scrolling surface inside one the
person already drags. The page is where a long list belongs. This meets FR-014 as amended, and the spec records the terminal's
change as one the person has not yet agreed.

## R7. Protocol and version

**Decision**: protocol 0.20 becomes 0.21: the node's report, the registry row and the agent service all change, and
a node and its broker must match (§4.0). Version 0.65.0, a new agent operation. No schema change: nothing is stored.
