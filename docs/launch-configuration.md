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
        "PARLEY_PROJECT": "alpha"           // matches the key's suffix, always
      }
    },
    "parley-beta": {                        // the same repository, a second roster
      "command": "parley-mcp",
      "env": {
        "PARLEY_AGENT": "stock",            // a different name, because beta chose it
        "PARLEY_PROJECT": "beta"
      }
    },
    "some-other-server": { }                // never touched, never counted, never removed
  },
  "parleyMachines": {                       // what differs per machine
    "laptop": {
      "parley-alpha": { "PARLEY_OWNS": "Stock levels and reservations. Not pricing." },
      "parley-beta": { "PARLEY_OWNS": "What is on hand, and what is promised." }
    },
    "server": {
      "parley-alpha": { "PARLEY_BROKER": "http://127.0.0.1:19100" }
    }
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

## One file on every machine

A repository checked out on two machines is two agents, because the machine is part of the name. One
committed file describes both. An infrastructure repository on a laptop and on the server it manages gives
`infra@laptop`, which answers questions about the system, and `infra@server`, which makes changes:

```jsonc
"parleyMachines": {
  "laptop": { "parley-acme": { "PARLEY_OWNS": "Answers questions about the infrastructure. Changes nothing." } },
  "server": { "parley-acme": { "PARLEY_OWNS": "Makes infrastructure changes on this host.",
                               "PARLEY_BROKER": "http://127.0.0.1:19100" } }
}
```

**An ownership line describes an instance and does not limit it.** Nothing in Parley stops `infra@laptop`
changing something. What an instance can do is decided by that machine's own permissions and credentials. The
line tells peers which one to ask. A sender asks one by its full name, and a request to bare `infra` still
reaches both.

**A block is filed by machine name, then by the entry's key, and holds `PARLEY_OWNS` and `PARLEY_BROKER`.** Nothing
else. It may be empty, which says this machine is enrolled in that project and differs in nothing.

**A block wins.** For that machine and that entry, a value in the block beats the entry and the environment. A
value it does not give comes from the entry. The machine is the name the agent will carry, so `PARLEY_MACHINE`
chooses a block too.

**The workspace is not in the file.** When a file has `parleyMachines`, an agent's workspace is the folder the
file was found in. A node starts an agent from the folder in that machine's own `node.json`. A checkout moved on one
machine is refused when it attaches, naming both folders, because a name belongs to one repository and a clone looks the
same to the broker. Retire the old name, then enrol again from the new folder.

**A mistake stops the whole file,** whichever machine it concerns: a setting that cannot go in a block, a name that
can never be a machine's, a block for an entry the file does not have. It is reported by machine, entry and setting.

**A committed file shows machine names and a tunnel's port** to anybody who can read the repository. A repository
that should not show them keeps its file out of git, and each machine writes its own.

`PARLEY_OWNS` is written only when the agent already has an ownership line. Enrolling never makes
one up. Accepting an agent's answer to the ownership question writes it to this machine's block.

## What the adapter reads

`parley-mcp` reads these from its environment:

| Variable | Meaning |
|---|---|
| `PARLEY_AGENT` | The agent's name. Required. |
| `PARLEY_PROJECT` | The project whose broker to reach. The name is looked up in this machine's configuration. An unknown name is refused, not guessed. |
| `PARLEY_BROKER` | A broker address, used instead of a project. For a tunnel's local end, for example. |
| `PARLEY_OWNS` | What the agent is responsible for. |
| `PARLEY_WORKSPACE` | Where to look for this file first. Without a `parleyMachines` section, the directory the agent works in; unset means unknown. |
| `PARLEY_RESPONDER` | The model named on each message. Defaults to `unknown-model`. |
| `PARLEY_MACHINE` | The machine half of the agent's name. Unset means this machine's own name. |

An agent a node starts also carries `PARLEY_LAUNCH_TOKEN`. It is not written to the file.

It looks for this file in `PARLEY_WORKSPACE`'s folder if a file there has an entry for this launch, then in the
folder it started in and each parent, up to the first holding `.git` or `.mcp.json`. It lays this machine's block
over what it was given.

## How a repository is enrolled

There are three ways, and they write the same thing:

- `parley enroll <folder> [--all | --none] [--describe]` finds the git repositories under a folder,
  offers them, and enrolls the ones chosen. `--all` and `--none` skip the picker. `--describe`
  reports what would change and changes nothing. It needs a project, because the key it writes is
  `parley-<project>`.
- The terminal's Enroll agents from folders (`Alt+f`).
- The web console's Register an agent.

For each repository, enrolling:

1. Creates or updates the project's entry in `.mcp.json`, leaving every other server as it was, and writes this
   machine's block, even if it would be empty. Another machine's block is never touched. An ownership line or a
   `PARLEY_RESPONDER` already in the entry stays for machines with no line of their own. A folder or an address
   already in the entry is taken out.
2. Mints `parleyAgentId` if the file has none.
3. Adds the agent to this machine's `node.json` when it is to be started unattended. That is the
   consent to start it: a peer's request can then start it with nobody watching.
4. Appends a `## Peers` block to the repository's instructions file if it has none. A `## Peers`
   section already there is never rewritten.

The whole set is checked before anything is written. Two repositories with one name, two names
over one folder, a folder that is not there, a folder not on this machine's own disks, and a name
the registry would refuse all stop the run with nothing changed.

**Enrolling on a machine that has only a tunnel.** Give the project's name and the broker's address:

```bash
parley enroll /srv --project acme --broker http://127.0.0.1:19100
```

The name is checked against that broker's registry, and nothing is written if it is another project. The address
goes in this machine's block. No project, store or port is created here. Under another role while another machine has a block
for that project, enrolling is refused, because the role is shared and the change would rename that machine's agent.

**Withdrawing takes this machine's block.** The entry stays while another machine's block names it, and
`parleyAgentId` stays while any entry remains.

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
| Not on this machine's own disks | `agents[0]: /home/you/Dropbox/orders is on Dropbox. Parley keeps its work only on this machine's own disks: move it to a local folder.` |
| Withdrawal, several entries, no project named | `left alone, it answers in 'alpha', 'beta' — name which one to withdraw from` |
| Withdrawal, the entry belongs to another agent | `left alone, its 'parley-alpha' entry is 'stock' rather than 'inventory'` |
| Withdrawal, the entry is pinned to a broker address | `left alone, its 'parley' entry is pinned to the broker at '127.0.0.1:51707', which this cannot match to project 'alpha'` |
