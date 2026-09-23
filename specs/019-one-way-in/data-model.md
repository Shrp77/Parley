# Data model: One place, and one way in

What lives on a machine after this feature, and what each part is allowed to say. Two new file shapes, one
deleted, and one type that deliberately does not change.

## The layout

```text
<root>/                                  PARLEY_CONFIG names this, not a file
├── node.json                            the machine catalogue (018, unchanged)
├── ports.json                           which ports this machine has handed out  (new)
├── parley-broker.log                    the broker's log, as today
└── projects/
    ├── orders/
    │   ├── project.json                 where it listens, and what it is        (new)
    │   ├── parley.db                    the store
    │   ├── parley.db-wal
    │   ├── parley.db-shm
    │   ├── parley.db.lock               held by the serving broker
    │   ├── parley.db.owner              which process holds it
    │   ├── parley.db.files/             the file location of §9.5
    │   └── parley.db.files-incoming/    placements in progress
    └── billing/
        └── …
```

`<root>` defaults to the platform's per-user configuration directory, which is where `projects.json`,
`node.json` and the log already sit. **This feature moves what is under the root, not where the root is.**

**`projects.json` is deleted.** The directory listing is the set of projects (FR-005). There is no index,
so there is nothing for an index to disagree with.

---

## `project.json`

One per project, inside that project's folder.

```json
{
  "endpoint": "http://127.0.0.1:19100",
  "description": "The order pipeline and its two services."
}
```

| Field | Required | Rule |
|---|---|---|
| `endpoint` | yes | An absolute URI with a scheme. What the broker binds (FR-012). |
| `description` | no | Free text, shown on the surfaces. Absent or empty is normal. |

**There is no `name`.** The folder is the name (FR-004), so the two cannot disagree. A reader takes the
name from the directory entry and validates it as a project name; a folder whose name is not one is not a
project and is reported (FR-029).

**There is no `store`.** It is derived: `<root>/projects/<name>/parley.db` (FR-006). FR-007 removes the
ability to put it anywhere else, so there is nothing to record.

**There is no `node_config`.** It named a per-project agent catalogue, whose meaning 018 deleted. FR-068
keeps it out of the new file so it is not born carrying a corpse — which is 018's T027, absorbed here
because this is the change that replaces the file it lived in.

### What a reader does with a bad one

| Case | Result |
|---|---|
| Folder with no `project.json` | Not a project. Silent — half-created, or somebody's scratch directory. |
| `project.json` that is not valid JSON | Reported naming the project and the file; the others are read (FR-029). |
| `endpoint` missing or not a URI | Same: reported, others read. |
| Folder name not a valid project name | Reported, skipped (FR-027). |
| Two projects with one `endpoint` | Both reported by name, neither preferred (FR-022). |

The rule throughout is FR-023's: what cannot be read is reported *as that project's file*, never as the
machine having no projects.

---

## `ports.json`

One per machine, at the root, beside `node.json`.

```json
{
  "band": { "from": 19100, "to": 19355 },
  "handed_out": {
    "orders": 19100,
    "billing": 19101
  }
}
```

| Field | Required | Rule |
|---|---|---|
| `band` | no | The range ports are taken from. Absent means the built-in default. Recorded so that a machine whose band was widened keeps that decision. |
| `handed_out` | yes | Project name to port. One entry per project this machine gave a port to. |

**It is bookkeeping and never authority.** FR-012 puts the truth about what a broker binds in
`project.json`; this file exists only so that two claims made at once cannot be granted one port. Nothing
that reads, starts or serves a project opens it (FR-011).

**It is reconciled, not believed.** On every claim, the projects are listed and every endpoint found is
treated as spoken for whether or not it appears here (FR-013). So:

| State of `ports.json` | What happens |
|---|---|
| Missing | Created. Every existing project's port is learned from its `project.json`. |
| Empty, or lost entries | Rebuilt from the projects on the next claim. |
| Unreadable | The claim fails saying so. **Nothing that serves is affected** — SC-009. |
| Names a project that no longer exists | That entry is dropped; its port becomes available (FR-014). |
| Names a port a project does not have | The project wins. The entry is corrected. |

**It is never deleted**, including when the last project goes. `StoreFiles.Lock` records why: on Linux an
exclusive lock belongs to an open file rather than to a name, so unlinking it would let the next two
claimants each lock a different file of one name — two holders, each believing it is alone.

### The hold

The file is its own lock. A claim opens it with no sharing, reads, decides, writes and closes. On Windows
that is a sharing refusal; on Linux and macOS it is an exclusive `flock`. **Taken only by `project add`
and `project remove`**, for as long as one read-modify-write takes.

Contention is ordinary, not fatal — two people adding projects at once is a thing that happens — so a
claim retries with a short backoff and a bounded budget, and a claim that runs out of budget fails naming
the contention and creates nothing (FR-015).

---

## `ProjectSettings` — what does not change

`Parley.Core.ProjectSettings` stays free of filesystem rules and filesystem access (FR-067). The reason is
not tidiness: a broker compares deployments belonging to machines it is not on, so a type that asked a
filesystem about a path would be answering the wrong question, and `Parley.Core` has no I/O and no package
dependencies by construction.

What changes is only where its values come from:

| Field | Before | After |
|---|---|---|
| `Name` | the manifest entry | the folder name |
| `Store` | the manifest entry, any path | derived from the root and the name |
| `Endpoint` | the manifest entry | `project.json` |
| `NodeConfig` | the manifest entry | **removed** (FR-068) |
| `Description` | the manifest entry | `project.json` |

Reading and writing these files belongs to `Parley.Configuration`, which is where `ProjectManifest` lives
now and which both a broker's tool and an agent's server can see without either acquiring the other's
dependencies.

---

## What is deleted

| Thing | Why |
|---|---|
| `projects.json` | Replaced by the directory listing (FR-005). |
| `ProjectSettings.NodeConfig` | Meaningless since 018; kept out of the new file (FR-068). |
| `--db`, `PARLEY_DB` | No store outside a project (FR-007). |
| `--listen` on `project add` | The port is handed out (FR-016). |
| `--node-config` on `project add`/`set` | Wrote a field nothing reads. |
| `ProjectManifest.Collisions`' endpoint and store checks | A shared store is no longer expressible; a shared endpoint is detected on read instead (FR-022). |
