# The launch configuration, and what each reader owes

This is not a wire contract. Nothing here crosses a transport or reaches a broker. `.mcp.json` is a
file in a repository, often committed, edited by hand as often as by a tool. Several programs read
it and a person reads it, and when they disagree the usual result is a deleted configuration. So
each reader owes the rules below.

## The file

```jsonc
{
  "parleyAgentId": "7f0c…",                 // what this repository is; one per repository
  "mcpServers": {
    "parley-alpha": {                       // one entry per project
      "command": "parley-mcp",
      "env": {
        "PARLEY_AGENT": "inventory",        // the agent's name in this project
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

`parleyAgentId` sits beside `mcpServers`, not inside an entry. It belongs to the repository, so a
repository that answers in two projects has one identity and two entries. It is minted the first time
the repository is enrolled and kept after that, until the last of its entries is withdrawn: a
repository enrolled again after that is a new agent. The per-machine catalogue (`node.json`) is keyed by
it.

**The identity is what an agent is. The name is only what a project shows for it.** The agent's
server reports the identity when it attaches, so a repository can change its name and keep
everything it was party to:

1. Change `PARLEY_AGENT` in the entry for that project.
2. Close the agent's session, and open the repository again.

The broker sees an identity it already holds, from the same repository on the same machine, under a
name it does not hold, and gives that agent the new name. Its conversations, the work it owes, the
files it placed and its ownership line all stay with it. Two things stop a rename, and each says
which: the new name already belongs to another agent, or the old name still has a session
attached. The same identity from a different folder is refused as a collision, and the same
identity on another machine is a different agent, because the machine is part of the name.

The identity is a label. It is not a credential and it lets nobody attach who could not already.

A repository with no project name uses the key `parley` and no `PARLEY_PROJECT`. Enrolling always
writes `parley-<project>`.

`PARLEY_PROJECT` repeating the key's suffix is deliberate. The key is what the client uses to
namespace tools. The variable is what the adapter reads to find a broker. Nothing derives one from
the other at run time, so they are written together and checked together. An entry whose key and
variable name different projects is refused, not guessed at.

`PARLEY_OWNS` is written only when the agent already has an ownership line. Enrolling never makes
one up. Accepting an agent's answer to the ownership question writes it here.

`PARLEY_WORKSPACE` is written as `${PARLEY_WORKSPACE:-<path>}` so a teammate can override the path.
A path containing `}` or `$` is written plainly, because the expansion has no escape.

## What the adapter reads

`parley-mcp` reads these from its environment:

| Variable | Meaning |
|---|---|
| `PARLEY_AGENT` | The agent's name. Required. |
| `PARLEY_PROJECT` | The project whose broker to reach. The name is looked up in this machine's configuration. An unknown name is refused, not guessed. |
| `PARLEY_BROKER` | A broker address, used instead of a project. For a tunnel's local end, for example. |
| `PARLEY_OWNS` | What the agent is responsible for. |
| `PARLEY_WORKSPACE` | The directory the agent works in. Unset means unknown. |
| `PARLEY_RESPONDER` | The model named on each message. Defaults to `unknown-model`. |
| `PARLEY_MACHINE` | The machine half of the agent's name. Unset means this machine's own name. |

An agent a node starts also carries `PARLEY_LAUNCH_TOKEN`. It is not written to the file.

## How a repository is enrolled

There are three ways, and they write the same thing:

- `parley enroll <folder> [--all | --none] [--describe]` finds the git repositories under a folder,
  offers them, and enrolls the ones chosen. `--all` and `--none` skip the picker. `--describe`
  reports what would change and changes nothing. It needs a project, because the key it writes is
  `parley-<project>`.
- The terminal's Enroll agents from folders (`Alt+f`).
- The web console's Register an agent.

For each repository, enrolling:

1. Creates or updates the project's entry in `.mcp.json`, leaving every other server as it was.
2. Mints `parleyAgentId` if the file has none.
3. Adds the agent to this machine's `node.json` when it is to be started unattended. That is the
   consent to start it: a peer's request can then start it with nobody watching.
4. Appends a `## Peers` block to the repository's instructions file if it has none. A `## Peers`
   section already there is never rewritten.

The whole set is checked before anything is written. Two repositories with one name, two names
over one folder, a folder that is not there, and a name the registry would refuse all stop the
run with nothing changed.

## What every reader owes

1. **Find entries under both spellings.** `parley` and `parley-*`. A reader that knows only one
   reports a repository as unenrolled when it is enrolled, and it gets enrolled twice.
2. **Answer with all of them.** A repository has as many enrolments as it has entries. "The first
   one" is never the right answer to "what is this repository".
3. **Distinguish three states.** Enrolled nowhere, enrolled in these projects, and could not be
   read. The third is not the first.
4. **Never write at a key held by an entry that is not ours.** An entry is ours when it carries a
   Parley variable or runs `parley-mcp`. Refuse and name the key.
5. **Never remove an entry whose `PARLEY_AGENT` is not the agent named.**
6. **Leave every other server alone**, including when the file is rewritten. Count them in the note
   so a person can see they survived.
7. **Refuse what cannot be merged into.** A non-object `mcpServers`, a server named twice,
   unreadable JSON, a non-object root.

## What is said when enrolling or withdrawing stops

Each refusal names its cause, because each sends a person somewhere different.

| Situation | What is said |
|---|---|
| The key is held by a stranger | `there is already a server called 'parley-alpha' that this did not write. Rename it or remove it first; enrolling would replace it` |
| The key and the variable disagree | `'parley-alpha' names project 'alpha' and its environment names 'beta'. Make them agree before enrolling.` |
| Two repositories, one name | `agent 'inventory' is also used by agents[0]. Two repositories sharing a name means either may collect the other's mail` |
| Two names, one folder | `workspace is also claimed by agents[0]. One directory cannot be two agents: the second launch configuration would overwrite the first` |
| Withdrawal, several entries, no project named | `left alone, it answers in 'alpha', 'beta' — name which one to withdraw from` |
| Withdrawal, the entry belongs to another agent | `left alone, its 'parley-alpha' entry is 'stock' rather than 'inventory'` |
| Withdrawal, the entry is pinned to a broker address | `left alone, its 'parley' entry is pinned to the broker at '127.0.0.1:51707', which this cannot match to project 'alpha'` |
