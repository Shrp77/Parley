# Contract: what the terminal surface may call, and on which client

This feature adds no operation. It admits three that 004's contract kept off this surface, on
the terms `CLAUDE.md` recorded when destroying a project was admitted: **a description first and
the thing's own name typed back.** Everything else in 004's `contracts/operations.md` continues to
hold and is not restated; this file is the diff.

## The two clients stay two clients

Unchanged from 004: `ClientFor(project)` is the Operators client and everything read *about* a
deployment comes from it; `AgentsFor(project)` is the Agents client and is reached only through
`SeatBook`. FR-019.

## Operator operations newly admitted — each behind a door

| Call | Serves | The door |
|---|---|---|
| `Retire` | FR-011, FR-012, FR-013 | described; chosen with keep first; the agent's name typed back |
| `Purge` | FR-011, FR-012, FR-013, FR-014 | as retire, and when the agent owes an answer in an open exchange, the asker's outcome shown and accepted before the name is asked for |
| `Reinstate` | FR-015 | described; the agent's name typed back |

The description is composed from two reads that already exist: `DescribeRegistry` for role,
workspace, adapter, status and presence; `Waiting` for what the agent owes, as the edges naming it
`WaitedOn`.

**The door is enforced by a test reading the source, not by a convention.** `SurfaceTests` allows
`RetireAsync`, `ReinstateAsync` and `PurgeAsync` in exactly one file under `src/Parley.Cli/Top`,
and that file must contain the typed-name prompt. A second file that reaches any of them fails the
build's test run. FR-017, SC-007.

## The reports neither contract had named

`Refusals`, `Clarifications`, `Referrals` and `Notifications`. The web console bound all four; the
terminal bound none, and until this feature no contract said so — which made 004's promise that
absences are *listed* untrue for four of them. Written down here rather than quietly left, because
a gap nobody recorded is indistinguishable from one nobody noticed.

**Superseded in 0.26.0.** Protocol 0.5 replaced `Referrals` and `Notifications` with `Results` and
`Flags`, and the terminal now binds all four reports, with `Waiting`, in one Health dialog
(`Health.cs`) — the same five the web console's Health page reads. The paragraph below records the
earlier reasoning.

They stay out on the same grounds, and it is scope rather than safety: each is a survey across the
whole deployment serving an open question in SPEC.md §17 rather than a fact about what is happening
right now, each already has a command a person runs when they are asking that question, and none of
them belongs in any of the four regions. The four regions answer *what is this deployment doing*;
these answer *what has it been doing, in aggregate, and should the protocol change*. A terminal
that grew a fifth region for them would be a different feature, argued here first.

`DriftTests` compares the two surfaces against this file, so the next operation one of them gains
alone fails the build rather than waiting to be noticed by somebody moving between them.

## Admitted in 0.26.0: nothing is excluded any more

`ForceConclude`, `Instruct` and `Prune` were excluded here, and that exclusion is withdrawn by the
owner's decision, made while protocol 0.5 was specified, that **every surface offers every operator
operation**. A person at a terminal over SSH is the one who cannot open the web console, and a
surface that shows stalled work but cannot end it sends them elsewhere for the one thing the flag
asked of them.

They were admitted on the terms the others were: a description first and a deliberate act.
`SurfaceTests` holds each to one file carrying its door — `Closing.cs` takes the conversation's
identifier typed back, `Pruning.cs` the project's name, and `Instructing.cs`, which deletes nothing,
says the agent may decline. `DriftTests` now asserts that both surfaces bind every operation on
`BrokerClient`, with no exemption read from this file.

## Order of operations for a withdrawal

1. `DescribeRegistry` and `Waiting` — the description.
2. The choice, the consequence if any, the typed name.
3. `Retire` or `Purge` on the wire. The broker's answer is shown in its own words: `Changed` for
   retire, the `PurgeVerdict` and the per-table toll for purge, exactly as `parley retire` and
   `parley purge` print them.
4. Only if step 3 changed something: the catalogue entry and the launch configuration are
   withdrawn, and each is named in the report as removed or as already absent.

Step 4 follows step 3 rather than preceding it so that a refused or failed operation leaves the
repository as it was. A person who sees "not registered" has changed nothing.

## Written, not sent — the additions to 004's table

| Action | How | Notes |
|---|---|---|
| Add an agent (FR-007 to FR-010) | in-process, through `Enrolment.Add`, the extracted body of `parley enroll apply` | one writer for `.mcp.json` and the catalogue; research R5 |
| Withdraw an agent's files (FR-013) | in-process, through `Enrolment.Withdraw` | the inverse, beside it |
| Record a catalogue on a project (FR-010) | in-process, the same code `parley project set --node-config` runs | the manifest keeps one writer; the serve watcher restarts the project on its changed entry |

None goes near a broker.

## What the node does differently — one shipped change outside the surface

`parley node`, standalone or hosted by `serve --with-node`, watches its catalogue file and rejoins
the broker with its new roles when the file changes (research R3). The wire contract is untouched:
`Join` is called again with a new `StartableRoles`; no operation is added. Without this an agent
added under a running node would read `startable` and never be started, which is §17 question
13's case exactly.
