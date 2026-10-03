# Contract: what changes on each surface

## §10.2 operator operations (new)

All are operator operations: available to the person running the deployment, never to an agent, and bound on
**both** surfaces (principle 2.9; `DriftTests` requires every `BrokerClient` operation on both).

| Operation | Takes | Returns | Refuses |
|---|---|---|---|
| `facilitation()` | — | The Auto Accept switch, and every agent's current ownership question: state, the answer as written, the flattened answer, why it waits | — |
| `set_auto_accept(on)` | `on` | The switch as now set | — |
| `ask_again(agent)` | An agent | Whether a question was opened | The facilitator; a seat; a retired agent; an agent with a question `asked`, `asked_back`, `answered` or `waiting` (a waiting answer would be lost) |
| `reply_to_asked_back(agent, text)` | An agent, the person's words | Whether it was sent | No question in `asked_back`; text failing the message cap or refused characters |
| `discard_answer(agent)` | An agent | Whether it was discarded | No question `waiting` |

`record_ownership` (020) is unchanged in shape and gains the source `automatic`, which **only the facilitator
loop may record**: an operator call passing it is refused, because a person's decision is never recorded as the
machine's.

`registry()` gains `owns_source` per row. The facilitator's row is the one whose adapter is `parley-facilitator`; storage ties that adapter to the reserved role, so no separate flag states it.

## §10.1 agent operations

**None added, none changed.** `agents()` omits the facilitator. Checked by `AgentsCannotSetTheirOwnLineTests`
(no Agents-service name contains "own") and `SpecifiedOperationTests` (every §10.1 name is an MCP tool; none of
the above may be).

## The node command (Nodes service)

| Command | Sent when | The node does |
|---|---|---|
| `RefreshAgent { agent }` | An agent's **first** line was accepted automatically, it owes no open exchange, and it has no attention pending | If it started that agent: waits until no turn is in progress, then stops it and starts it again, reading its launch configuration afresh. If it did not start it: nothing, and says so. |

Sent at most once per agent, ever (`ownership_questions.restart_sent`). A node that does not know the command
ignores it, as it ignores every unknown command today (`NodeCommand.cs:208-213`); the line then stays pending.

## The terminal

| Where | Action | Key |
|---|---|---|
| Project view, **Agent** menu | Review ownership — the list of agents, their line, its provenance, and any question and its state | `v` |
| Ownership review | Accept a waiting answer, edit it, write a line, discard it, ask again, reply to an agent that asked back | in the review |
| **Configuration** view, reached from the **Project** menu (from the projects list or inside a project) | Auto Accept on / off, showing the current state. *Planned* as a `Project` menu action under `a`; *built* (0.35) as a setting in the project's Configuration view, opened with F3, because a global letter is shadowed by whichever pane already uses it | `F3`, then Enter on Auto Accept |

020's "Ask what this agent owns" (`k`) and "Ask every agent" (`e`) become **Ask again**, sent by Parley, needing
no seat. The seat-required refusal is removed.

## The web UI

The same review and switch on the project page, each action described on a GET before a POST performs it
(`EveryActionHasADoorTests`).

## What every surface shows about the facilitator

- Named **Parley**, never as an agent or a seat, and never offered for retire, purge, instruct or start. Its stored
  role is `parley-facilitator`, not `parley`, which is an ordinary role this repository's own agent uses.
- Never a recipient: a Request addressed to it, or a Clarify on an exchange it did not open, is refused.
- A message it sent reads as Parley's; a reply a person wrote reads as that person's words sent through Parley.
