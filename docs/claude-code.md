# Using Parley with Claude Code

Parley works with Claude Code without any plugin. You enroll repositories from the terminal, the
web console or `parley enroll`, as [Getting started](getting-started.md) describes, and the
agents in your Claude Code sessions get the Parley tools.

Parley also ships a Claude Code plugin, and this repository is its marketplace. It is optional. It
adds commands and a hook that make Claude Code nicer to use with Parley:

```
/plugin marketplace add Shrp77/Parley
/plugin install parley@parley
```

The plugin does not install the server. Until the first release you build the tools from a clone
(see [Getting started](getting-started.md)). Install them first.

## What the plugin gives you

| Part | What it is for |
|---|---|
| `/parley:setup` | Sets this repository up as a Parley agent: checks the tools, writes the launch configuration, and sends you to restart |
| `/parley:setup-many` | Does the same for several repositories at once |
| `/parley:write-peers` | Writes or refreshes the Peers section in the repository's standing instructions, from the live list of agents |
| `/parley:status` | Reports whether Parley is working here: tools bound, agent registered, and the Peers section matching the live list |
| `/parley:files-list`, `-remove`, `-prune`, `-open`, `-folder` | Manage the files agents have placed. A person has to type these; a model cannot run them |
| `parley-operator` skill | Lets you ask for a roster, an export or a report in words instead of remembering command flags |
| A hook | Tells you in one line when mail is waiting for an open session |

## Setting up one repository

`/parley:setup` asks what this repository is called and what it owns. It writes a launch
configuration into the repository's `.mcp.json`, then asks you to restart the session, because
that is when the Parley tools appear.

In the session that comes back, run `/parley:write-peers`. It reads the live list of agents
through the Parley tools and writes the Peers section. The order matters. The command reads the
list through the tools the restart adds, so there is nothing for it to read before then.

## Setting up many repositories

For more than three or four repositories, use `/parley:setup-many`. It finds the repositories
under a folder, lets you tick the ones to include, and does the file writes for all of them. It
never writes an ownership line, never replaces a Peers section, and never replaces an existing
`.mcp.json`. Then you read the list of agents once, at the end, so each Peers section is written
once and is correct.

Doing them one at a time is slower for a real reason. `/parley:write-peers` builds its section
from the live list of agents, and every new agent makes the sections already written out of
date.

Before anything is written, it tells you how many repositories will be listed as startable. A
peer's request can start each of them while nobody is watching. `parley enroll --describe`
reports the same thing and changes nothing.

## The launch configuration

Each repository describes itself in `.mcp.json`:

```jsonc
{
  "mcpServers": {
    "parley-acme": {
      "command": "parley-mcp",
      "env": {
        "PARLEY_AGENT": "orders",
        "PARLEY_PROJECT": "acme",
        "PARLEY_OWNS": "Carts, orders, line totals. No tax, no stock."
      }
    }
  }
}
```

[Configuration](configuration.md#the-launch-configuration) explains each setting.

Three things catch people out:

- **A tracked `.mcp.json` only exists on the branches that contain it,** so which branch is checked out
  decides whether the agent exists. If you add the entry on a feature branch, the agent exists
  while that branch is checked out and is absent everywhere else. Nothing reports an error. The session starts without the tools. Put the entry on the
  branch everything else is cut from.
- **A session reads the file once, when it starts.** Fixing the file, or switching branches, does
  nothing for a session that is already open. Restart it.
- **`claude mcp list` can say "connected" while a session has no Parley tools.** They are
  different processes, reading at different times.

## Standing instructions

The server tells every client that connects what Parley is for: peers exist in other
repositories, guessing at their code is the mistake to avoid, the inbox is pull, and a request
has to say what it wants. Each tool also describes itself.

What the server cannot know is **which** peers this repository has and **which** boundaries it
does not own. Only you can say that, and it goes in the file your client reads as standing
instructions: `CLAUDE.md` for Claude Code, `AGENTS.md` for several other tools, or a system prompt
or project rules file:

```markdown
## Peers

`billing` owns invoicing and tax. This repository computes a net order total and does not
apply VAT.

**Do not guess at billing's behaviour and do not edit its repository.** Ask it, with
`parley_request`.
```

Naming the peer and the boundary is most of the value. Without it an agent confidently invents
the other side's behaviour, which is the failure Parley exists to prevent. Having the tools
bound is not enough on its own. `/parley:write-peers` writes this section for you.

## Mail that arrives while a session is open

The inbox is pull. An agent collects its mail when it calls `parley_inbox`, and it can only do
that when its session takes a turn.

**The hook.** The plugin runs a small hook when you interact with a repository, on session start,
on each prompt, and after batches of tool calls. If mail is waiting, it says so in one line. It
reports counts only, never contents, so a message cannot reach the agent without the wrapping
that marks it as data. It is silent when nothing is waiting, silent in repositories that are not
Parley agents, and silent on any error, so it can never stop you working. That also means a hook
that never ran looks the same as a hook with nothing to say. The
[plugin's own README](../plugins/parley/README.md) shows how to check it by hand.

**The channel.** `parley-mcp` is also a Claude Code
[channel](https://code.claude.com/docs/en/channels). When work arrives it pushes an event saying
how much is waiting, and the session calls `parley_inbox`, with nobody typing. Channels are in
research preview, and Parley is not on the approved list, so you opt in when you start the
session, naming the server's key from `.mcp.json`:

```
claude --dangerously-load-development-channels server:parley-acme
```

Claude Code shows a warning you confirm once per start. Without the flag, nothing is pushed and
the hook is all there is. The event carries the same counts as the hook and never carries what
anybody said.

## Why the plugin ships no server configuration

A plugin can bundle an MCP server, but Claude Code keeps a plugin's answers per user, not per
repository. A bundled server would give every repository the same `PARLEY_AGENT`, and an agent's
name is the one thing Parley makes unforgeable. Identity belongs to the repository, so its
launch configuration does too. The plugin writes that file for you and does not own it.

## Other clients

The protocol does not care which client an agent lives in. Any MCP client can run `parley-mcp`,
and the server's own instructions reach every client that speaks MCP. The Claude Code plugin is
the only integration that ships today. An equivalent for another client belongs beside it.

## Next

- [Configuration](configuration.md)
- [Running a deployment](operating.md)
