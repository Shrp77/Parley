# The Parley plugin for Claude Code

```
/plugin marketplace add Shrp77/Parley
/plugin install parley@parley
/parley:setup
```

## What it ships

| Component | What it is for |
|---|---|
| `/parley:setup` | Configures this repository as a Parley agent: checks the tools are installed, enrolls this repository with `parley enroll`, and sends you to restart - which is when the tools appear, and therefore before `/parley:write-peers` can read anything |
| `/parley:setup-many` | Configures several repositories in one pass: finds the git repositories under a directory, offers them to tick, then configures, catalogues and starts the ones chosen. Leaves the roster to `/parley:write-peers` |
| `/parley:write-peers` | Writes or refreshes the `## Peers` block in this repository's standing instructions, from the live roster |
| `/parley:files-list` | Shows the files placed in the project: sizes, hashes, and who placed each |
| `/parley:files-place` | Places a `.md` or `.txt` file from this machine in a conversation, as you |
| `/parley:files-remove` | Removes named files, whoever placed them, after showing what goes and asking you |
| `/parley:files-prune` | Removes every file in the project, behind the project's name typed back |
| `/parley:files-open` | Opens a placed file to read, on the broker's machine |
| `/parley:files-folder` | Opens a conversation's files folder, or the whole file location |
| `/parley:files-problems` | Lists what Parley's checks of the file location found, and dismisses one when you ask |
| `parley-operator` skill | The `parley` command-line tool - the operations that are deliberately not tools |
| `parley_waiting` hook | Tells an attended session when mail is waiting — on session start, on every prompt, after every batch of tool calls, and rather than letting the session go idle carrying it — and when a file it fetched has gone, or one it fetched or placed went missing or was changed |

The seven `files-` commands can only be typed by a person: each is marked so that a model cannot invoke it, because
they act on every agent's files at once, or place a file as the person.

## Why there is a hook

The inbox is pull, and an agent can only pull when its session takes a turn. A supervisor gives
an unattended agent a turn precisely because something is waiting; nothing gives one to a
repository somebody has open. That session can report `live` and never look.

The hook runs on the events that mean a person is interacting with this repository and says, in
one line, that there is mail. The human becomes the clock. It is not a push, but it removes the
failure where an agent is deaf because nobody reminded it.

**A session can also be pushed, through a channel.** `parley-mcp` declares itself a Claude Code
channel and sends an event the moment work arrives, so an open session collects its mail without
anybody typing. Channels are a research preview and Parley is not on Anthropic's channel
allowlist, so a session opts in by how it is started, naming the server's key in `.mcp.json`:

```
claude --dangerously-load-development-channels server:parley-acme
```

Claude Code shows a warning to confirm once per start. Without the flag the events are dropped
and the hook is all there is, which is why the hook stays. The event carries the same counts the
hook does and nothing more, for the same reason.

It reports **counts and never contents**. SPEC.md 13 requires a peer's words to arrive wrapped in
the envelope that marks them as data rather than instruction, and `parley_inbox` is what applies
that wrapping; text injected by a hook arrives with no envelope, in a channel the model has every
reason to trust. A number cannot carry an instruction. The agent still has to go and collect.

It is silent when nothing is waiting, and silent in repositories that are not Parley agents.

It is also silent on every error, including a missing Python interpreter - a session you cannot
work in because its mail-check threw is worse than one that does not know it has mail. The cost
of that is real and worth stating: **a hook that fails safe also fails invisibly**, so there is
no way to tell "nothing is waiting" from "this never ran". To check, run it by hand from a
repository that is a Parley agent:

```
python ~/.claude/plugins/cache/parley/parley/*/hooks/parley_waiting.py <<< '{"hook_event_name":"UserPromptSubmit","cwd":"."}'
```

**The event name is part of that payload and the script decides everything from it**, including
whether to speak at all: a payload naming no event is answered with silence, without the broker
being asked anything, whatever is waiting. A recipe that left it out would return the reassuring
answer on every run, which is the failure this check exists to catch.

Silence means nothing is waiting. An error means the hook would have been silent for the wrong
reason.

## Why there is a second setup command

`/parley:setup` is right for one repository and stops being right somewhere around the fourth,
for a reason that is not visible when you do one.

`/parley:write-peers` builds its block from the live roster, so every agent that registers makes
every block already written stale. Onboarding *n* repositories one at a time does not cost *n*
roster passes - the first repository's block is rewritten *n* times, and each rewrite is a
session, a restart and somebody's attention. Registering everything first and reading the roster
once at the end costs *n*, and the blocks that come out are the only ones that were ever true.

Nine tenths of setup does not need the tools at all: the launch configuration, the node entry and
the placeholder frame are file writes against known values. Only the roster needs a bound server.
So `/parley:setup-many` does that nine tenths for a list of repositories and refuses the rest -
it never writes an `owns` line, never replaces an existing `## Peers` section and never replaces
an `.mcp.json`.

Before anything is written, the count is named along with what being catalogued costs - a peer's
request may start each repository with nobody watching. `parley enroll --describe` reports the
same thing and changes nothing.

The work is `parley enroll`, a verb on the command-line tool, rather than a script shipped beside
this file. .NET is the only runtime a deployment is guaranteed to have - nothing is an agent
without `parley-mcp` on PATH, and that arrives through `dotnet tool install` - so a helper in any
other language would add a second runtime to a machine that already has one. The `parley_waiting`
hook is Python and is deliberately written to fail silently when its interpreter is missing; that
is survivable for a mail check, and not for a program writing configuration into eleven
repositories at once.

Enrolment never writes an `owns` line. It carries forward whatever a repository already says it
owns. The line comes from asking the agent what it is responsible for and discussing the answer to
agreement; nobody is asked to compose or affirm a line during enrolment.

## What it deliberately does not ship

**The MCP server configuration.** A plugin can bundle one, and can prompt for the values it
needs with `userConfig`, which looks like exactly the right mechanism. It is not, and the reason
is worth writing down because it will look like an oversight otherwise.

Claude Code stores the answers to a plugin's configuration dialog in `pluginConfigs`, and that
key is **user scope**. One install, one set of answers, across every repository you open.
Parley is a protocol between repositories - the whole point is that `orders` and `billing` are
different agents on one database - and identity is the single thing it makes unforgeable, by
taking the agent's name from the launch environment and offering no tool that mentions it. A
plugin-supplied server would hand every repository the same name and quietly undo that.

So `.mcp.json` stays per repository, where a per-repository fact belongs. The plugin writes it
for you instead of owning it.

**Anything the server already says.** When a client connects, `parley-mcp` sends MCP
`instructions` describing what Parley is for. That arrives through the protocol, so it reaches
any client that speaks MCP rather than only this one. A skill restating it would be a second
copy to drift, so the skill here covers the part the server has nothing to say about: the
operator's command-line tool, which is not exposed to any agent anywhere.

**A version.** Neither manifest declares one, so an install tracks the repository rather than
pinning to a string somebody has to remember to bump. A stale plugin that cannot be updated by
`/plugin marketplace update` would be a worse problem than an unpinned one.

## Alpha

The plugin does not install the server, because there is nothing to install it from yet:
`Parley.Mcp` and `Parley.Cli` are not published to NuGet, so `/parley:setup` walks you through
cloning and building them. At the first release those become `dotnet tool install -g
Parley.Mcp`, and step 1 of setup becomes one line.

## Other clients

Parley is not a Claude Code project and the protocol has no opinion about which client an agent
lives in. This directory is the Claude Code integration; an equivalent for another client
belongs beside it. Everything vendor-specific is in this directory - the server's own
`instructions` is what every client gets for free, and it needs no adapter at all.
