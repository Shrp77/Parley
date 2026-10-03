# Contract: the command surface after 019

> **Superseded in part, as of 0.32.0.** This contract was written before the build, and three
> things in it did not survive. Each is marked where it occurs below, with the original kept so the
> reasoning stays readable:
>
> - `project add`, `project set` and `project remove` are refused on the command line (exit 64).
>   Creating, changing and removing a project is the terminal's: run `parley` and do it there. Only
>   `project list` stays.
> - `--listen` is gone from every command, `project` included. No command makes a broker reachable
>   from another machine; the supported route is an SSH tunnel (see the README).
> - `serve` kept `--with-web-ui` and `--web-ui <url>`. `parley --help` still offers them.

Twelve verbs stay, sixteen go. The list was derived by sweeping every script, hook and plugin command in
the repository first (see [research.md](../research.md) R5) and keeping what a program calls — not by
deciding what looked useful.

## Global options

| Option | Variable | Means | Changed |
|---|---|---|---|
| `--project`, `-p` | `PARLEY_PROJECT` | which deployment to act on | no |
| `--broker`, `-b` | `PARLEY_BROKER` | connect to this address | no |
| `--config` | `PARLEY_CONFIG` | **the root directory** | **yes** — named a file |
| `--db`, `-d` | `PARLEY_DB` | — | **removed** (FR-007) |
| `--node-config` | `PARLEY_NODE_CONFIG` | the machine catalogue | no |

## What stays

| Verb | Shape | Why it stays |
|---|---|---|
| `serve` | `[--log <path>] [--with-node] [--with-web-ui [--web-ui <url>]]` | brings the machine up. **`--listen`, `--db` and `--no-web-ui` are gone.** *Planned:* `--with-web-ui` and `--web-ui` were to go too, with `serve` always starting the surfaces (FR-030, FR-031, FR-039). *Built (0.32.0):* both stay, and the web UI starts only when asked for |
| `up` | `[--node-config <path>]` | the node alone |
| `autostart` | `install` \| `remove` \| `status` | installers call `install` and `remove`; `status` is how a machine that stopped serving is diagnosed. **All four options gone** (FR-039) |
| `terminal` | *(forwarded verbatim)* | the surface |
| `web-ui` | `[<url>]` | the surface |
| `use` | `[<project>] [--shell] [--clear]` | prints the line that sets `PARLEY_PROJECT` |
| `project` | `list` | `list` is called by both installers, including the readiness poll. **`list` is where a person answers "what listens where"** — see below. *Planned:* `add <name>`, `set <name>` and `remove <name> [--destroy]` stayed too, `add` keeping `--listen` (FR-020) and `--description`. *Built (0.32.0):* `add`, `set` and `remove` exit 64 and send the person to the terminal (`parley`); FR-020 was withdrawn, so no command takes `--listen` |
| `enroll` | `<dir> [--all\|--none] [--describe]` | one verb, no subcommands (FR-040) |
| `registry` | — | named by both installers' closing check |
| `waiting` | `<agent\|role> [--with-files]` | **the plugin hook, on nearly every turn of every session** |
| `open` | — | both installers, before stopping the broker |
| `files` | `list` \| `remove` \| `prune` \| `open` \| `folder` | **five plugin slash commands execute these.** A slash command is a program calling, and has no terminal to be pointed at |

Plus `--version` — the only way to tell a real install from `dotnet tool update`'s silent no-op — and
`--help`.

## What goes

| Verb | Points at |
|---|---|
| `retire`, `reinstate`, `purge` | the agents pane, where the name is typed back |
| `start` | the agents pane |
| `instruct` | the agents pane |
| `export`, `archive`, `unarchive`, `destroy`, `force-conclude` | the conversations view |
| `prune` | the Health dialog |
| `refusals`, `clarifications`, `results`, `flags` | the Health dialog |
| `instructions` | the agents pane |

Each of the sixteen either takes an identifier a person cannot obtain from this tool — **there is no
`parley conversations`** — or is a read the surfaces already show better. Both surfaces bind every
operator operation, so nothing leaves the product.

`prune` is the one debatable removal: it takes no identifier and a person might reasonably schedule it.
Nothing in this repository calls it and it deletes text permanently, so it goes with the rest. Recorded so
that reinstating it is a decision rather than a rediscovery.

## `project list` — what listens where

Parley does not own firewall rules, tunnels or routes and should not try to. What it owes the person
writing them is one legible answer.

**Today's output misleads for exactly that use.** It prints:

```
orders           http://127.0.0.1:51707       reachable
                 node   …\orders-catalogue.json
```

`reachable` means *this process connected to it* — from this machine. Somebody planning a firewall rule
reads it as reachable from the network, and **no rule they write can make that true**, because nothing off
the machine can address `127.0.0.1`. The word is doing the opposite of its job (FR-023). The `node` line
names the per-project catalogue 018 made meaningless, and goes.

After, as planned (the remedy line is superseded; see below):

```
orders        http://127.0.0.1:19100     answering · this machine only
              store   …\projects\orders\parley.db
              To reach it from another machine: (the planned remedy, removed in 0.32.0)

billing       http://0.0.0.0:19101       answering · reachable from other machines
              store   …\projects\billing\parley.db
```

- **Two facts, never merged into one word** (FR-022): whether it answered, and whether anything outside
  this machine could address it.
- **The remedy sits beside the limitation** (FR-024), so the answer is where the question is.
- **Endpoint, store and reachability in one place** (FR-025) — no second source to consult.

**The planned remedy is superseded (0.32.0).** It read `project set orders --listen
http://0.0.0.0:19100`, and both halves of that were removed: `project set` is the terminal's, and no
command takes `--listen`. As built, `project list` prints three things on separate lines: whether the
broker answered, whether only this machine can address it, and its store. The way to reach a broker
from another machine is a tunnel: an SSH port-forward to the project's loopback port, with
`PARLEY_BROKER` pointed at the local end (see the README). Adding a project in the terminal says so
when it hands out the port. An endpoint edited into `project.json` by hand to bind a network address
is still served, and the listing then says that any machine able to reach it can address it, with no
credentials.

Parley says nothing about whether a firewall *permits* the traffic. It cannot know, and a tool that
guessed would be believed.

## How a removed verb behaves

```
$ parley export c-4f2a
That moved to the terminal and the web UI, which can show you the conversation to pick.
Run `parley terminal`, open the conversation, and copy or export it from there.
```

- Exit code **64** (`EX_USAGE`), so a script detects it (FR-051).
- Names the surface and how to reach it.
- **Never says the old verb after the word `parley`.** See below — this is a build constraint, not a
  style choice.

## The constraint that breaks the build

`tests/Parley.Mcp.Tests/PluginTests.cs` → `The_operator_skill_documents_every_operator_command` asserts
the verb sets in `src/Parley.Cli/Program.cs` and `plugins/parley/skills/parley-operator/SKILL.md` are
**exactly equal, both directions**. It extracts them from the whole source file, comments and strings
included:

```csharp
Regex.Matches(text, @"parley ([a-z][a-z-]*)")
```

So a stub reading *"`parley export` moved to the terminal"* puts `parley export` back into `Program.cs`,
and the test then demands `SKILL.md` document a command that no longer exists.

**The rule: a stub names the surface, never `parley <removed verb>`.** Write *"That moved to the terminal"*,
not *"`parley export` moved"*. `SKILL.md` loses the sixteen in the same change.

A companion test, `The_agent_facing_command_reaches_for_no_operator_command`, holds `write-peers.md` and
`status.md` to containing no operator verb at all, so the same discipline applies there.

## Documentation that moves with this

| File | What |
|---|---|
| `README.md` | ~90 mentions |
| `plugins/parley/skills/parley-operator/SKILL.md` | the full listing at L122–178, and the sixteen |
| `plugins/parley/README.md` | prose sweep |
| `plugins/parley/commands/setup.md` | `project add --db --listen` → `project add <name>` (planned; as built, naming a project is the terminal's) |
| `plugins/parley/commands/setup-many.md` | `enroll scan\|plan\|apply` → the one verb |
| `plugins/parley/commands/files-*.md` | unchanged — those verbs stay |
| `SPEC.md` §4.1 | the manifest illustration becomes the layout |
| `SPEC.md` §10.2 | **operations do not change**; only their command-line spelling. A sweep, not a rewrite |
