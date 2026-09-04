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

## Still excluded, and listed so their absence is deliberate

`ForceConclude`, `Instruct`, `Prune`. The web console binds them. The terminal does not, and
`SurfaceTests` continues to forbid them anywhere under `Top`. Admitting one is a decision to argue
here first, not a call to add.

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
