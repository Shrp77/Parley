# Contract: the layout, the two files, and every refusal

What is on disk, what each file is allowed to say, and what happens for every way it can be wrong. This is
the reference the implementation is held to; [data-model.md](../data-model.md) says what the shapes are
and why.

## Resolving the root

One rule, in one place — `ProjectManifest.Locate` — and read by everything else.

| Given | Root is |
|---|---|
| `--config <dir>` | that directory |
| `--config <dir>/projects.json` | `<dir>` — the previous spelling, honoured for at least one release |
| `PARLEY_CONFIG` set | the same two rules |
| neither | the platform's per-user configuration directory |

**The old spelling is recognised by name, never by probing the filesystem.** A value whose final segment
is the manifest's own file name names its parent; anything else is a directory. A rule that tested for
existence would behave differently before and after first run, which is a rule nobody can reason about.

The root is created if absent. That is the first run on a new machine, and nothing about it is an error.

## `<root>/projects/<name>/project.json`

```json
{
  "endpoint": "http://127.0.0.1:19100",
  "description": "The order pipeline and its two services."
}
```

### Refusals and how each is reported

| Case | Result | Said as |
|---|---|---|
| Folder has no `project.json` | Not a project | nothing — half-created, or somebody's directory |
| Not valid JSON | That project unusable, others read | names the project **and the file path** |
| `endpoint` absent, empty, or not an absolute URI | That project unusable, others read | names the project and the field |
| Folder name is not a valid project name | Not a project | names the folder, says what a name may be |
| `description` longer than the manifest allowed before | accepted | no change to that rule |
| Two projects share one `endpoint` | both reported, neither preferred | names both projects |
| Unknown members present | ignored, preserved on write | — |

**Never reported as "this machine has no projects".** One unreadable file must not be able to say that;
it is the difference between a deployment that is broken and a machine that was never set up, and they
lead somewhere completely different.

**Unknown members are preserved.** A newer version's file read by an older one must not lose fields on
the next write.

## `<root>/ports.json`

```json
{
  "band": { "from": 19100, "to": 19355 },
  "handed_out": { "orders": 19100, "billing": 19101 }
}
```

### The claim

```
open <root>/ports.json  with no sharing        ← the hold starts
  read it; unreadable or absent → treat as empty
  list <root>/projects/*/project.json
  spoken_for = handed_out ∪ { every endpoint's port found in a project }
  if the operator stated an endpoint:
      try to bind it on THE ADDRESS IT NAMES
      if it binds:  release, record it as spoken for, write, done
      if it does not: refuse, naming the address
  otherwise, for p in band, ascending, p ∉ spoken_for:
      try to bind 127.0.0.1:p
      if it binds:  release, record handed_out[name] = p, write, done
      if it does not: continue
  none left → refuse
close                                           ← the hold ends
```

- **The whole read-modify-write is inside one hold.** That is what FR-010 means by a claim that cannot be
  granted twice.
- **The bind is inside the hold too.** It is not what makes the claim safe — the hold is. It is there so
  a port something else on the machine already holds is not handed out.
- **The union with the projects is the reconciliation** of FR-013. The projects are the truth; this file
  is bookkeeping.

### Loopback is what is *handed out*, not what an endpoint *is*

SPEC.md §16 confines the transport to *"a loopback connection, a private network somebody controls, or a
tunnel authenticated outside this protocol"* — so a broker reachable from another machine is a supported
deployment. Two consequences, and getting either wrong is a real defect:

- **What is chosen automatically is always loopback** (FR-020). This command cannot know which interface a
  person means to expose, and guessing would put a broker on a network by accident.
- **A stated endpoint is tested on the address it names** (FR-021). Binding `127.0.0.1:n` proves nothing
  about `0.0.0.0:n` — the loopback bind succeeds while the real one fails, and the failure arrives at the
  next start, on the interface nobody was watching.

### Refusals

| Case | Result |
|---|---|
| File held by another claim | retry with backoff; on exhausting the budget, refuse naming the contention, **create nothing** |
| File unreadable | the claim refuses saying so. **Nothing that serves is affected** |
| Band exhausted | refuse, name the band, create nothing |
| `handed_out` names a project that is gone | entry dropped, port available |
| `handed_out` disagrees with a project's `project.json` | the project wins; the entry is corrected |
| File absent | created, and every existing project's port learned from its own file |

**The file is never deleted**, including when the last project goes — `StoreFiles.Lock` records why: on
Linux an exclusive lock belongs to an open file rather than a name, so unlinking it lets the next two
claimants each lock a different file of one name.

**Nothing on the serving path opens it.** Only `project add` and `project remove` take the hold.

## What a store is, and what travels with it

The store is `<root>/projects/<name>/parley.db`. What belongs to it is the list
`project remove --destroy` already enumerates:

| Suffix | Kind |
|---|---|
| *(none)* | the store |
| `-wal`, `-shm`, `-journal` | SQLite's companions |
| `.lock` | the serving broker's exclusive claim |
| `.owner` | which process holds it |
| `.files` | the file location of §9.5 — a **directory** |
| `.files-incoming` | staging for placements in progress — a **directory** |

## Moving an existing deployment

Per project, in this order:

1. **Take the store's lock.** This is the check, not a file to tidy — if it cannot be taken, something is
   serving, and the move stops before touching anything and names the holder.
2. **Copy** the store and every companion above to the project's folder. Never overwrite: a destination
   that already holds a file stops this project, naming it.
3. **Verify** the copies.
4. **Rewrite** `project.json`.
5. **Delete** the old files, lock and owner last.

`.lock` and `.owner` are **recreated, not moved** — both describe a process that is not running, which
this command just proved.

### Why it is resumable

An interruption leaves the configuration naming exactly one of the two copies, and that one is
authoritative. Re-running finishes rather than guessing, and step 2's never-overwrite makes repeating safe.

| Interrupted after | State | Re-run does |
|---|---|---|
| 2 or 3 | config names the old path | copies what is missing, continues |
| 4 | config names the new path | deletes the old files |
| 5, partly | config names the new path | deletes the rest |

### Refusals

| Case | Result |
|---|---|
| A broker is serving the store | stops before touching anything; names the process |
| The operator has not accepted the cost | does not start |
| Destination holds a file | that project stops, named; others already done stay done |
| Source unreadable | that project stops, named |
| Already in the standard place | left alone, reported as already correct |
| Port inside the operating system's ephemeral range | **reported, not changed** — see below |

**Ports are never renumbered by a move.** What the move does is *report*, and what it reports depends on
whether the endpoint is reachable from off the machine — because the cost of changing it is completely
different in the two cases.

**A loopback endpoint.** Only this machine holds the address, and every repository enrolled by this
tooling pins by `PARLEY_PROJECT` rather than `PARLEY_BROKER` — `EnrollmentPlan` says why: *"the name is
the stable half."* So moving costs nothing, and the move offers it where the port is worth moving:

| Condition | Why it is worth saying |
|---|---|
| Inside the OS ephemeral range | An outbound connection can transiently hold it, so the broker occasionally fails to bind at logon. Windows' range is 49152–65535; Linux's default is 32768–60999 |
| Inside or adjacent to a Windows excluded port range | Reserved by Hyper-V/WSL and **reallocated at boot**. The failure is *"an attempt was made to access a socket in a way forbidden by its access permissions"* — which reads like a permissions fault, not a taken port |

**A non-loopback endpoint. Reported, and moving is *not* offered.** Things hold that address which this
machine cannot see and this command cannot rewrite:

- a firewall rule pinned to the port — move it and the broker binds happily while nothing can reach it,
  which is the worst shape of failure because it looks healthy
- `PARLEY_BROKER` on **other machines'** agents
- an SSH tunnel spelled in somebody's config
- any router or NAT rule

So for these the move says what it found and stops there. Changing a network endpoint is a deployment
decision with consequences off this machine, and `project set` is where a person makes it deliberately.
