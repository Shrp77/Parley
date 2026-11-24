# Feature Specification: A person stops or restarts an agent

**Feature Branch**: built on `035-material-3-web-ui`
**Created**: 2026-11-14
**Status**: Built
**Input**: User description: "Should we also add broker controls in the web and terminal UIs? Like restarting an agent
from the broker or shutting down an agent type thing?" Then: "Go ahead and build the functionality."

Written by hand after the change was agreed in conversation. Spec Kit's commands were not run for it.

## Why

On 2026-11-14 the hosted node dropped its connection and every agent in two projects read as *stranded*. The fix for that
was separate (the hosted node now rejoins by itself, 0.58.22). It showed a gap: a person had no way to stop an agent that
is stuck, or restart one so it reads a changed launch configuration, except Task Manager.

SPEC.md §11.1 said *"Three things end a resident agent, and nothing else may."* A person's stop is a fourth, so it is a
spec change, argued in §11.1.

## Decisions

- **Wait for the turn, never cut it short.** The node waits for the agent's turn lock, as a refresh does. A turn is
  bounded by the node's patience (1200 s), so the wait is bounded. Killing mid-turn would throw away the most of what the
  session had learned, and would race the reply.
- **Ends a process, never work.** What the agent owes stays owed; its conversations are untouched. A stopped agent is
  started again by the next work for it, because only retirement gates routing (§14). The words say so, and point to
  Retire for keeping one out.
- **Only a process Parley started.** One open outside Parley (§11.7) belongs to whoever opened it; no node holds it.
  Surfaces do not offer either act for it, and the broker refuses it.
- **Restart does not start a stopped agent.** That is Start's. Restart on an agent that is not running answers false.
- **No typed name.** Nothing is destroyed. The deliberate act is choosing the act from a description that names the loss,
  with keeping it running offered first (terminal) or on a separate confirmation page (web).

## Requirements

- **FR-001** Operators gain `Stop(StopRequest)` and `Restart(StopRequest)`, returning `DidItReply`. `changed` is true when
  the agent's node was asked, false when nothing runs under the name.
- **FR-002** Both are refused (`FAILED_PRECONDITION`) for the person seat, the facilitator, an agent open outside Parley,
  and an agent whose machine has no node joined that can start it; the last names `parley up`.
- **FR-003** The node command `StopAgent { agent, because, identity, restart }` stops a resident the node started once its
  turn is over, and for a restart starts it again with its launch configuration read afresh and gives it a turn.
- **FR-004** Sending it releases the broker's pending wake for the agent, so the next work starts it.
- **FR-005** The terminal's agent row menu offers *Restart* and *Stop* on a running agent Parley started, each behind a
  description with *Keep it running* first. The web console's agent page offers both in place of *Start* for such an
  agent, each through the Act page's description and a POST.
- **FR-006** Both surfaces read their words from `Parley.Client.Rendering.StoppingWords`.
- **FR-007** `parley stop` and `parley restart` exit 64 and name the terminal, as every other moved operator verb does.
- **FR-008** Protocol 0.16: the wire gained a node command.

## Tests

- `Parley.Server.Tests.StopAndRestartTests`: the node is sent the command; nothing is sent for an agent not running; open
  outside Parley, no node, the person and Parley are refused.
- `Parley.Cli.Tests.NodeStopTests`: a stop ends the process and starts nothing; a restart replaces it; an agent the node
  is not running is reported, not stopped.
- `Parley.Web.Tests.StoppingAnAgentTests`: offered only for a running agent Parley started; described on GET, done only on
  POST.
- `Parley.Terminal.Tests.RowMenuTests`: the row menu offers both only when Parley started the running agent.
