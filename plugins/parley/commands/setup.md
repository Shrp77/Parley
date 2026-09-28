---
description: Configure this repository as a Parley agent - check the server is installed, write the launch configuration, and name the peers.
disable-model-invocation: true
---

# Set this repository up as a Parley agent

Parley lets an agent in this repository request work of an agent in another repository, or
something it knows, and receive the result, without either seeing or editing the other's files. Getting there takes three things:
the server on PATH, a launch configuration naming this repository's agent, and a note telling
this agent that a peer exists.

**For one repository.** Several at once is `/parley:setup-many`, and the difference is not
convenience: `/parley:write-peers` reads the live roster, so every agent that registers makes
every block already written stale. Onboarding repositories one at a time rewrites the first
one's block once per repository that follows it.

Work through the steps below with the user. **Ask before running anything that installs
software or writes a file.** This command deliberately grants no pre-approved tools: it
installs a global .NET tool and edits configuration the user may already have, and both belong
in the normal permission flow.

**Do not assume the repository is clean.** Every file below may already exist, and the ways it
can be wrong are not the same: `.mcp.json` may carry variables from an older shape that nothing
reads any more, `node.json` may still hold consent to start a repository whose registration was
deleted, and a `## Peers` section may carry project rules this command did not write and cannot
recreate. Being told to treat the setup as new says nothing about the files - a purge clears the
broker's store and touches neither the repository nor the machine. So read each one before
writing it, and say what was found. A step that quietly overwrites something is a step that
reports success either way.

## 1. Check the server is installed

```
dotnet tool list --global
```

Look for `parley.mcp`. If it is there, go to step 2.

If it is not, Parley is in alpha and **not yet published to NuGet**, so installing it means
building it from a clone. Tell the user that, show them these commands, and ask before running
them:

```
git clone https://github.com/Shrp77/Parley
cd Parley
dotnet pack src/Parley.Mcp -c Release
dotnet tool install -g --add-source src/Parley.Mcp/bin/Release Parley.Mcp
```

That puts `parley-mcp` on PATH, which is what lets a launch configuration name it. The
operator's command-line tool is a separate install and is not needed to finish this setup:

```
dotnet pack src/Parley.Cli -c Release
dotnet tool install -g --add-source src/Parley.Cli/bin/Release Parley.Cli
```

If the user already has a clone, skip the `git clone` and run the rest from it.

## 2. Ask for the three things only the user knows

**The agent name for this repository.** Lowercase letters, digits and hyphens, starting with a
letter, up to 32 characters. It should name the domain this repository owns - `orders`,
`billing`, `inventory` - not the directory, the framework, or the model. Peers address it by
this name, it is recorded in the registry the first time the server starts, and it is fixed by
the launch configuration rather than chosen at run time.

Two repositories must never be given the same name. Identity is the one thing Parley makes
unforgeable, and it does that by taking the name from the launch environment and offering no
tool that mentions it - which is worth nothing if two servers claim the same name, because
either one may then collect the other's mail.

**What this repository owns.** One line that every other agent sees beside this one's name when
it lists the roster. It is how a peer decides whether a question is this repository's, so say
what you own **and** what you do not - the exclusion is the half that stops somebody asking the
wrong agent:

> `Carts, orders and line totals. No tax, no stock.`
>
> `Invoicing, tax and payment capture. Does not price orders or reserve stock.`

**Keep it to about a line, even though nothing enforces that any more.** The length bound came
off so that an agent asked what it owns can answer in two halves and name a peer. What has not
changed is the cost: this text is read far more often than it is written - once per peer, every
time anybody lists the roster - and it is charged against the working memory of every agent that
reads it. Both examples above are under eighty characters, and neither is short at the expense of
saying what it means.

**Put the repository's own words in front of them first, and do not write the line.** A cold
prompt gets a vague answer or none, and there is something honest to offer instead: quote what
the project already says about itself - the description line of its `README`, the `description`
field of its package manifest, the top-level directories - and name where each came from, so
the user is reading their own project rather than this agent's reading of it. Then ask.

The distinction is not a nicety. Evidence invites them to write a line; a draft invites them to
approve one, and an approved draft is model-written text that looks person-written from every
other repository in the deployment - nobody on the far side can tell. SPEC.md §17 question 10
closes this deliberately: *"It is not self-declared, and that is the whole of why it is worth
reading. A field a model could write would be no more trustworthy than the prose it replaces,
merely centralised."* The field is worth routing by because a person meant it.

**Never propose the exclusion half.** A repository cannot supply it: no tax code in the tree
does not mean this repository does not own tax, only that it has not been written yet - and
that half is the one that stops somebody asking the wrong agent. Suggesting it is inventing the
load-bearing part with confidence. Ask, and take "I don't know yet" for an answer; a name and
no line still leaves a requester the name to go on, and a receiver that does not own the work
asks the agent that does - which is what §17 question 10 says it falls back to.

It must be one line and it is refused at start-up if it is not, so do not paste prose with a
line break in it. It goes into a JSON file, so escape a quotation mark or a backslash if the
user's wording has one - an unescaped one makes the whole launch configuration unreadable and
the server simply never starts. Ask the user; do not write it for them from what you can see in the
repository, because a boundary is a decision somebody made rather than something a directory
listing implies. If they are unsure, leaving it out is better than guessing - an agent with
none is listed with none, and askers fall back to its name.

**The broker.** One process owns the store and everything else connects to it, so what this
repository needs is an address rather than a path. `127.0.0.1:51707` is the default and needs no
configuration; a broker on another machine goes in `PARLEY_BROKER`.

If the user runs more than one Parley deployment on this machine, `parley project list` names
them and gives each one's address. A launch configuration may carry `PARLEY_PROJECT` instead of
`PARLEY_BROKER`, which is worth preferring when there is more than one: an address is a port
number and ports move, so a committed `.mcp.json` naming a project survives a change that would
otherwise have to be made in every repository at once. It names *where to connect* and nothing
else - `PARLEY_AGENT` and `PARLEY_OWNS` are unaffected, and a project cannot supply either.
Naming a project this machine does not describe stops the server at launch rather than falling
back, because connecting an agent to the wrong deployment looks entirely normal from both ends.

The user needs one running. If nothing is listening, nothing works - the server exits at launch
saying so rather than starting and failing every tool. The install script registers a broker that
serves every project this machine names, so what is usually missing is a project. If
`parley project list` names none, have the user name one, on the first screen of:

```
parley
```

That is the only place a project is created - `parley project add` was removed on 2026-09-25, so
do not offer it. The broker starts the project within seconds. Naming it is the whole of creating
it: the project gets a
folder of its own, a `parley.db` inside it, and a port claimed for it. **You do not choose where
the store goes, and that is the point** - it is one SQLite file shared by every repository that
should be able to reach every other, so it must live outside all of them, and a path under one
repository would make that repository's clone a prerequisite for everybody else's agent. The file
does not need to exist; the broker creates and migrates it, and nothing else does.

If a broker is running, show the user who is already registered before they choose a name:

```
parley registry
```

## 3. Write the launch configuration

Claude Code reads `.mcp.json` at the repository root. If the file already exists, **merge into
it** - add one entry to `mcpServers` and leave every other server alone. Never replace the file.

The key is `parley-<project>`, or the bare `parley` when the deployment names no projects:

```json
{
  "mcpServers": {
    "parley-<project>": {
      "command": "parley-mcp",
      "env": {
        "PARLEY_AGENT": "<the agent name>",
        "PARLEY_PROJECT": "<project>",
        "PARLEY_OWNS": "<leave this out; it is obtained by asking the agent>",
        "PARLEY_WORKSPACE": "${PARLEY_WORKSPACE:-<absolute path to this repository>}"
      }
    }
  }
}
```

A repository may answer in more than one project by holding one entry for each, with a different
name and a different `owns` line in each - brokers do not federate, so the project it joins is the
only set of peers it can reach. An entry already there under the bare `parley` key is left exactly
as it is: renaming it would change every tool name the agent behind it already sees.

Substitute the real values. Four notes on the shape:

- The `${VAR:-default}` form is environment-variable expansion, which `.mcp.json` supports.
  `.mcp.json` is usually committed and both paths are machine-specific, so the default is what
  this machine uses and a teammate overrides it by setting the variable rather than by editing
  a shared file.
- The clarify cap is **not** here. It used to be, set per repository, which meant one
  deployment could admit forty clarifications on one exchange and three on another with nothing
  to say why. It belongs to the broker now, and it is still the measurement SPEC.md 17 question
  4 asks for rather than a considered limit - the terminal's health view is what says where depth
  actually settles.
- `PARLEY_AGENT` and `PARLEY_OWNS` are written plainly, with no fallback. Both are the same on
  every machine, because they name what this repository is rather than where it lives. **Omit
  `PARLEY_OWNS` rather than guessing at it.** An empty string means the same as absent, and a
  placeholder left in by accident is a claim the user did not make -- but the reason to leave it
  out is stronger than that now: **the line is obtained by asking the agent, and a person
  accepting the answer is what writes it.** Press `k` on an agent in `parley`, or use
  the console's ask, and the accepted line is written into this file for them. A line invented
  here would be a worksheet answer standing where a measured one belongs, and the record of who
  said it would be wrong.
- `PARLEY_WORKSPACE` is optional and only an operator ever sees it; it is how `parley registry`
  tells two similarly named agents apart. Write the absolute path, not `.` - the server
  deliberately does not default it, because a plausible and wrong workspace is worse than an
  honest blank.

These are optional and can be left out: `PARLEY_RESPONDER` (what to record as having produced a
message, `unknown-model` if unset), `PARLEY_BROKER` (where the broker is, the local default if
unset), `PARLEY_MACHINE` (this machine's name, detected if unset).

The sweep cadence and the retention pair are **not** here any more, and that is worth saying to
a user who has seen an older configuration. They describe the deployment rather than this
repository, and they now belong to `parley serve` - one process applies them, so there is one
answer. Setting them per agent used to mean a deployment could enforce different numbers on one
file with nothing to show which was in force.

### The file is per branch, and that is a trap

`.mcp.json` is a tracked file in most repositories, so **which branch is checked out decides
whether this agent exists**. Written on a feature branch, the entry is on disk while that branch
is out and gone the moment somebody switches to one that predates it — and the agent is then
simply absent. Nothing errors. The session starts, the tools are not there, and the request a
peer sent it waits, flagged to the person running the deployment as never collected.

It is worse than it first looks, because a session reads `.mcp.json` **once, at startup**. Fixing
the file, or switching to the branch that has it, does nothing for a session already running:
`claude mcp list` will report the server connected — that is a separate process reading the
current file — while the session beside it still has no Parley tools at all. The two claims look
identical and are not.

So say this to the user when the entry is written on anything but their mainline: get the commit
onto the branch everything else is cut from, and restart the session afterwards. Until then the
registration is only as reliable as their next `git switch`.

## 4. Ask whether this repository may answer unattended

Everything so far needs a person to take a turn: this agent answers when its session does, and
its session acts when the user sends it something. So an exchange with a peer needs a human at
both ends unless this machine offers otherwise.

**The offer is not in `.mcp.json` any more.** How to start a repository is configuration
belonging to the machine it is on, so it lives in `node.json` - beside where `parley up` is run,
or wherever `PARLEY_NODE_CONFIG` points - and never reaches the broker at all. The store holds no
executable command line, and an agent that has never run still has a record of how to start it.

```json
{
  "agents": {
    "<the agent's role>": {
      "command": "claude",
      "workspace": "<absolute path to this repository>"
    }
  }
}
```

Write only the runner. The node appends the flags that make it speak the turn protocol and the
agent's own name, so `claude` on its own is the whole value; flags the user does want — a
permission mode, a model — go after it and are kept.

The workspace is where the node starts it, and it is read from this file rather than from the
registry on purpose. The registry's workspace is what an agent's adapter reported about itself,
which describes where it ran last rather than instructing where to run it — and a node taking a
directory from a registration would be taking a path from the network.

**Ask the user before adding this repository to that file, and take no for an answer.** Listing
it here is the permission: it says the node may start this repository when a peer asks it
something,
which spends their tokens with nobody watching. There is no second flag, and there used to be —
recording *how* to start a repository had been read as consent to run it unwatched, so the two
had to be prised apart. When starting is the machine's own configuration the question does not
arise.

Withdrawing it later means removing that entry. Nothing else, and nowhere else.

**One thing to check while editing it:** `node.json` is keyed by *role*, not by the full agent
name. An agent is `role@machine` and the machine half is detected, so this file describes this
machine and an entry here can only ever mean an agent on it.

## 5. Check the broker is reachable

`parley registry` answers if one is running, and says how to start one if not - `parley --project <name> registry`
when this machine names more than one project, since it will not choose between them. This is worth
doing before the restart rather than after: a repository configured against a broker that is not
there produces a server that exits at launch, which the user meets as a plugin that installed
and did nothing.

If the store sits inside a repository despite step 2's advice, add it to that repository's
`.gitignore`. It holds the full text of every question and answer.

## 6. Write the peers block, with what is knowable now

**Do not leave this until after the restart.** Everything below needs the tools; this does not,
and skipping it is how a repository ends up configured, connected, and with standing
instructions that never mention Parley. Step 8 says why that state is the failure this whole
command exists to prevent - and it is reached by stopping one step early, behind a restart that
ends the session that would have reminded the user.

The block has two halves and only one of them needs the roster. The frame - that peers exist,
that they cannot be seen from here, that guessing is the mistake - is true of every deployment
and can be written now. Which peers, and what each owns, cannot: that is what
`/parley:write-peers` reads from the live roster once the tools are bound.

So write the frame, in this repository's standing instructions - `CLAUDE.md` at the repository
root, or `AGENTS.md` if that is what the project uses. Replace an existing `## Peers` section
rather than appending a second one, and leave the rest of the file alone.

**A `## Peers` section already in the file may not be one this command wrote.** It holds three
kinds of line and only two of them are yours: the frame below, the roster of peers, and
whatever the project has added about how that roster is to be read - which document is
authoritative when a peer's claim conflicts with it, which entries are still open, what an
`owns` line does not settle. Rewrite the first two. Carry the third across unchanged.

A line that cannot be sorted into one of the three is a line to show the user, not one to
decide about. It is in the file because somebody put it there, and rules about how to read the
roster belong beside the roster - moving them out is not a tidier answer, it is the same
deletion with an extra step. The command that cannot tell whose line it is, is the command
about to delete it.

```markdown
## Peers

Other repositories have their own agents, reachable through Parley. They cannot see this
repository and this repository cannot see theirs: only the text of a request and its result
crosses between them. `parley_inbox` is pull rather than push - call it when you start work,
and again when you are waiting on a result.

To hand over a file, place it with `parley_file_place` and name it and its sha256 in your
request; fetch a file you are sent with `parley_file_fetch`. Only `.md` and `.txt` files can be
placed, so code travels as `Name.cs.txt`.

- The roster has not been read yet. Run `/parley:write-peers` to fill this in.

**Do not guess at what these repositories do, and do not edit them.** When you need work done
in one, or something it knows, send its agent a `parley_request` saying what you want, and what
it is for when that is not obvious. If a request you receive is unclear, ask with
`parley_clarify` rather than guessing. Do the work other agents request of this repository.
```

The placeholder line is deliberate and is not a to-do nobody will action. An agent reading this
block before the roster is filled in knows the two things that keep it from doing damage - that
peers exist and that it must not invent them - and knows the one command that completes it.
An agent reading no block at all knows neither.

## 7. Restart

MCP servers are launched when Claude Code starts, so the tools appear after a restart. Tell the
user to restart, then check `/mcp` shows `parley` connected. `parley_agents` then lists the
roster, which is who this agent can address and therefore does not include itself. Its own
absence is the designed behaviour rather than a registration that failed - the roster is
refused to a caller with no open stream, so a roster that came back at all is the proof that
this agent attached, and an agent is registered at the moment it attaches.

This step is seventh rather than last because everything below it needs the tools, and nothing
above it did. Naming the peers used to come first here, which could not work: it calls
`parley_agents`, and until the restart there is no such tool to call.

## 8. Name the peers, in the session that comes back

**This one is the user's to run, and it happens after the restart.** `/parley:write-peers` is
marked `disable-model-invocation`, so this agent cannot invoke it - and the session reading
these instructions will not survive the restart to relay it either. So say it before
restarting, as part of handing over:

> Once you have restarted and `/mcp` shows `parley` connected, run `/parley:write-peers`.

If no other repository is registered yet - the usual case when this is the first one being set
up - there is nothing for it to write, and it should be run again once the other side is
configured. It builds the block from the live roster rather than from memory, which is the
whole reason it is a command and not a paragraph in this file.

Do not let the user skip it permanently. Binding the tools tells this agent *how* to ask; it
does not tell it *that there is anyone to ask*. An agent with the tools bound and no idea a peer
exists will answer from this repository and guess at the rest, which is the failure Parley
exists to prevent, reached by installing it correctly and stopping one step early.

Step 6 is what makes stopping here survivable rather than silent: the frame is already in the
file, so an agent that gets this far knows peers exist and knows it must not invent them. What
this step adds is who they are. The placeholder line it leaves behind is the reminder, sitting
in the one file that is read on every turn.
