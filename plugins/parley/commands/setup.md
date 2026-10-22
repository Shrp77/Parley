---
description: Set this repository up as a Parley agent - check the tools are installed, enroll the repository, and name the peers.
disable-model-invocation: true
---

# Set this repository up as a Parley agent

Parley lets an agent in this repository request work of an agent in another repository, or
something it knows, and receive the result, without either seeing or editing the other's files.
Getting there takes three things: the tools on PATH, this repository enrolled in a project, and a
note telling this agent that a peer exists. `parley enroll` does the middle one. Nothing here is
written by hand.

**For one repository.** Several at once is `/parley:setup-many`, and the difference is not
convenience: `/parley:write-peers` reads the live roster, so every agent that registers makes
every block already written stale. Onboarding repositories one at a time rewrites the first
one's block once per repository that follows it.

Work through the steps below with the user. **Ask before running anything that installs
software or writes a file.** This command deliberately grants no pre-approved tools: it
installs global .NET tools and configures a repository the user may already have configured, and
both belong in the normal permission flow.

## 1. Check the tools are installed

```
dotnet tool list --global
```

Look for `parley.mcp` and `parley.cli`. Enrolling needs both: `parley-mcp` is what a launch
configuration names, and `parley` is the command that does the enrolling. If both are there, go
to step 2.

If either is missing, Parley is in alpha and **not yet published to NuGet**, so installing it
means building it from a clone. The clone's install script does it all - the tools, the broker
registered to start at logon, and a check that the broker answers. Tell the user that, show them
these commands, and ask before running them:

```
git clone https://github.com/Shrp77/Parley
cd Parley
./install.ps1        # Windows
./install.sh         # Linux, macOS
```

If the user already has a clone, skip the `git clone` and run the rest from it.

## 2. Check there is a project

A broker serves projects, and a repository is enrolled into one. If nothing is listening, nothing
works. The install script registers a broker that serves every project this machine names, so what
is usually missing is a project:

```
parley project list
```

If it names none, have the user name one on the first screen of:

```
parley
```

That is the only place a project is created - there is no command-line verb for it, so do not
offer one. The broker starts the project within seconds. If a broker is running, show the user who
is already registered before they go on:

```
parley registry
```

If this machine names more than one project, every command below takes `--project <name>` to say
which one is being enrolled into, because the key written into the repository is
`parley-<project>` and a run against the wrong one leaves it answering in a deployment nobody
meant. `parley use` says which project a command would act on, and why.

## 3. Enroll this repository

`parley enroll` takes the directory that **holds** the repositories, so for this one it is the
parent directory. It lists the git repositories directly under it and asks which to enroll, so
the person ticks only this repository. It needs a real terminal, which a session's shell is not:
ask the user to run it themselves, for example with the `!` prefix.

```
parley enroll <the parent directory>
```

Preview it first with `--describe`, which says what would change and changes nothing. Do not pass
`--all` here: it enrolls every repository beside this one.

Enrolling:

- merges an entry into this repository's `.mcp.json` - key `parley-<project>`, naming
  `PARLEY_AGENT`, `PARLEY_PROJECT` and `PARLEY_WORKSPACE` - and records the repository's identity
  there as `parleyAgentId`. Other servers in the file are left alone;
- adds the repository to this machine's `node.json`, which is the consent to start it. A peer's
  request may then start this agent with nobody watching, which spends whatever the agent costs
  to run. The command names the count before it configures anything, and **the user should be
  told this before they confirm**;
- writes the peers frame into `CLAUDE.md` (or `AGENTS.md` if that is what the project uses) when
  the file has no `## Peers` section. An existing section is left exactly as found;
- starts the agent so that it registers itself.

**The agent name** is taken from this repository's existing `PARLEY_AGENT` if its `.mcp.json`
already has one, and otherwise from the folder name, lower-cased with anything else turned into a
hyphen. There is no command-line option and no picker field to set it. If the folder-derived name
is not what the user wants - it should name the domain this repository owns, such as `orders`,
`billing` or `inventory`, not the directory, the framework or the model - put the intended
`PARLEY_AGENT` into the repository's `.mcp.json` before enrolling, and enroll will take it. If the
repository is already enrolled under the wrong name, change `PARLEY_AGENT` in its `.mcp.json`,
restart, and retire the old name from the terminal. Two repositories must never share a name:
identity is the one thing Parley makes unforgeable, and it does that by taking the name from the
launch environment, which is worth nothing if two servers claim the same name.

**The `owns` line is not written by enrolling, and not by you.** Once the agent attaches, Parley
asks it what it owns and what it does not, and the answer is the line. With the project's Auto
Accept switch on (the default) the answer is recorded at once, marked `automatic`; with it off the
user accepts it from the ownership review. Either way the user can see and correct it on every
surface, so tell them to check it once the agent has answered. Do not ask the user to write one
and do not put a guess of your own in `.mcp.json`: a line invented here would stand where the
agent's own statement belongs, and the record of who said it would be wrong.

### The file is per branch, and that is a trap

`.mcp.json` is a tracked file in most repositories, so **which branch is checked out decides
whether this agent exists**. Enrolled on a feature branch, the entry is on disk while that branch
is out and gone the moment somebody switches to one that predates it - and the agent is then
simply absent. Nothing errors. The session starts, the tools are not there, and the request a
peer sent it waits, flagged to the person running the deployment as never collected.

It is worse than it first looks, because a session reads `.mcp.json` **once, at startup**. Fixing
the file, or switching to the branch that has it, does nothing for a session already running:
`claude mcp list` will report the server connected - that is a separate process reading the
current file - while the session beside it still has no Parley tools at all. The two claims look
identical and are not.

So say this to the user when the entry is written on anything but their mainline: get the commit
onto the branch everything else is cut from, and restart the session afterwards.

## 4. Check the broker is reachable

`parley registry` answers if a broker is running, and says how to start one if not -
`parley --project <name> registry` when this machine names more than one project. Do this before
the restart rather than after: a repository configured against a broker that is not there
produces a server that exits at launch, which the user meets as a plugin that installed and did
nothing.

## 5. Restart

MCP servers are launched when Claude Code starts, so the tools appear after a restart. Tell the
user to restart, then check `/mcp` shows the Parley server connected - under the key
`parley-<project>`, or `parley` for a repository enrolled without a project name.
`parley_agents` then lists the roster, which is who this agent can address and therefore does not
include itself. Its own absence is the designed behaviour rather than a registration that failed -
the roster is refused to a caller with no open stream, so a roster that came back at all is the
proof that this agent attached.

## 6. Name the peers, in the session that comes back

**This one is the user's to run, and it happens after the restart.** `/parley:write-peers` is
marked `disable-model-invocation`, so this agent cannot invoke it - and the session reading
these instructions will not survive the restart to relay it either. So say it before
restarting, as part of handing over:

> Once you have restarted and `/mcp` shows Parley connected, run `/parley:write-peers`.

If no other repository is registered yet - the usual case when this is the first one being set
up - there is nothing for it to write, and it should be run again once the other side is
enrolled. It builds the block from the live roster rather than from memory, which is the whole
reason it is a command and not a paragraph in this file.

Do not let the user skip it permanently. Binding the tools tells this agent *how* to ask; it
does not tell it *that there is anyone to ask*. An agent with the tools bound and no idea a peer
exists will answer from this repository and guess at the rest, which is the failure Parley
exists to prevent, reached by installing it correctly and stopping one step early. The frame
that enrolling wrote is what makes stopping here survivable rather than silent: it tells an agent
that peers exist and that it must not invent them, and its placeholder line names the one command
that completes it.
