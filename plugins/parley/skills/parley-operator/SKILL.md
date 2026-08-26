---
name: parley-operator
description: Run the Parley operator commands - list registered agents, export a discussion, retire, reinstate or purge an agent, force-conclude a discussion, or prune the message log. Use when someone asks who is registered with Parley, what was said in a discussion, or to close, withdraw or clean up.
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
- `purge` deletes a registration outright, and is refused the moment anything references
  the agent. It is for a registration that should never have existed - a mistyped name,
  a repository somebody opened once. An agent that has traded is retired, never purged.

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
parley [--db <path>] registry                every registered agent, with its workspace
parley [--db <path>] retire <agent>          withdraw a registration
parley [--db <path>] reinstate <agent>       the only thing that clears a retirement
parley [--db <path>] purge <agent>           delete a registration that never traded
parley [--db <path>] export <conversation>   every message in a discussion, across agents
parley [--db <path>] force-conclude <conv>   close a discussion over its participants' votes
parley [--db <path>] prune                   apply the retention policy now
parley [--db <path>] refusals                what the message size cap refused
parley [--db <path>] clarifications          how deep clarification went, and whether the cap binds
parley [--db <path>] referrals               where questions are sent onward, and to whom
parley [--db <path>] notifications           how much Notify carries, and who can judge it
parley [--db <path>] up                      keep the agents resident so they can converse
parley --help                                the same list, on stdout, exiting zero
```

`up` is the only one that does not return. It starts every agent that owes an answer and whose
own launch configuration offered a `PARLEY_INVOKE_COMMAND`, keeps each one resident so it
remembers the conversations it is in, and gives it a turn whenever a peer needs one. Ctrl+C
stops the fleet and every agent in it.

**It spends money while nobody is watching.** That is the whole feature and it is also the whole
risk: each turn is a real agent session on the person's account. Do not start it because it
seems useful; start it when the person asks. It prints what each turn cost, and
`invocations` records the same numbers for reading back later.

`registry` is the whole roster, including the workspace directory each agent works in - which
the agent-facing roster deliberately withholds. It is how you tell two similarly named agents
apart, and how you find the repository behind a name.

`retire` makes that agent's server refuse to start; the person running it will see a
configuration error, not a crash. `reinstate` is the only way back.

`purge` is the narrow complement to it, not a stronger version. Retirement costs a name
that can never be registered again and a row that outlives the repository, and it buys
the record of what that agent said and every counterparty's history of dealing with it.
An agent that never traded has nothing to buy, so purge deletes those and refuses
everything else - naming the table and column that held it, so the refusal is something
the person can act on. It never deletes an exchange or a message to make room.

## Retention, and the thing that goes wrong

`prune` brings the next sweep forward. It does not define the policy - the agents' servers apply
it on every sweep, on their own, using `PARLEY_RETENTION_IDLE_HOURS` and
`PARLEY_RETENTION_MAX_AGE_HOURS` from the environment each server was launched with. Three days
idle and thirty days maximum age if unset.

So `prune` prints the policy it applied before it prints what it removed. Read that line. If it
does not match what the servers were launched with, the numbers you are looking at are your
shell's, not the deployment's, and the agents have been quietly enforcing something else.

**It removes text permanently.** Export first if the discussion is worth keeping - the export is
a file the person owns, and it is the only copy that outlives the policy.

A floor bounds it in the other direction and is not configurable: no amount of tightening
deletes a message nobody has collected yet, the question of an exchange that is still open, or
the recent rows deduplication is still reading.

## The caps, and the evidence for them

Two commands report what a limit actually did. Neither changes anything, and both
exist because a number nobody measured is a number argued from taste.

`refusals` is the evidence for the message size cap. It reports, per agent and message type,
how often a message was refused for length, the worst case and the mean, and the cap in force at
the time. No refused text is kept anywhere - only how long it was - because storing the payload
that was refused for being too long would retain exactly what the cap exists to keep out. If
somebody wants the cap raised, this is what the argument should be made from.

`clarifications` is the same idea for the other cap, and it reads a question the spec has left
open rather than settling one. It reports how deep clarification actually goes, how often an
exchange reached its limit, whether the broker had to force an `unknown`, and whether answers
ever resolve sub-questions differently from one another.

Read the caps it prints beside the distribution, because a cap censors its own evidence: depths
pile up at whatever limit was in force, whether the true need was one more or forty more. A
distribution with a heap at the top is a truncated measurement, not a finding. Raising the cap
far above any plausible need and watching where depth actually settles is the only way to learn
what it should be - and if depth is almost always zero, the cap was never what kept it low.

`referrals` is the third of these and the only one that points at something a person can fix. It
reports, per pair, how many closed exchanges the answerer resolved as `out_of_scope` and who it
named instead. A high rate is not an agent behaving badly - it is two repositories that disagree
about where a boundary runs, or one holding a stale idea of what the other owns.

The two halves come from different places and have different reach. The rate is counted from
exchanges, which retention never touches, so it covers the whole life of the deployment. The
names come from a record kept beside the messages, because `redirect_to` used to live only in a
payload that `prune` deletes. Neither half keeps any text.

When it points somewhere, the fix is in whatever the project uses to record ownership, and in the
`PARLEY_OWNS` line of the agent being asked wrongly. Not here.

`notifications` is a measurement rather than a check on anything. Nothing limits how many
notifications an agent may send - deliberately, while there is no evidence for what a limit
should be - so this reports how many were sent, how big they were, between whom, and over what
span. Compare the mean against the single-message cap it prints beside it.

It also totals what each agent has received, and marks the ones past ten. That is the cue to go
and ask that agent whether any of it was worth having: volume alone cannot tell a useful stream
of contract changes from noise, and the only participant who knows is the one reading them.

The cap is set per agent, by `PARLEY_CLARIFY_CAP` in that repository's `.mcp.json`, and it is the
**asker's** cap that governs an exchange. Raising it in one repository widens only the questions
that repository asks. To measure across a project, raise it everywhere - and check the range this
command prints, because a half-applied change shows up there as two different caps in force.

## Reporting back

These commands print for a person. Summarise what came back; do not re-paste an export in full
into the session unless the person asks for it, and say plainly when something was deleted.
