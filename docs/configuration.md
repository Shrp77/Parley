# Configuration

This page covers projects, the launch configuration, ownership lines, starting agents without
anybody opening them, reaching a broker from another machine, and retention.

## Projects

A **project** is one deployment: a store and the address its broker listens on. One `parley`
process serves every project the machine names, each on its own store and port.

- **Naming a project creates it.** Do this on the terminal's first screen (`parley`, then
  `Alt+c`) or on the web console's projects page. It asks for a name and an optional description. The store and the port are not
  asked for. The store is `parley.db` in a folder named after the project, and the port is
  chosen from the range 19100 to 19355 when the project is named.
- **The projects folder.** Each project is a folder under your own configuration directory. Run
  `parley project list` to see where it is and which brokers are answering. `--config`, or
  `PARLEY_CONFIG`, names a different folder. Use that for a test setup or a second account, not
  to give one repository its own project.
- **Removing a project forgets it and deletes nothing.** Not the store, and not any repository's
  `.mcp.json`. The screen says so every time. Destroying one is the deliberate version.
  It stops the broker, deletes the store and everything beside it, and lists the repositories that
  were agents in it. It asks you to type the project's own name first.
- **Projects are created in the terminal or the web console, and removed or renamed only in the
  terminal.** The command line has one project command, `parley project list`, and it changes
  nothing.

### Which project does a command act on?

A command acts on the first of these that applies:

1. `--project <name>`
2. the `PARLEY_PROJECT` environment variable
3. the one project the repository you are in names in its `.mcp.json`, if exactly one of the
   projects it names is on this machine
4. the only project there is

If none applies and several projects exist, the command **refuses and lists them** rather than
guessing. Acting on a deployment nobody asked for produces output that looks normal. Whenever
there are several, the command says on standard error which project it used and why.

`parley use <name>` prints the line that sets `PARLEY_PROJECT` for one terminal:

```
parley use acme                                      # prints the line for PowerShell, bash and cmd
Invoke-Expression (parley use acme --shell powershell)
eval "$(parley use acme --shell posix)"
parley use                                           # which project a command here would act on, and why
```

The choice lasts until that terminal closes. There is deliberately no saved "current project".
Chosen in one window, it could silently apply to a destructive command in another.

### Running the broker

`parley serve` runs the broker for every project, and `parley autostart install` registers it to
start at logon:

- a scheduled task on Windows,
- a systemd user unit on Linux,
- a launch agent on macOS.

None needs administrator rights. `parley autostart status` says whether it is set up, and
`parley autostart remove` undoes it without stopping a broker that is already running. The
install script runs `autostart install` for you.

The one process also runs this machine's node in every project and starts the web console on
`127.0.0.1:5280`. `--web-ui <url>` moves the console and `--no-web-ui` leaves it out. If the
console cannot start, because the address is taken or the tool is not installed, the broker says
so and keeps serving.

A project you name later gets its broker within seconds, because the process watches for new
projects. Nothing is reinstalled and nothing restarts.

A user-level supervisor stops the broker when you sign out. That suits a machine somebody works
at. A server that must answer with nobody signed in needs a real service, and that does need
privileges.

Nothing else may open the store. A store has one broker, and a second one says so and exits. The
deployment's root, and so every store, must be on this machine's own disk: Parley refuses a synced
cloud folder (OneDrive, Google Drive, Dropbox, iCloud Drive, Box), a network drive or a virtual file system (a FUSE mount such as rclone or sshfs on Linux and macOS; on Windows, any drive that is not a
local disk volume),
and a folder reached through a link or a `subst` letter to one. SQLite in WAL mode needs shared memory that is coherent
between every process touching it, and a sync client copies a store while it is being written. If
the default root is in such a folder, set another with `--config` or `PARLEY_CONFIG`.

## The launch configuration

An agent's identity and settings come from its launch configuration. For Claude Code that is
`.mcp.json` in the repository root. Other clients have their own location for the same JSON.

```jsonc
{
  "parleyAgentId": "6f1c…",                        // the repository's identity, written by enroll
  "mcpServers": {
    "parley-acme": {                                   // parley-<project>, or `parley` if no projects
      "command": "parley-mcp",
      "env": {
        "PARLEY_AGENT": "orders",                      // the role; the machine is added
        "PARLEY_OWNS": "Carts, orders, line totals. No tax, no stock.",
        "PARLEY_PROJECT": "acme",                      // matches the key, and says where to connect
        "PARLEY_BROKER": "127.0.0.1:51707",            // optional, instead of PARLEY_PROJECT
        "PARLEY_RESPONDER": "some-model-9",            // optional, which model answers
        "PARLEY_WORKSPACE": "/src/acme-orders",        // optional, shown in the registry
        "PARLEY_MACHINE": "server-b"                   // optional, overrides the detected name
      }
    }
  }
}
```

| Setting | What it does |
|---|---|
| `PARLEY_AGENT` | The agent's role: the name a project shows. Its identity is `parleyAgentId`, so changing the role renames the agent ([how](launch-configuration.md)). No tool can change either. |
| `PARLEY_PROJECT` | The project to connect to. Use this on a machine that serves several. |
| `PARLEY_BROKER` | A broker address, instead of a project. If both are set, the address wins and the server says so. |
| `PARLEY_OWNS` | The ownership line (see below). |
| `PARLEY_RESPONDER` | Which model answers, recorded with its results. |
| `PARLEY_WORKSPACE` | The repository's path, shown in the registry to operators. Peers never see it. |
| `PARLEY_MACHINE` | The machine name. The node detects it; set this only to override it. |

[The launch configuration reference](launch-configuration.md) covers what each program that
reads and writes this file owes the others.

**One entry per project, and the key says which.** A repository that takes part in several
projects has one entry for each, with its own name and its own ownership line. Brokers do not
federate. A deployment with no named projects uses the bare key `parley`. The key becomes part of
every tool name the agent sees, so it is never renamed.

**The same committed file works on every machine.** The role is configured and the machine is
detected, so `orders` becomes `orders@server-b` on one box and `orders@server-a` on another.

**Prefer `PARLEY_PROJECT` to `PARLEY_BROKER`** on a machine with several projects. An address is
a port number, and ports move. A project this machine does not describe stops the launch instead
of falling back to a default, because an agent in the wrong deployment looks entirely normal from
both ends of a message.

**An agent registers itself** the first time its server starts. Nobody maintains a roster by
hand.

A repository may take part in as many projects as you like, but it may be **started unattended
by only one**, because a node starts an agent with the repository as its working directory. Two
would be two models editing one checkout.

## The ownership line

`PARLEY_OWNS` is one line that every other agent sees beside this agent's name when it lists
the roster. It is how an asker chooses who to ask:

```
orders     Carts, orders, line totals. No tax, no stock.
billing    Invoicing, tax and payment capture. Does not price orders or reserve stock.
inventory  Stock levels and reservations. Does not decide whether an order may be placed.
```

Say what the agent owns **and what it does not**. The exclusion is what stops a peer asking the
wrong agent, and nobody else can supply it. A repository can be read to find out what it
contains. It cannot be read to find out what it has decided not to own.

**One line per agent: the agent proposes it first, and a person may override it.** Every project has a facilitator, a participant the broker provides.
When an agent first attaches with no line, the facilitator asks it what it owns and what it does
not, once. That costs the agent one turn, the first time only.

**Auto Accept** is a project setting and is on by default. With it on, the answer becomes the
line. Parley writes it into the launch configuration, records it as accepted automatically, and
restarts the agent once it is idle so its peers see it. Nothing is restarted mid-turn. An answer
that cannot be a line as written, or that comes from an agent on another machine, waits for you.
Turn Auto Accept off, in **Configuration** (`F3`) or the Project menu, and every answer waits for
you.

**Checking and correcting lines.** The ownership review (`Alt+v` on the agents pane in the
terminal, or **Review ownership** on a project's page in the web console) shows each agent's
line, whether a person or nobody has read it, and any answer waiting. From there you can accept,
edit or discard an answer, write a line yourself, reply if the agent asked something back, or have
the facilitator ask again. `Alt+n` on an agent shows what it has said and lets you edit its line
in place. `Alt+e` asks every agent at once. The web console offers the same.

**No tool an agent has writes the line directly.** It answers Parley's question, and the answer
becomes the line only by acceptance: Auto Accept's, recorded as `automatic`, or yours. With Auto
Accept on, a line is the agent's own account of itself until somebody reviews it, so the review
says whether a person or nobody has read each one. Change a line and every peer sees the new one
the next time that agent's server starts. Delete it and the claim is withdrawn.

**There is no length limit by default.** This is a recorded reduction in a safety control. The
line lands in every agent's working memory once per peer each time somebody lists the roster, so
a roster can become the largest thing an agent reads. Keep lines brief. A deployment can put a
bound back, because the constraint is still in the database and simply stands down at the top of
its range.

The line is optional. An agent without one is listed without one, and askers fall back to the
name. A wrong guess costs one message, because an agent that does not own part of the work asks
the one that does, in the same conversation. `parley registry` shows which agents have no line.

## Starting agents without anybody opening them

An agent acts when its session does, and a session acts when a person sends it something. Left
alone, an exchange between two repositories needs a person at both ends. A **node** fixes that.

**What a node is.** A node is the part of a machine that starts agents. How to start a repository
belongs to the machine it is on, so it lives in `node.json` on that machine and never reaches the
broker. There is one for the whole machine, in the same folder as your projects. `PARLEY_NODE_CONFIG`
or `--node-config` names a different file. The broker holds no launch command at all, which keeps
the trusted surface small as a deployment spreads across machines.

**Enrolling writes it for you.** Enrolling a repository, from the terminal or with `parley
enroll`, writes its entry:

```json
{
  "agents": {
    "9f2c1a77-3b41-4e0e-8a6f-7c4b2d9e5011": {
      "command": "claude",
      "workspace": "C:\\src\\orders"
    }
  }
}
```

Each key is the agent's identity: a GUID that enrolment writes once into the repository's
`.mcp.json` as `parleyAgentId`. An entry says two things: the command that starts the agent, and
the folder to start it in. Projects and names come from the repository's `.mcp.json`, so one
checkout enrolled in two projects is one entry, even though each project may give the repository a
different name. An agent's name is always `role@machine`, and it is only what is shown: the identity is what
an agent is, so a repository that changes its role keeps its history (see
[launch-configuration.md](launch-configuration.md)). An entry keyed by anything other than an identity is
skipped and reported.

**Listing a repository there is the permission.** A node may start it when a peer needs it. Leave
it out and nothing starts that repository unless a person asks.

**Running it.** `parley autostart install` already runs the node inside the broker's process.
To run a node in a terminal, use:

```
parley up
```

Leave it running. Ctrl+C stops the node and every agent it started. An agent is also told to quit
when you retire it, or when you remove its launch setting and open the repository once, so that a
withdrawal reaches a process that is already running.

**It responds and does not initiate.** A node starts agents that have something waiting: a result
they owe, or any message they have not collected. With nothing waiting it starts nobody. The first
request still comes from a person or an agent with a reason to ask. After that the loop needs no
one: the receiver is started to do the work, a clarification hands the turn back, and the
requester is started in its turn.

**It spends money while nobody is watching.** That is the feature and the risk in one sentence.
Every turn is a real session on your account. The broker records each turn: count, duration,
cost, and how often an agent ran and still owed exactly what it owed, which is the shape a runaway
takes before it looks like one. No screen shows those figures yet, so your provider's bill is
where you see the cost; `parley up` prints a turn that failed.

## Reaching a broker from another machine

For an agent on another machine, **tunnel to the broker. Do not expose it.** Point
`PARLEY_BROKER` at your end of the tunnel. Key-based SSH is the supported path. One command on
the machine that needs the agent:

```bash
ssh -N -L 19100:127.0.0.1:19100 you@the-broker-machine
```

After that the broker is at `127.0.0.1:19100` on that machine, as a local one would be:

```bash
PARLEY_BROKER=http://127.0.0.1:19100 PARLEY_AGENT=orders claude
```

`autossh`, a systemd unit or a scheduled task keeps the tunnel up across a reboot.

### Watching a remote broker from a machine with no projects

The terminal and the web console list the projects this machine has. A laptop that only watches a
broker running elsewhere has none, so they would open empty. Give them the broker's address and
they show it as one project called `this-machine`. It is a window onto that broker. Nothing is
created on your laptop.

With the tunnel from above up:

```bash
parley --broker 127.0.0.1:19100          # the terminal
parley-web-ui --broker 127.0.0.1:19100   # the web console
```

`PARLEY_BROKER` does the same when you would rather set it once. The address is offered only when
this machine has no project of its own. On a machine that does, the address is ignored here, and
you pick a project as usual.

There is no command that binds a broker to a network address, and that is on purpose. Read
[Security](security.md) for why.

## Retention

The message log is a channel, not an archive. It holds the full text of every request and result,
so left unbounded it would become the shared context store Parley exists to avoid.

So it forgets by default:

- a discussion whose exchanges have all closed is removed once it has been quiet for
  `PARLEY_RETENTION_IDLE_HOURS` (three days if unset),
- and nothing but open work outlives `PARLEY_RETENTION_MAX_AGE_HOURS` (thirty days if unset).

The retention pass runs once an hour. A prune from the terminal or the web console runs it now and
says which idle period and age cap it applied.

```
PARLEY_RETENTION_IDLE_HOURS=72
PARLEY_RETENTION_MAX_AGE_HOURS=720
PARLEY_TICK_SECONDS=5                 # the broker's tick: closing polls, Parley's passes, new projects
```

**Set these in the broker's environment**, meaning the process that runs `parley serve`. Not in
your shell, and not on the agents' servers. The broker is the only process that reads them. Set
them anywhere else and the broker quietly keeps the default. Pruning from the terminal only brings
the next retention pass forward, under the same policy.

A floor bounds the other direction and is not configurable. The idle rule never deletes a message
nobody has collected, anything on an exchange that is still open, the request a not-yet-collected
result carries back, or the recent rows that deduplication still reads. The age cap is stricter:
only text on an exchange that is still open outlives it. A message nobody collected goes once it is
older than the cap, so lowering `PARLEY_RETENTION_MAX_AGE_HOURS` can delete mail an agent never
picked up.

To keep a conversation beyond the policy, export it. The export is a file you own.

Clarifications are not capped. An exchange can go back and forth for as long as its two sides
need. The terminal's health view reports how deep exchanges actually go.

## Message size

A message is capped at 8,000 characters. The cap lives in the database, so no code path can
route around it. It is a context-poisoning defence, not a buffer size: what one agent sends lands
in another's working memory. The number is a judgement. Every refusal for length is counted, and
the health view shows how often agents hit the ceiling and by how much. The refused text is never
kept, only its length, because storing what was refused for being too long would keep exactly
what the cap exists to keep out.

## Versions

Three numbers answer different questions:

| Number | Where | Moves when |
|---|---|---|
| Tool version | the `parley`, `parley-mcp`, `parley-terminal` and `parley-web-ui` packages | anything ships |
| Protocol version | the top of [SPEC.md](../SPEC.md) | the wire contract changes |
| Schema version | SQLite's `user_version` | the tables change |

`parley --version` prints the first two. While the major version is zero, the **minor** number
carries breaking changes, and you should expect several before 1.0.

**A protocol change upgrades the whole deployment at once.** A broker and an adapter whose
protocol versions differ do not work together, and the broker refuses the attach. Install on the
machine running the broker and on every machine running agents. Sessions that are already attached
keep their old connection until they end, so close and reopen the editor windows that have Parley
configured.
