# The launch configuration, and what each reader owes

This is not a wire contract. Nothing here crosses a transport or reaches a broker — `.mcp.json` is a
file in a repository, usually committed, edited by hand as often as by a tool. That is exactly why it
needs one: several programs read it, a person reads it, and the failure mode of disagreement is a
deleted configuration.

## The file

```jsonc
{
  "mcpServers": {
    "parley-alpha": {                       // one entry per project
      "command": "parley-mcp",
      "env": {
        "PARLEY_AGENT": "inventory",        // the identity; the machine half is detected
        "PARLEY_PROJECT": "alpha",          // matches the key's suffix, always
        "PARLEY_OWNS": "Stock levels and reservations. Not pricing.",
        "PARLEY_WORKSPACE": "${PARLEY_WORKSPACE:-/src/inventory}"
      }
    },
    "parley-beta": {                        // the same repository, a second roster
      "command": "parley-mcp",
      "env": {
        "PARLEY_AGENT": "stock",            // a different name, because beta chose it
        "PARLEY_PROJECT": "beta",
        "PARLEY_OWNS": "What is on hand, and what is promised.",
        "PARLEY_WORKSPACE": "${PARLEY_WORKSPACE:-/src/inventory}"
      }
    },
    "some-other-server": { }                // never touched, never counted, never removed
  }
}
```

`PARLEY_PROJECT` duplicating the key's suffix is deliberate. The key is what the *client* uses to
namespace tools; the variable is what the *adapter* reads to find a broker. Nothing derives one from
the other at runtime, so they are written together and checked together.

## What every reader owes

1. **Find entries under both spellings.** `parley` and `parley-*`. A reader that knows only one
   reports a repository as unenrolled when it is enrolled, which is how it gets enrolled twice.
2. **Answer with all of them.** A repository has as many enrolments as it has entries. "The first one"
   is never the right answer to "what is this repository".
3. **Distinguish three states.** Enrolled nowhere, enrolled in these projects, and could not be read.
   The third is not the first.
4. **Never write at a key held by an entry that is not ours**, where "ours" is decided by
   `PARLEY_AGENT` being present. Refuse and name the key.
5. **Never remove an entry whose `PARLEY_AGENT` is not the agent named.** The existing guard, which
   exists because a withdrawal once removed a different agent's entry and reported success.
6. **Leave every other server alone**, including when the file is rewritten. Count them in the note so
   a person can see they survived.
7. **Refuse what cannot be merged into.** A non-object `mcpServers`, unreadable JSON, a non-object
   root. Three silent replacements of somebody's configuration are recorded in this codebase's
   comments; none of them were malice and all of them were a cast.

## Commands

| Command | Change |
|---|---|
| `parley enroll scan <dir> [<manifest>]` | a scanned row reports every project the repository already answers in |
| `parley enroll plan <manifest>` | says which entry it would update and which it would add, by key |
| `parley enroll apply <manifest> [--project <name>]` | **new flag.** Without it, and without `project` in the manifest, behaviour is exactly as today: key `parley`, unpinned |
| `parley-top` — adding an agent to a project | unchanged on screen; writes the project-keyed entry |
| `parley-top` — withdrawing an agent | removes only that project's entry and says how many remain |

The enrolment manifest gains one field:

```jsonc
{
  "_readme": [ "..." ],
  "node_config": "…/node.json",
  "project": "alpha",        // NEW. Optional. --project overrides it.
  "agents": [ /* … */ ]
}
```

## Messages

Refusals are named rather than generic, because each sends a person somewhere different.

| Situation | What is said |
|---|---|
| The key is held by a stranger | `.mcp.json already has a server called 'parley-alpha' that this did not write. Rename it or remove it first; enrolling would replace it.` |
| Already this project's agent | `already an agent of 'alpha' — nothing to change` |
| Pinned to an address that is this project | `it is already pinned to the broker at '127.0.0.1:51707', which is 'alpha' itself` |
| Pinned to an address nothing knows | `it is already pinned to the broker at '127.0.0.1:51707', which no project in this manifest holds` |
| Withdrawal, several entries, no project named | `it answers in 'alpha' and 'beta'. Name which one to withdraw from.` |
| Withdrawal, entry belongs to another agent | `left alone, its 'alpha' entry is 'stock' rather than 'inventory'` |
| Catalogued elsewhere (FR-013) | `'alpha' already lists this workspace for unattended start, in …/alpha-node.json. A repository may answer in several projects and may be started unattended by only one.` |
