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

## 2. Ask for the two things only the user knows

**The agent name for this repository.** Lowercase letters, digits and hyphens, starting with a
letter, up to 32 characters. It should name the domain this repository owns - `orders`,
`billing`, `inventory` - not the directory, the framework, or the model. Peers address it by
this name, it is recorded in the registry the first time the server starts, and it is fixed by
the launch configuration rather than chosen at run time.

Two repositories must never be given the same name. Identity is the one thing Parley makes
unforgeable, and it does that by taking the name from the launch environment and offering no
tool that mentions it - which is worth nothing if two servers claim the same name, because
either one may then collect the other's mail.

**The database path.** One SQLite file, shared by every repository that should be able to reach
every other. It therefore has to live outside all of them - a path under one repository makes
that repository's clone a prerequisite for everybody else's agent, and a path inside each
repository gives every agent its own private database in which no peer exists. Something like
`~/.parley/<project>/parley.db` works. The file does not need to exist; the server creates and
migrates it on first launch.

If the operator's tool is installed and the database already exists, show the user who is
already registered before they choose a name:

```
parley --db <path> agents
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
        "PARLEY_WORKSPACE": "${PARLEY_WORKSPACE:-<absolute path to this repository>}"
      }
    }
  }
}
```

Substitute the real values. Three notes on the shape:

- The `${VAR:-default}` form is environment-variable expansion, which `.mcp.json` supports.
  `.mcp.json` is usually committed and both paths are machine-specific, so the default is what
  this machine uses and a teammate overrides it by setting the variable rather than by editing
  a shared file.
- `PARLEY_AGENT` is written plainly, with no fallback. It is the same on every machine, because
  it names what this repository is rather than where it lives.
- `PARLEY_WORKSPACE` is optional and only an operator ever sees it; it is how `parley agents`
  tells two similarly named agents apart. Write the absolute path, not `.` - the server
  deliberately does not default it, because a plausible and wrong workspace is worse than an
  honest blank.

These are optional and can be left out: `PARLEY_RESPONDER` (what to record as having produced a
message, `unknown-model` if unset), `PARLEY_TICK_SECONDS` (how often the sweep runs, 60 if
unset), `PARLEY_RETENTION_IDLE_HOURS` and `PARLEY_RETENTION_MAX_AGE_HOURS` (how long the message
log keeps what it carried, 72 and 720 if unset). Set the retention pair on **every** agent's
server or none: the servers are what apply the policy, so setting it in one place and not
another has agents quietly enforcing different numbers on one file.

## 4. Create the database's directory

Create the parent directory of the database path if it does not exist. Do not create the file.

If the database sits inside a repository despite step 2's advice, add it to that repository's
`.gitignore`. It holds the full text of every question and answer.

## 5. Name the peers

Run `/parley:peers`, which writes the block. If the peers are not registered yet - the usual
case when this is the first repository being set up - skip it for now and come back once the
other side is configured.

Do not skip it permanently. Binding the tools tells this agent *how* to ask; it does not tell
it *that there is anyone to ask*. An agent with the tools bound and no idea a peer exists will
answer from this repository and guess at the rest, which is the failure Parley exists to
prevent, reached by installing it correctly and stopping one step early.

## 6. Restart

MCP servers are launched when Claude Code starts, so the tools appear after a restart. Tell the
user to restart, then check `/mcp` shows `parley` connected. `parley_agents` then lists the
roster, and this agent's own registration will be in it.
