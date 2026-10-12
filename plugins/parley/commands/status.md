---
description: Report whether Parley is working in this repository - the server bound, this agent registered, and the Peers section matching the live roster.
disable-model-invocation: true
---

# Report what Parley is doing in this repository

Setting a repository up has three results that fail independently and look identical from the
inside: the server is bound or it is not, this repository is registered or it is not, and the
standing instructions name the peers that exist or they name some earlier set. A session where
all three are wrong looks exactly like one where all three are right - the agent works, answers
from this repository, and never mentions that anybody else exists.

This command reads those three things and says what it found. **It writes nothing.** Every
finding names the command that fixes it, because a status command that repaired what it
inspected would be `/parley:setup` and `/parley:write-peers` with an extra name, and a report
that changes what it is reporting on cannot be run twice.

**Read the roster with `parley_agents` and nothing else.** The operator command line answers
the same question and must not be used here: its roster carries the workspace path of every
registered agent, which SPEC.md §10.1 withholds from an agent deliberately, and a diagnostic
that reached for it would hand this repository a map of the whole deployment in exchange for a
tidier report. If `parley_agents` cannot answer, that *is* the finding - say so, rather than
going around it.

## 1. Is the server bound?

Call `parley_agents`.

If the tool is not there, Parley is not running in this session, and the useful part of the
report is which of the three reasons it is:

- **Never configured.** No `parley` or `parley-<project>` entry in `.mcp.json` at the repository root. Run
  `/parley:setup`.
- **Configured since this session started.** The entry is there. MCP servers are launched when
  Claude Code starts, so a repository configured mid-session has no tools until a restart, and
  this is the most common answer of the three.
- **Configured and failing to start.** The entry is there and a restart has happened. The server
  exits at launch rather than starting and failing every call, so the cause is in what it was
  given: a `PARLEY_OWNS` with a line break or an unescaped quotation mark, a name that is not a
  role, a `PARLEY_BROKER` pointing at nothing. Say which of these the launch configuration could
  produce; do not guess which one it did.

Stop here if the tool did not answer. The remaining checks need the roster, and inferring them
from files would produce a report that describes the configuration rather than the running
system - which is the one thing this command exists not to do.

## 2. Is this repository registered?

**It is, if step 1 answered. Do not go looking for this repository in the roster.**

`parley_agents` deliberately omits the caller: an agent's roster is who it can address, and
itself is not on that list. So its own absence is the designed behaviour and means nothing —
reporting it as a missing registration is a finding this command will produce in every
repository, for ever, and the remedy it suggests (run setup again) is work nobody needed to do.

What makes the answer sound is that the roster is not a free read. The broker routes it through
the same path as every other agent operation, which refuses a caller with no open stream — so a
roster that came back at all is proof that this agent is attached, and an agent is registered
at the moment it attaches. One fact establishes the other, and the evidence is already in hand
from step 1.

That leaves one thing this command genuinely cannot see: the name this repository registered
under. The machine half is detected rather than configured, so a repository can register as
`docs@some-unexpected-host` and nothing in an agent's own view would show it. Say that it is
not visible from here rather than inferring it from the launch configuration, which records
what was asked for and not what the broker recorded. An operator can see it with a registry
listing; an agent cannot, and should not pretend to.

## 3. Does the Peers section match the roster?

Read the `## Peers` section of this repository's standing instructions - `CLAUDE.md` at the
repository root, or `AGENTS.md` if that is what the project uses - and compare it against the
roster. Four findings, and the first is the one this command is for:

- **Peers in the roster with no line in the block.** This agent does not know they exist. It
  will answer from this repository and guess at the rest, which is the failure Parley exists to
  prevent, reached by installing it correctly. Name them and say to run `/parley:write-peers`.
- **The placeholder is still there.** A block saying the roster has not been read, while the
  roster has peers in it, is the same finding with a timestamp: setup finished and step 8 never
  ran.
- **Lines in the block for agents not in the roster.** Retired, purged, or never restarted after
  being configured. Worth reporting and not worth acting on alone - a peer that is merely
  switched off is still a peer, and §5.5 says a question reaches an agent on a machine that is
  off.
- **No `## Peers` section at all.** Run `/parley:setup` step 6, or `/parley:write-peers` if
  the tools are already bound.

Do not read the peers' `owns` lines out in this report. Whether each line is a fair statement of
that peer's boundary is `/parley:write-peers`' judgement to make and it makes it carefully;
repeating the claims here invites somebody to act on a description that has not been through it.

## 4. What was left behind

Two files survive a purge and a re-run, and both are the class of thing that looks like a fresh
install and is not:

- **`.mcp.json`** may carry variables nothing reads. A `parley` or `parley-<project>` entry naming a store path,
  a sweep cadence, a retention pair or a clarify cap carries settings that belong to the broker
  process. Report them; they are not harmful, but they are evidence that this repository was
  configured against a different Parley.
- **`node.json`**, in the same folder as the projects or wherever `PARLEY_NODE_CONFIG` points, may
  hold an entry for an identity that is not registered. That entry is standing consent for this
  machine to start a repository unattended, and it outlives the registration it was written for
  - a purge clears the broker's store and touches neither the repository nor the machine.

  **This repository's own identity is not evidence of anything here.** The roster omits the caller,
  so its entry looks unregistered from inside and always will; judging it by what step 1 returned
  reports a stale consent that is nothing of the kind. Check the other entries and leave this
  one alone.

## 5. Say it in one place

End with the three things somebody running this across several repositories needs to compare,
in this order: whether the server is bound, whether this repository is attached and therefore
registered, and the count of peers named in the standing instructions versus peers in the
roster. A rollout is checked by reading the same three lines in each repository, and a report
that puts them in a different order each time cannot be read that way.

The second line is a yes, not a name. This command cannot see the name this repository
registered under, and a line that printed one would be printing the launch configuration back —
what was asked for rather than what the broker recorded.

Then the findings, worst first, each naming its command. If there are none, say that - a
repository where all three agree is the outcome, and a status command that only speaks up when
something is wrong leaves somebody unable to tell a clean check from a check that did not run.
