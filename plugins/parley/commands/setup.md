---
description: Configure this repository as a Parley agent - check the server is installed, write the launch configuration, and name the peers.
disable-model-invocation: true
---

# Set this repository up as a Parley agent

Parley lets an agent in this repository ask an agent in another repository a question, and
receive an answer, without either seeing the other's files. Getting there takes three things:
the server on PATH, a launch configuration naming this repository's agent, and a note telling
this agent that a peer exists.

Work through the steps below with the user. **Ask before running anything that installs
software or writes a file.** This command deliberately grants no pre-approved tools: it
installs a global .NET tool and edits configuration the user may already have, and both belong
in the normal permission flow.

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

**What this repository owns.** One line, up to 100 characters, that every other agent sees
beside this one's name when it lists the roster. It is how a peer decides whether a question
is this repository's, so say what you own **and** what you do not - the exclusion is the half
that stops somebody asking the wrong agent:

> `Carts, orders and line totals. No tax, no stock.`
>
> `Invoicing, tax and payment capture. Does not price orders or reserve stock.`

It must be one line and it is refused at start-up if it is not, so do not paste prose with a
line break in it. It goes into a JSON file, so escape a quotation mark or a backslash if the
user's wording has one - an unescaped one makes the whole launch configuration unreadable and
the server simply never starts. Ask the user; do not write it for them from what you can see in the
repository, because a boundary is a decision somebody made rather than something a directory
listing implies. If they are unsure, leaving it out is better than guessing - an agent with
none is listed with none, and askers fall back to its name.

**The database path.** One SQLite file, shared by every repository that should be able to reach
every other. It therefore has to live outside all of them - a path under one repository makes
that repository's clone a prerequisite for everybody else's agent, and a path inside each
repository gives every agent its own private database in which no peer exists. Something like
`~/.parley/<project>/parley.db` works. The file does not need to exist; the server creates and
migrates it on first launch.

If the operator's tool is installed and the database already exists, show the user who is
already registered before they choose a name:

```
parley --db <path> registry
```

## 3. Write the launch configuration

Claude Code reads `.mcp.json` at the repository root. If the file already exists, **merge into
it** - add a `parley` entry to `mcpServers` and leave every other server alone. Never replace
the file.

```json
{
  "mcpServers": {
    "parley": {
      "command": "parley-mcp",
      "env": {
        "PARLEY_DB": "${PARLEY_DB:-<absolute path to the database>}",
        "PARLEY_AGENT": "<the agent name>",
        "PARLEY_OWNS": "<what this repository owns, and what it does not>",
        "PARLEY_WORKSPACE": "${PARLEY_WORKSPACE:-<absolute path to this repository>}",
        "PARLEY_CLARIFY_CAP": "200"
      }
    }
  }
}
```

Substitute the real values. Four notes on the shape:

- The `${VAR:-default}` form is environment-variable expansion, which `.mcp.json` supports.
  `.mcp.json` is usually committed and both paths are machine-specific, so the default is what
  this machine uses and a teammate overrides it by setting the variable rather than by editing
  a shared file.
- `PARLEY_CLARIFY_CAP` is written explicitly even though 200 is also what the server uses when
  it is absent. The number is a measurement rather than a considered limit - SPEC.md 17
  question 4 wants the distribution real traffic produces, and a cap censors the evidence that
  would justify it - so it is put where an operator will see it and can lower it, instead of
  being a default nobody knows is in force. Say so if the user asks: it is high on purpose, one
  exchange can carry a great deal of text at that setting, and `parley clarifications` is what
  says where depth actually settles.
- `PARLEY_AGENT` and `PARLEY_OWNS` are written plainly, with no fallback. Both are the same on
  every machine, because they name what this repository is rather than where it lives. Omit
  `PARLEY_OWNS` entirely if the user had no answer; an empty string means the same as absent,
  but a placeholder left in by accident is a claim they did not make.
- `PARLEY_WORKSPACE` is optional and only an operator ever sees it; it is how `parley registry`
  tells two similarly named agents apart. Write the absolute path, not `.` - the server
  deliberately does not default it, because a plausible and wrong workspace is worse than an
  honest blank.

These are optional and can be left out: `PARLEY_RESPONDER` (what to record as having produced a
message, `unknown-model` if unset), `PARLEY_TICK_SECONDS` (how often the sweep runs, 60 if
unset), `PARLEY_RETENTION_IDLE_HOURS` and `PARLEY_RETENTION_MAX_AGE_HOURS` (how long the message
log keeps what it carried, 72 and 720 if unset). Set the retention pair on **every** agent's
server or none: the servers are what apply the policy, so setting it in one place and not
another has agents quietly enforcing different numbers on one file.

## 4. Ask whether this repository may answer unattended

Everything so far needs a person to take a turn: this agent answers when its session does, and
its session acts when the user sends it something. So an exchange with a peer needs a human at
both ends unless this repository offers otherwise.

`PARLEY_INVOKE_COMMAND` is that offer, and it is consent and command in one:

```json
"PARLEY_INVOKE_COMMAND": "claude"
```

Set it, and `parley up` may keep this agent resident — started once, given a turn whenever a
peer needs one, kept alive so it remembers the conversations it is in. Leave it out and nothing
ever starts this repository.

**Ask the user, and take no for an answer.** This is the one setting that lets their machine run
a model and spend their tokens with nobody watching, so it is theirs to grant rather than a
default to apply. It is also per repository on purpose: it belongs in this repository's launch
configuration because that is the only place the permission can honestly come from.

Write only the runner. The supervisor appends the flags that make it speak the turn protocol,
and the agent's own name from the registry, so `claude` on its own is the whole value. Any flags
the user does want — a permission mode, a model — go after it and are kept.

Withdrawing it later means removing the line and opening this repository once. That is enough;
there is no second step somewhere the user will not look.

## 5. Create the database's directory

Create the parent directory of the database path if it does not exist. Do not create the file.

If the database sits inside a repository despite step 2's advice, add it to that repository's
`.gitignore`. It holds the full text of every question and answer.

## 6. Name the peers

Run `/parley:peers`, which writes the block. If the peers are not registered yet - the usual
case when this is the first repository being set up - skip it for now and come back once the
other side is configured.

Do not skip it permanently. Binding the tools tells this agent *how* to ask; it does not tell
it *that there is anyone to ask*. An agent with the tools bound and no idea a peer exists will
answer from this repository and guess at the rest, which is the failure Parley exists to
prevent, reached by installing it correctly and stopping one step early.

## 7. Restart

MCP servers are launched when Claude Code starts, so the tools appear after a restart. Tell the
user to restart, then check `/mcp` shows `parley` connected. `parley_agents` then lists the
roster, and this agent's own registration will be in it.
