---
name: parley-operator
description: Run the Parley operator commands - list registered agents, export a discussion, retire or reinstate an agent, force-conclude a discussion, or prune the message log. Use when someone asks who is registered with Parley, what was said in a discussion, or to close, withdraw or clean up.
when_to_use: The user asks who is registered with Parley, what two agents said to each other, to remove or restore an agent, to close a discussion its participants have not closed, or to apply the retention policy now.
---

# The Parley operator commands

`parley` is a command-line tool, installed by a person and **never given to an agent**. It holds
the operations that act on other agents' work: reading a whole discussion, withdrawing a
registration, closing a discussion over its participants' votes, deleting message text.

They are not tools and cannot become tools by accident. `parley-mcp`, the server an MCP client
launches, does not have them compiled into it - a test asserts that by reflecting over the real
binary. Reaching them means running this program.

## The boundary

Run these when the person asks for them. Do not run them to get around the protocol.

- `force-conclude` closes a discussion the participants have not agreed to close. It is not a
  way past a gate an agent is waiting on. If a question is blocked, the answer is to answer it.
- `export` prints every message in a discussion, across agents. That is exactly the context
  Parley keeps apart, so it is for a person reading back a decision - not a way to hand one
  agent another's reasoning. Do not paste an export into an agent's session as a shortcut for
  asking it.
- `retire` stops an agent's server from starting at all. It is a withdrawal, not a mute.

None of this is a rule the tool enforces; it is the reason the tool is separate from the tools.
No agent has authority over another, and these commands are how a **person** intervenes.

## The database

Every command needs one:

```
parley --db <path> <command>
```

`--db` defaults to `PARLEY_DB`. That is the same variable the agents' servers are launched with,
which is the point - and also the trap. **Your shell is not the environment the MCP client
launched the servers with.** If `PARLEY_DB` is unset here, or set to something else, read the
`parley` entry in the project's `.mcp.json` and use that path.

## The commands

```
parley [--db <path>] agents                  every registered agent, with its workspace
parley [--db <path>] retire <agent>          withdraw a registration
parley [--db <path>] reinstate <agent>       the only thing that clears a retirement
parley [--db <path>] export <conversation>   every message in a discussion, across agents
parley [--db <path>] force-conclude <conv>   close a discussion over its participants' votes
parley [--db <path>] prune                   apply the retention policy now
parley [--db <path>] refusals                what the message size cap refused
parley --help                                the same list, on stdout, exiting zero
```

`agents` is the whole registry, including the workspace directory each agent works in - which
the agent-facing roster deliberately withholds. It is how you tell two similarly named agents
apart, and how you find the repository behind a name.

`retire` makes that agent's server refuse to start; the person running it will see a
configuration error, not a crash. `reinstate` is the only way back.

## Retention, and the thing that goes wrong

`prune` brings the next sweep forward. It does not define the policy - the agents' servers apply
it on every sweep, on their own, using `PARLEY_RETENTION_IDLE_HOURS` and
`PARLEY_RETENTION_MAX_AGE_HOURS` from the environment each server was launched with. Three days
idle and thirty days maximum age if unset.

So `prune` prints the policy it applied before it prints what it removed. Read that line. If it
does not match what the servers were launched with, the numbers you are looking at are your
shell's, not the deployment's, and the agents have been quietly enforcing something else.

`refusals` is the evidence for the message size cap. It reports, per agent and message type,
how often a message was refused for length, the worst case and the mean, and the cap in force at
the time. No refused text is kept anywhere - only how long it was - because storing the payload
that was refused for being too long would retain exactly what the cap exists to keep out. If
somebody wants the cap raised, this is what the argument should be made from.

**It removes text permanently.** Export first if the discussion is worth keeping - the export is
a file the person owns, and it is the only copy that outlives the policy.

A floor bounds it in the other direction and is not configurable: no amount of tightening
deletes a message nobody has collected yet, the question of an exchange that is still open, or
the recent rows deduplication is still reading.

## Reporting back

These commands print for a person. Summarise what came back; do not re-paste an export in full
into the session unless the person asks for it, and say plainly when something was deleted.
