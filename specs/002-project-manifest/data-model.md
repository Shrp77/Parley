# Data Model: Machine-level project manifest

**Feature**: 002-project-manifest | **Date**: 2026-08-28

Nothing here is stored in the database. These are configuration entities, read on one machine by
one operator's tools. The storage schema is unchanged and carries no project identity — FR-015.

---

## ProjectManifest

One per machine. Holds the projects configured on it.

| Field | Type | Rules |
|---|---|---|
| `_readme` | list of strings | Optional. Guidance carried in the file for whoever opens it. Ignored on read. |
| `projects` | list of `Project` | Required. May be empty — a machine with no projects is a valid state, not an error. |

**Location**: `Environment.SpecialFolder.ApplicationData` + `Parley/projects.json`, overridden by
`--config` then `PARLEY_CONFIG`.

**Rules**:

- **M1** — A manifest that is absent is not an error. Every command falls back to its previous
  resolution.
- **M2** — A manifest that exists and cannot be parsed is refused, naming the file. It is never
  treated as describing no projects. *(This is the failure class already found twice in this
  repository: an unreadable node catalogue treated as empty, and a wrong-typed node cast away.)*
- **M3** — A manifest whose `projects` is present but not a list is refused, for the same reason.
  Absent is the only shape that means "none".
- **M4** — Project names are unique, compared ordinally.
- **M5** — No two projects name the same store.
- **M6** — No two projects name the same endpoint.
- **M7** — M4–M6 are enforced both when writing and when reading, so a hand-edited file is refused
  by the same rule `add` applies.

---

## Project

One independent deployment: a store, an endpoint, a catalogue, and the agents that connect there.

| Field | Type | Rules |
|---|---|---|
| `name` | string | Required. Lowercase letters, digits and hyphens, starting with a letter, at most 32 characters — the same shape as an agent name, so one grammar is learned. |
| `store` | string | Required. Absolute path to the SQLite file. Need not exist; a broker creates it. |
| `endpoint` | string | Required. Transport address, as `BrokerEndpoint` already parses. |
| `node_config` | string | Optional. Absolute path to this project's node catalogue. Absent means this machine starts nothing for this project, which differs from an empty catalogue only in that there is no file to add to. |
| `description` | string | Optional. One line, for the operator's own benefit. Never crosses to an agent, so it carries no cap beyond a sane one. |

**Rules**:

- **P1** — `name` matches the agent-name grammar. Reusing it is deliberate: an operator should not
  have to learn two.
- **P2** — `store` and `node_config`, when present, are absolute. A relative path resolves against
  whatever directory the caller happened to be in, which is the defect this feature exists to
  remove.
- **P3** — `endpoint` parses. An unparseable endpoint is refused when the manifest is read, not
  when a connection is attempted.
- **P4** — The directory holding `store` must exist, or the project is reported as
  misconfigured. The *file* may be absent; brokers create it and only `serve` migrates it.
- **P5** — A project's name has no meaning to the protocol and is never sent. It exists so a
  person can say which deployment they mean.

---

## Relationships

```text
ProjectManifest 1 ── * Project
                        │
                        ├── store        → one SQLite file, opened only by that project's broker
                        ├── endpoint     → one transport address, held by one broker
                        └── node_config  → one NodeCatalogue (existing entity)

Agent (existing) ── connects to ──> endpoint
     resolved from PARLEY_BROKER, else from PARLEY_PROJECT via this manifest
     identity still from PARLEY_AGENT / PARLEY_OWNS — never from the project
```

**Isolation is a property of the store, not the name.** Two projects are separate because their
brokers open different files and a connection cannot see another's. The name is how a person
refers to one; removing it from the manifest forgets how to reach the project and changes nothing
about the project itself.

---

## Resolution (pure function, no I/O)

Given four inputs, produce the values a command will use, and say where each came from:

| Input | Example |
|---|---|
| explicit argument | `--db`, `--broker`, `--config`, `--project` |
| environment variable | `PARLEY_DB`, `PARLEY_BROKER`, `PARLEY_NODE_CONFIG`, `PARLEY_PROJECT` |
| the named project | from the manifest, if one was found |
| previous default | today's behaviour: local endpoint, `node.json` in the working directory |

**Rules**:

- **R1** — Precedence is argument, then variable, then project, then previous default, per value
  independently. A project may supply the store while an explicit `--broker` overrides the
  endpoint.
- **R2** — The source of each resolved value is reported, so an operator can see *why* a command
  is about to touch a particular file.
- **R3** — With no manifest and no project named, resolution is exactly today's, value for value.
  This is what makes the feature additive.
- **R4** — One project configured and none named resolves to that project. More than one
  configured and none named is refused, listing the names.
- **R5** — A named project that the manifest does not contain is refused, naming it and listing
  what is configured. It never falls back, because a fall-back here connects to the wrong
  deployment.

---

## State transitions

A project has no lifecycle in the manifest — it is present or it is not. What changes is whether
its broker is running, and that is **observed, never recorded**:

```text
configured ──(serve)──> reachable ──(stop)──> configured
     │
     └──(remove)──> not configured   [store, catalogue and launch configurations all remain]
```

Reachability is discovered by looking, for the same reason agent liveness is read from a heartbeat
rather than written down: a recorded status is a claim that goes stale the moment the thing it
describes changes, and nothing is watching to correct it.
