# Data model — what the surface holds while it runs

**It still stores nothing.** SPEC.md §4 gives the store to one broker process and FR-006 keeps
this a client. 004's model stands unchanged — projects from the manifest, one channel per project,
the seat as a held stream, delivered messages for the seat's life, everything else read per draw —
and this file records only what this feature adds to it.

## Held for the life of the application

| Thing | Where it comes from | Lifetime |
|---|---|---|
| **The application** | one `IApplication`, created by `TopCommand` or supplied by a test | the process; never the static class (research R2) |
| **Focus** | the toolkit's own, moved by Tab and by the mouse | a region at a time; named in the status line in words |
| **Selection** | the highlighted project, agent and conversation | survives a refresh of the region it is in, so a redraw does not move the person |

## Read per draw, never held

| Thing | Read | Notes |
|---|---|---|
| Agents of the selected project | `DescribeRegistry`, joined to the catalogue file | the join is derived on each draw; see below |
| What an agent owes | `Waiting` | only when the remove action opens, not on every draw |
| The catalogue | the project's `node_config` file | read fresh before every draw of the agents region and again before writing (edge case: edited by hand) |

## Derived, not stored

### Agents region row

The union of the registry and the catalogue for one project, keyed by agent name. Three states,
each a phrase and never a colour:

| In registry | In catalogue | State shown | Meaning |
|---|---|---|---|
| yes | yes | §14's presence: `running`, `startable`, `stranded`, or `retired` | the ordinary row |
| no | yes | `catalogued, not yet registered` | what adding produces; the node can start it and it registers when its server first attaches |
| yes | no | presence, and `not in this catalogue` | §17 q13's misleading `startable`, said out loud |

A row whose adapter is `Seat.Adapter` is a seat: shown as one, and never offered to the remove
action (research R7).

### Agent entry

What makes a repository an agent of a project on this machine. Two files, both written by adding
and both removed by retiring or purging:

| Field | Value | Written where |
|---|---|---|
| `Workspace` | the repository directory, absolute | both files |
| `Agent` | the role; suggested from the directory name by `EnrollCommand.SuggestName`, editable before confirming | both files |
| `Project` | the selected project's name | `.mcp.json` → `mcpServers.parley.env.PARLEY_PROJECT` (research R4) |
| `Owns` | optional, at most `OwnershipStatement.MaxCharacters` and only with approval, exactly as the enroll manifest requires | `.mcp.json` → `env.PARLEY_OWNS` |
| launch configuration | `mcpServers.parley = { command: "parley-mcp", env: {...} }` merged into whatever else the file holds | `<workspace>/.mcp.json` |
| catalogue entry | `agents.<Agent> = { command: "claude", workspace: <Workspace> }` | the project's `node_config` |

Validation, before anything is written (FR-009):

- the directory exists and holds `.git` (a directory, or a file for a worktree — said, not
  refused, as the enroll command does);
- its `.mcp.json`, if any, is a JSON object whose `mcpServers` is absent or an object — anything
  else is refused as unmergeable, the same refusal `EnrollmentPlan.Describe` throws;
- its `parley` entry, if any, names no other project (`PARLEY_PROJECT`) and no other broker
  (`PARLEY_BROKER`); an entry naming this project is "already correct", an entry naming neither is
  re-pointed and said;
- the name is well formed (`EnrollmentPlan.IsWellFormedName`) and not already in the catalogue
  under a different workspace.

### Withdrawal

The unit every destructive action on this surface is built from (spec: *Confirmation*), applied
to an agent:

| Step | State | Leaves how |
|---|---|---|
| described | role, workspace, adapter, status, presence, and every `Waiting` edge it is `WaitedOn` in | choose |
| chosen | `Keep` (first, and Enter's answer) / `Retire` / `Purge` | keep → done; else → |
| consequence accepted | only for `Purge` when at least one edge exists: the asker named, and what they will see | declined → keep |
| named | the typed text equals the agent's name exactly (ordinal); anything else, including empty, keeps | mismatch → keep, said |
| applied | the operator operation's own outcome (`Changed`, `PurgeVerdict`, the toll per table) | refused → files untouched |
| withdrawn | `.mcp.json` parley entry removed, catalogue entry removed; each named in the report, or named as absent | done |

Reinstating is the smaller shape: described, named, applied. It recreates no file and says the
two have to be added again for the agent to be startable (FR-015).

### Layout

One value, derived from the driver's width on every layout pass: `SideBySide` at or above the
threshold, `Stacked` below it. Nothing else changes with it — the four views are the same objects
either way, so a test that found a row by its text finds it in both.

## The files read from disk

| File | Read | Written | Why it is allowed |
|---|---|---|---|
| the broker's log | to follow | never | 004's convenience view, unchanged |
| the project's catalogue | before drawing the agents region; before adding | by adding and withdrawing | the broker never sees it (§17 q13), so it is not a second answer to anything the broker answers |
| a repository's `.mcp.json` | before adding and before withdrawing | by adding and withdrawing | the enroll command's own file, through the enroll command's own code |
| the manifest | as 004 does | by creating, removing and — new — recording a catalogue on an entry | through `ProjectCommand`, the manifest's one writer |
