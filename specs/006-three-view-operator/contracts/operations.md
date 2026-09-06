# Contract: the four new operations, and which surface binds what

This file is the diff against 005's `contracts/operations.md`, which is itself the diff against
004's. Everything those two established continues to hold and is not restated. **What is new here is
that this feature adds operations to the protocol**, which neither of them did.

## The two clients stay two clients

Unchanged since 004. `ClientFor(project)` is the Operators client and everything read *about* a
deployment comes from it; `AgentsFor(project)` is the Agents client and is reached only through
`SeatBook`. The four operations below are Operators, all of them, on both surfaces.

## The four new operator operations

Added to `SPEC.md` §10.2 and to the `Operators` gRPC service. **None appears on `Agents`, none
appears on `Broker`, and `SurfaceBoundaryTests` fails if any does.**

| Operation | What it does | Why it is §10.2 and not §10.1 |
|---|---|---|
| `archive(conversation)` | Marks a conversation archived. Durable; delivery unaffected. | It is a judgement about an operator's own view of a deployment. An agent archiving a conversation it is in would be deciding for every other operator. |
| `unarchive(conversation)` | Reverses it. | Same. |
| `destroy(conversation)` | Deletes a conversation and every message in it, for every participant. Cannot be undone. Leaves a tombstone of facts. | The same asymmetry as `purge`: it destroys peers' halves of something they may be relying on. §14's purge consequence is the precedent and the new one is written beside it. |
| `start(agent)` | Asks the node whose catalogue names the role to launch it now. | It causes a process to run on the operator's machine. An agent able to start another agent on demand is the orchestrator Principle II forbids. |

### Names, and the boundary they must not span

§10.1 holds `agents`, `ask`, `clarify`, `answer`, `notify`, `inbox`, `conclude`. None of the four
collides, and none is a near-miss of one — the trap `force_conclude` is named apart from `conclude`
to avoid, and which `tests/Parley.Spec.Tests` checks mechanically.

`destroy` rather than `purge`: `purge` takes an agent and is already spoken for, and two operations
sharing a name across different subjects is how the wrong one gets bound.

### `conversations()` changes shape slightly

Not a new operation. It gains an archived flag per conversation and an option on the request to
include archived ones. Everything the list and the detail panel need — identifier, participants,
message count, first and last activity, whether anything is open — §10.2 already specifies and
`ListConversations` already returns. `research.md` R6.

## What each surface binds

Both surfaces bind all four. That is the point of the feature being scoped to both: 005 added
`DriftTests` precisely so a one-sided addition fails the build, and four new operations bound by one
surface would have been the first thing to trip it.

| Operation | Terminal | Console | Note |
|---|---|---|---|
| `Archive` / `Unarchive` | yes | yes | project view, conversations list |
| `Destroy` | yes | yes | behind the door below |
| `Start` | yes | yes | called by enrolment, and offered on an agent that has never run |
| `Conversations` | yes | yes | now with the include-archived option |

### Still one-sided, still deliberate

`Refusals`, `Clarifications`, `Referrals` and `Notifications` remain bound by the console and not by
the terminal, on the grounds 005 recorded: each is a survey across the whole deployment serving an
open question in §17 rather than a fact about what is happening now, and each already has a command
a person runs when they are asking that question. **This feature does not widen the terminal into a
survey tool**, and the entry stays in the contract so the absence is still deliberate rather than
merely still true.

## The doors

Unchanged in substance from 005, extended to two new subjects.

| Call | The door |
|---|---|
| `Destroy` | described first — the conversation, its participants, its message count, and that every participant's half goes; keep offered first; the conversation's own identifier typed back. When an exchange in it is open, the waiting asker's outcome is shown and accepted **before** the identifier is asked for, exactly as a purge does. |
| `Archive` / `Unarchive` | no typed name. Nothing is destroyed and the act reverses with one keystroke, which is the line 004 drew and this does not move. |
| `Start` | no typed name, and it is described: adding an agent says it is about to start a process on this machine, in the same description that names the two files it will write. |

**The door is enforced by a test that reads the source.** `SurfaceTests` already allows
`RetireAsync`, `ReinstateAsync` and `PurgeAsync` in exactly one file under `src/Parley.Cli/Top`, and
requires that file to contain the typed-name prompt. `DestroyAsync` joins that list under the same
rule. A helper whose *name* merely contains one of those words fails it, which has happened once and
is the point.

## Not yet bound by either surface

**Empty, and the check that reads it now finds nothing — which is the state it was written to
reach.**

While the protocol landed ahead of the views, `ArchiveAsync` and `UnarchiveAsync` sat here with
`DriftTests` told to expect them. The terminal binds both at T056, so the rows are gone. The
section stays as a heading with no table because the next feature that lands a protocol change
before its surface will want it back, and because deleting it would lose the argument for why an
exemption belongs in a contract rather than in a test file.
