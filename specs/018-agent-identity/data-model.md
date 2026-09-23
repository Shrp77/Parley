# Data model: An agent is an identity, not a name

Three things hold state. Two are files on the machine; one lives for as long as a process does. **Nothing is
added to the store, and nothing new crosses the wire.**

## Agent identity

| field | what it is |
|---|---|
| value | a GUID, lowercase and hyphenated |

- Minted by enrolment, once per repository, never derived from a path, a name, a project or a machine
  (FR-002).
- Opaque: nothing parses it, orders by it, or reads meaning out of it.
- Lives in two places and must agree in both: the catalogue's key on each machine that catalogues it, and the
  repository's own launch configuration.
- **Repository-scoped, not machine-scoped** (R2). The same checkout on two machines carries the same
  identifier; what distinguishes the running agents is the machine half of their names, as it always has.

## Catalogue entry

| field | what it is |
|---|---|
| identifier | the key |
| command | what to run |
| workspace | where to run it |

- One per identifier, by the shape of the file. The duplicate this feature exists to prevent cannot be
  written down.
- Holds **no project and no name**. What a project calls this agent is the repository's to declare.
- Absent for an agent that is registered here but not catalogued, which is a real and ordinary state — the
  repository is on this machine but nothing has been given permission to start it.

## Membership, which is not new

Already written by enrolment, once per project, into the repository's launch configuration.

| field | what it is | where it already lives |
|---|---|---|
| project | which project this entry is for | the entry's key, and `PARLEY_PROJECT` |
| name | what that project calls this agent | `PARLEY_AGENT` |

- Several per repository — one per project it answers in. This is what makes `infra` and `billing-infra` two
  names for one identity.
- Read by the node, never written by it. The writer is enrolment.
- Travels in the folder, so a moved or renamed checkout needs no repair beyond repointing the one catalogue
  entry.

## Resident

| field | what it is |
|---|---|
| identifier | who this process is |
| process | the running agent |

- Keyed by identifier, **not by name and not by project**, so one checkout is one process however many
  projects ask for it.
- Held in a table created once by whatever hosts the nodes and handed to each (R5) — not a static, because
  test classes run in parallel and this repository has a scar about process-wide singletons.
- A dead resident is removed on the next attempt, so the following wake arrives as a start rather than a turn
  into a corpse. That behaviour exists already and is keyed differently, nothing more.

## What relates to what

```text
repository ──carries──> identity ──keys──> catalogue entry ──names──> command + workspace
     │                                                                        │
     └──declares──> (project, name)+                                          │
                          │                                                   │
                          └───────── node announces per project               │
                                             │                                │
                            broker asks by name ──resolves──> identity ──> resident
```

**The single-direction rule worth stating**: names resolve *to* identities and never back. Nothing in the
node, the catalogue or the resident table is keyed by a name, because a name is a project's word for
something and the node has no projects.
