# Contract: the command surface after 019

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
| `serve` | `[--log <path>] [--with-node]` | brings the machine up. **`--listen`, `--db`, `--with-web-ui`, `--web-ui`, `--no-web-ui` are gone**: it serves everything and starts the surfaces (FR-030, FR-031, FR-039) |
| `up` | `[--node-config <path>]` | the node alone |
| `autostart` | `install` \| `remove` \| `status` | installers call `install` and `remove`; `status` is how a machine that stopped serving is diagnosed. **All four options gone** (FR-039) |
| `terminal` | *(forwarded verbatim)* | the surface |
| `web-ui` | `[<url>]` | the surface |
| `use` | `[<project>] [--shell] [--clear]` | prints the line that sets `PARLEY_PROJECT` |
| `project` | `list` \| `add <name>` \| `set <name>` \| `remove <name> [--destroy]` | `list` is called by both installers, including the readiness poll. `add` loses `--db` and `--node-config`; it keeps `--listen` (FR-020) and `--description`. **`list` is where a person answers "what listens where"** — see below |
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

After:

```
orders        http://127.0.0.1:19100     answering · this machine only
              store   …\projects\orders\parley.db
              To reach it from another machine: parley project set orders --listen http://0.0.0.0:19100

billing       http://0.0.0.0:19101       answering · reachable from other machines
              store   …\projects\billing\parley.db
```

- **Two facts, never merged into one word** (FR-022): whether it answered, and whether anything outside
  this machine could address it.
- **The remedy sits beside the limitation** (FR-024), so the answer is where the question is.
- **Endpoint, store and reachability in one place** (FR-025) — no second source to consult.

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
| `plugins/parley/commands/setup.md` | `project add --db --listen` → `project add <name>` |
| `plugins/parley/commands/setup-many.md` | `enroll scan\|plan\|apply` → the one verb |
| `plugins/parley/commands/files-*.md` | unchanged — those verbs stay |
| `SPEC.md` §4.1 | the manifest illustration becomes the layout |
| `SPEC.md` §10.2 | **operations do not change**; only their command-line spelling. A sweep, not a rewrite |
