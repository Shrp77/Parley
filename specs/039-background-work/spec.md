# Feature Specification: An agent waiting on its own background work is not idle

**Feature Branch**: built on `035-material-3-web-ui`
**Created**: 2026-11-14
**Status**: Built
**Input**: User description: "Several times Parley has shown an agent as idle, but it's actually waiting on sub-agents to
finish their work. Any way we can see this and not report the agent as idle?" The owner chose to show the background work
without holding the turn open.

Written by hand. Spec Kit's commands were not run for it.

## What was found

Probed against Claude Code 2.1.325, run as a node runs it (`claude -p --input-format stream-json --output-format
stream-json --verbose`, standard input kept open). A turn that starts a command in the background:

1. writes `system/background_tasks_changed` listing one task, then the turn's `result` while the task runs;
2. when the task finishes, writes `system/task_notification`, `background_tasks_changed` with no tasks, an `init`, and a
   second `result` with `origin.kind = "task-notification"`, with no prompt from anyone.

A prompt sent during that second turn is answered after it, with its own untagged `result`.

Claude Code starts a sub-agent from `claude -p` in the background by default, so this is common.

Two defects followed:

- **Idle while busy.** The node treated the first `result` as the end of the work.
- **Off by one.** The node read output only inside turns it gave. The second `result` waited in the queue, and the next
  turn took it as its own reply at once: the agent read as idle while it worked on the person's message, and every
  reply after went to the turn after its own.

## Decisions

- **Show, don't hold.** A given turn still ends at its reply, so mail is not held behind background work. The node
  reports the background-task count, and the agent's own turns as turns.
- **Never mistake the agent's own reply for a given turn's.** A `result` tagged `task-notification` is the agent's own
  turn's, whatever is waiting.
- **The agent's own turn holds the agent like a given one,** so a turn given meanwhile queues behind it, as the runner
  would queue the prompt.
- **The count belongs to the process.** It reaches every project whose node starts the checkout. The broker holds it in
  memory with the node that said it; a node leaving, or a new process launched under the name, clears it.
- **A background shell command counts,** a dev server included. An agent with one reads *waiting on 1 background task*
  for as long as it runs, which is true.

## Requirements

- **FR-001** `ResidentAgent` reads every output event as it arrives. It tracks the live count from
  `background_tasks_changed`; it begins accounting for a turn of its own at an `init` when no given turn holds it and it
  has answered before; that turn ends at a `result` tagged `task-notification`, the process's end, or the node's turn
  patience. Tagged results never reach a given turn.
- **FR-002** `OwnWork` callbacks report the agent's own turn's start and end and each change of count, in order, off the
  output pump; a failure is dropped.
- **FR-003** `TurnReport` gains `background { tasks }`. A turn of the agent's own is reported with `started` and `ended`.
- **FR-004** The broker accepts a count only as it accepts a turn report, holds it in memory, and shows it on
  `RegistryRow.background_tasks` while the agent runs as a process its node started.
- **FR-005** Surfaces say *waiting on N background tasks* (agents list and room, mark `◈`) and *Background work* (Inbox
  chip) in place of *idle*.
- **FR-006** Protocol 0.17.

## Not covered

- A turn of the agent's own that begins while a given turn still holds the agent (its `init` arrives before the given
  turn lets go) is not reported as its own turn. The given turn covered most of it, and its result is still never taken
  as a given turn's reply.
- The node's fan-out to several projects (`NodeCommand.Watched`) has no test of its own; the parts either side of it do.

## Tests

- `Parley.Headless.Tests.BackgroundWorkTests`: the agent's own turn is told and the next given turn reads its own reply;
  a turn given during the agent's own turn waits and reads its own reply; the tag and the task list are parsed.
- `Parley.Server.Tests.BackgroundWorkTests`: the count is on the registry row until zero; a new process clears it and the
  same process re-announced keeps it; a node leaving clears it; only the agent's machine's node is heard; an agent open
  outside Parley shows none.
- `Parley.Client.Tests.InboxViewsTests.An_agent_waiting_on_its_own_background_work_is_never_idle_anywhere`.
