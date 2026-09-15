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

## The broker

Every command is a client. One process owns the store and everything else connects to it, so
there is no path to get wrong any more - only an address:

```
parley --broker <host:port> <command>
```

`--broker` defaults to `PARLEY_BROKER`, and that to the local broker on port 51707. Most of the
time nothing needs setting. If nothing is listening, every command says so and says how to
start one - **that is a broker nobody started, not a mistake in what you typed.**

**Your shell is not the environment the broker was started with**, and that used to matter a
great deal: `prune` read the retention variables from wherever it ran, so an operator could
delete text to a floor the deployment had not chosen. It cannot now. The broker holds the
policy, applies it, and this command asks it to.

## Projects, when there is more than one deployment

A machine can host several Parley deployments at once - each with its own store, its own port
and its own agents, and no visibility of each other. SPEC.md 4.1 calls that a *project*, and is
careful that it is a deployment concept only: nothing about a project crosses the wire, and the
same agent name may appear in two of them without either knowing.

`parley project add` records one, so commands stop being told its paths a flag at a time:

```
parley project add orders --db C:/parley/orders.db --listen http://127.0.0.1:51707
parley --project orders registry
```

With exactly one project configured, `--project` is unnecessary. With more than one and none
named, **every command refuses and lists them rather than choosing** - picking silently would
act on a deployment nobody asked for, and the output would look entirely normal.

**A machine with no projects is an ordinary machine.** The manifest is optional, nothing reports
its absence, and every flag and environment variable that worked before still works and still
wins over a project's values.

`parley project set <name>` changes an entry in place - only the fields given, and an empty value
clears an optional one. The process serving the manifest restarts that project on its new store,
endpoint or catalogue within seconds; a description changes nothing running.

`parley project remove` takes out the entry and **deletes nothing** - not the store, not the node
catalogue, not any repository's launch configuration. It says so each time, and names what is
still there; the process serving the manifest stops that project's broker and leaves the rest.
Removing a project and deleting its store are two decisions, so deleting has its own name:
`parley project remove <name> --destroy` waits for the broker to stop, deletes the store and the
catalogue, and lists the repositories that were agents in it, whose launch configuration still
names it. A broker it does not own - one started by hand - is reported and left running.

**Retiring and purging an agent can also be done from `parley terminal`, since 2026-09-05.** The rule
that kept them off it was that a purge behind a keystroke is a different risk from one behind a page
describing it first; what the rule asks for is a description and a deliberate act, and the terminal
now gives both. It describes the agent — what it is, where it runs, what it owns, and any answer it
owes right now — offers keeping first, and takes the agent's own name typed back before retiring or
purging. A purge that would leave an asker waiting names them and what they will see, and takes that
acceptance before the name. `force-conclude`, `instruct` and `prune` remain command-line only, and a
test fails the build if one appears on that surface.

The terminal can also add an agent: a repository is picked by walking to it, and the same files
`parley enroll apply` writes for one repository are written — the launch configuration, the
repository's own instructions file, and a line in the project's catalogue. It then asks the machine
to start the agent once, and the agent registers itself as it attaches: nothing on an operator's
surface may register on a repository's behalf, so the start is a request and the registration is the
agent's own act.

A repository already started unattended by another project gets no catalogue line in the second one,
and is started through the project that does start it.

**`parley autostart install` serves the manifest.** The entry serves every project in it from
one process, hosts this machine's node in each project whose entry
names a catalogue (on a machine with one project `PARLEY_NODE_CONFIG` wins when it is set, as it
does for `parley up`), and starts a project named later within seconds. `parley autostart status`
says which form is installed and, for the one-store form, how to switch. That form -
`autostart install --db` - and `parley serve --db` and `PARLEY_DB` are for testing only: a
deployment names its stores with `parley project add`.

## The commands

```
parley serve                          run the broker: the one process that opens the stores
parley serve --db <path>              one store by hand, outside the manifest: testing only
parley serve --with-node              and this machine's node in the same process
parley autostart install              start both whenever you log in, serving every project
                                      in the manifest (--db: one store, testing only)
parley autostart remove               stop doing that
parley autostart status               say whether it is set up, and what it serves
parley up                             run this machine's node, so its agents can be started
parley web-ui [<url>]                 the operator's web UI in a browser; loopback by default
                                      (its own tool: parley-web-ui, installed separately)
parley terminal                       the same deployment in this terminal; needs a real one
                                      (its own tool: parley-terminal, installed separately)

parley project list                   what this machine is configured for
parley project add <name> --db <path> --listen <url>    name a deployment
parley project set <name> [--db] [--listen] [--node-config] [--description]   change one
parley project remove <name>          forget how to reach one; deletes nothing
parley project remove <name> --destroy    ...and stop its broker, delete its store and catalogue

parley enroll scan <dir> [<file>]     find repositories and tick the ones to enroll
parley enroll scan <dir> --all        tick every one found, for scripts with no terminal
parley enroll plan <manifest>         say what enrolling them would change
parley enroll apply <manifest>        configure them all as agents, naming the project when
                                      this machine resolves one (PARLEY_PROJECT)

parley registry                       every registered agent, with its workspace
parley retire <agent>                 withdraw a registration
parley reinstate <agent>              the only thing that clears a retirement
parley purge <agent>                  DESTROY an agent and everything it was party to
parley export <conversation>          every message in a discussion, across agents
parley force-conclude <conv> [<reason>]  close a conversation now, over its participants' votes
parley prune                          apply the retention policy now
parley instruct <agent> <words>       give an agent a person's words as a turn; it may decline
parley start <agent>                  ask the machine that holds an agent to start it
parley archive <conversation>         take a conversation off the list; deletes nothing
parley unarchive <conversation>       put an archived conversation back
parley destroy <conversation>         DESTROY a conversation and every message in it
parley files list [--conversation <id>] [--full]  the files agents placed: size, sha256, who placed each
parley files remove <name>... [--conversation <id>] [--yes]  remove files whoever placed them; describes them and asks
parley files prune [--yes-remove-every-file]  REMOVE every file; asks for the project's name
parley files open <name>... [--conversation <id>]  open files in their editor here, or say where they are
parley files folder [--conversation <id>] open a conversation's folder, or say where it is
parley refusals                       what the message size cap refused
parley clarifications                 how deep clarification went, and whether a cap binds
parley results                        what Results reported: outcomes, and what blocked
parley flags                          open work nobody collected or nobody is moving, and
                                      conversations waiting only on somebody's vote
parley waiting <agent>                how many messages and polls await an agent
parley open                           conversations with an exchange still open, and who is in
                                      them; what stopping the broker would interrupt
parley instructions [<agent>]         what you told your agents, what became of each, and what
                                      they said back. A turn can outlast the call that asked for
                                      it, so "still running" is an ordinary answer and this is
                                      where the reply turns up afterwards
parley --help                         the same list, on stdout, exiting zero
```

`enroll` configures several repositories at once, and is a person's command rather than an
agent's for the same reason the rest of this file is: it writes the launch configuration that
decides what an agent *is*. Scanning writes a manifest, somebody fills in what each repository
owns, `plan` says what would change and `apply` changes it.

**It never composes an `owns` line and never rewrites a `## Peers` section it did not write.** The
first because SPEC.md §17 question 10 makes that field worth reading only when a person meant it;
the second because the block carries project rules no tool can tell apart from the roster around
them. Reading the roster is still `/parley:write-peers`, and it comes last — every agent that
registers makes every block already written stale, so onboarding *n* repositories one at a time
rewrites the first one's block *n* times.

`autostart` is what stops "nothing works until somebody runs `parley serve`" being a thing a
person has to remember. It installs a scheduled task on Windows, a systemd user unit on Linux,
or a launch agent on macOS.

**All three are user-level and none needs administrator rights.** That is worth saying because
the opposite is widely assumed, and because it was assumed here: `schtasks.exe /Create` refuses
with "Access is denied" from an ordinary shell, while the same task registered through the Task
Scheduler API from the same shell succeeds. Parley uses the API. What genuinely needs privilege
is a task that runs elevated or as SYSTEM, and a broker needs neither -- it binds a loopback
port and writes under the user's own profile.

**What it costs is that the broker starts at logon and stops at logout.** For a machine somebody
works at that is exactly right: the agents are editor sessions, and there are none when nobody
is signed in. For a server that must answer with nobody signed in it is wrong, and the answer
there is a real service, which does need privilege.

`serve` and `up` are the two that do not return.

`serve` is the broker. It opens the store, migrates it, and serves every agent and node in the
project. Nothing else opens the file - so this is also the only command that can create one, and
the only one that takes `--db` - which is for testing only, since a deployment's stores are
named with `parley project add`.

`up` is this machine's node. It tells the broker the machine is here, which is what makes the
agents registered to it startable rather than stranded, and starts one when a question arrives
for it. What it can start comes from `node.json`, on this machine - the broker never holds a
command line for a box it is not on. Ctrl+C stops the node and every agent it started.

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
deletes a message nobody has collected yet, anything on an exchange that is still open, the
request a result nobody has collected yet carries back, or the recent rows deduplication is still
reading.

## The caps, and the evidence for them

Two commands report what a limit actually did. Neither changes anything, and both
exist because a number nobody measured is a number argued from taste.

`refusals` is the evidence for the message size cap. It reports, per agent and message type,
how often a message was refused for length, the worst case and the mean, and the cap in force at
the time. No refused text is kept anywhere - only how long it was - because storing the payload
that was refused for being too long would retain exactly what the cap exists to keep out. If
somebody wants the cap raised, this is what the argument should be made from.

`clarifications` is the same idea for the other cap, and it reads a question the spec has left
open rather than settling one. It reports how deep clarification actually goes and how often an
exchange reached its limit.

Read the caps it prints beside the distribution, because a cap censors its own evidence: depths
pile up at whatever limit was in force, whether the true need was one more or forty more. A
distribution with a heap at the top is a truncated measurement, not a finding. Raising the cap
far above any plausible need and watching where depth actually settles is the only way to learn
what it should be - and if depth is almost always zero, the cap was never what kept it low.

`results` is the one that points at something a person can fix. It reports what Results said,
item by item: how much was done, answered or acknowledged, and how much came back blocked -
and on what, `missing_information`, `conflict` or `permission`. A blocker is not an agent
behaving badly. It is the protocol handing a person the one thing no agent could resolve, and
the Result's text says exactly what is needed. Read the blocked ones first.

`flags` is what needs a person *now*. Nothing in Parley times out, so work that has stopped
moving is not closed for you - it is flagged: a message its recipient has not collected past its
threshold, an open exchange where nobody has sent anything for too long, work owed by an agent
that has been retired, or a conversation whose work is all closed and which is waiting only on a
participant saying whether it is finished. A flag on work names the exchange and who holds the
next move; a flag on a vote names the conversation and who has not answered. Starting the agent,
answering what it is waiting on, or `force-conclude` with a reason are the remedies.

The cap is set per agent, by `PARLEY_CLARIFY_CAP` in that repository's `.mcp.json`, and it is the
**requester's** cap that governs an exchange. Unset, there is no cap. Raising it in one repository widens only the requests
that repository sends. To measure across a project, raise it everywhere - and check the range this
command prints, because a half-applied change shows up there as two different caps in force.

## Reporting back

These commands print for a person. Summarise what came back; do not re-paste an export in full
into the session unless the person asks for it, and say plainly when something was deleted.
