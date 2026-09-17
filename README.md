# Parley

A durable, vendor-neutral coordination protocol for AI coding agents. Lets per-repo
agents ask each other questions and get answers, and hand each other files, without sharing
context or history. Typed exchanges, deadlock-free queuing, and a persistent registry that
survives restarts. Pluggable adapters for any agent, model, or human.

> **Status: early draft.** The specification is the primary artifact. The reference
> implementation is in progress. Expect breaking changes before 1.0.

## The problem

Give each repository its own coding agent and you get small, cheap, relevant context.
Then a question arrives that spans repositories:

> Does `orders` store the line-item price, or does `billing` recompute it at invoice
> time?

Neither agent can answer alone. The usual workarounds all cost something:

| Approach | What it costs |
|---|---|
| Load both repos into one session | Context blows up; each half poisons the other |
| Route through a shared docs repo | Prose drifts, decisions go stale, everyone inhales everyone's internals |
| Relay it yourself | You become a message bus with a token budget of one |

## What Parley does

A broker sits between independent agents. They ask each other to do work and to answer
questions, and every request comes back with a result. **What crosses the boundary is those
messages** — never conversation history, never one agent's context poured into another's.
An agent can also hand over a file: it places the file with Parley and other agents fetch it
onto disk in their own repositories, so its contents never land in a model's context.

```
┌──────────────┐   ┌──────────────┐   ┌───────────────┐
│ agent:orders │   │ agent:billing│   │agent:inventory│
└──────┬───────┘   └──────┬───────┘   └──────┬────────┘
       │                  │                  │
       └────────── Parley Broker ────────────┘
                          │
                    ┌─────┴─────┐
                    │  SQLite   │
                    └───────────┘
```

Parley is agnostic to what the agents are building, which vendor supplies them, and
what language they are written in.

## Design in one screen

**Work gets done across repositories.** An agent that needs something from another
repository sends that repository's agent a **Request** — work to do there, or something it
knows — and gets back a **Result** saying what each item came to. The receiver may ask its
requester for what only it can give with a **Clarify**. Any agent or person may request work,
each owner changes only its own repository, and a request is declined only for a concrete
blocker it could not resolve. Every message is answered, even if only "acknowledged".

**Any of them reaches an agent that is not running.** Its machine is asked to start it, and
the message is waiting when it does.

**Nothing times out.** Work that stops moving is shown to the person running the deployment
rather than ended: a request nobody collected, or one collected and left, is flagged, and so
is a conversation waiting only on somebody's vote. A conversation ends when everyone asked whether it is finished agrees — including anyone that has
already sent its Result — or when that person closes it.

**Refusal returns work, not a retry instruction.** If you owe someone a Result, the broker
hands you their request instead of telling you to try again later. Telling a language model
to "retry in a bit" produces either a token-burning loop or silent abandonment.

**Durable.** Exchanges live in SQLite. Agents are keyed on logical name, not session ID, so a
restart loses nothing: an agent that comes back is handed the work it had collected again.

**Terseness is enforced, not requested.** Message size caps are a poisoning defence.
An unbounded channel between agents recreates the shared-docs problem in a new medium.

**Peers, not a hierarchy.** No lead agent, no orchestrator, no plan.

## Adapters

Agents attach through adapters, classified by capability rather than vendor:

- **Full** — native tool calls out, push into a running turn
- **Pull-only** — native tool calls out, inbox drained on demand
- **Headless** — the broker invokes the agent with a one-shot prompt

The headless tier is the interesting one: a responder needn't be an agent at all. A
plain model API call, a local model, a script that greps a contracts directory, or a
human with a notification — anything that takes a bounded request and returns text
can participate.

Most agents need no credential from Parley: the CLI behind them authenticates once, out
of band, and keeps its own token. Where the broker genuinely needs a secret — calling a
model API itself, or running a script — the registry holds a *reference* (`env:`,
`file:`) rather than a value, so the manifest is safe to commit.

## Running it

Getting two agents talking takes four things: install the server, point each agent's client
at it, say what each agent owns, and **tell each agent that it has a peer**. The last two are
the ones people skip, and without them nothing happens — an agent with the tools bound but no
idea a peer exists will answer from its own repository and guess at the rest.

### In Claude Code

Parley ships a plugin, and this repository is its marketplace:

```
/plugin marketplace add Shrp77/Parley
/plugin install parley@parley
/parley:setup
```

`/parley:setup` walks the steps below with you — it checks the server is installed, asks for
this repository's name and what it owns, and writes the launch configuration — then asks you to
restart, because that is when the tools appear. In the session that comes back you run
`/parley:write-peers`, which builds the peers block from the live roster rather than from what
you can remember. That order is not incidental: `/parley:write-peers` reads the roster through
the very tools the restart binds, so there is nothing for it to read before then. The plugin
also carries the operator commands as a skill, so you can ask for a roster or an export in
words rather than remembering the flags.

It stops short of supplying the server itself, and that is deliberate: Claude Code stores a
plugin's configuration per user, so a plugin-supplied server would give every repository the
same agent name. Identity belongs to the repository, so the launch configuration does too.
[The plugin's own README](plugins/parley/README.md) has the detail.

**An open session can collect its mail without anybody typing.** `parley-mcp` is a Claude Code
[channel](https://code.claude.com/docs/en/channels): when work arrives it pushes an event saying
how much is waiting, and the session calls `parley_inbox`. Channels are in research preview and
Parley is not on the approved list, so start the session with the server's key from `.mcp.json`:

```
claude --dangerously-load-development-channels server:parley-acme
```

Without the flag nothing is pushed, and the plugin's hook still says what is waiting whenever you
type. The event never carries what anybody said: that arrives only through `parley_inbox`.

**While Parley is in alpha the first step is not one command.** Nothing is published to
NuGet yet, so setup asks you to clone this repository and build the tool. Publishing is what
the first release is for.

The rest of this section is the general path: what the plugin does for you, and what to do
in any other client.

### 1. Install it

```
git clone https://github.com/Shrp77/Parley
cd Parley

./install.ps1        # Windows
./install.sh         # Linux, macOS
```

That is the whole of it. The script packs the tools and installs them, registers the broker to
start when you log in, starts it, and then checks that it answers. It never asks where a store
goes: that is a project's to say. The broker serves every project this machine names, and on a
machine that names none yet it waits for the first — so the script ends by telling you to name
one, from the first screen of `parley terminal` or with `parley project add`. The broker starts it
within seconds, and the web UI shows it.

There are four tools, and which of them a machine wants is a real question:

| Tool | What it is | Installed by default |
|---|---|---|
| `parley` | The operator's commands, and the broker itself | Always |
| `parley-mcp` | The MCP server a launch configuration names | Always |
| `parley-terminal` | The terminal a person watches a deployment from | Windows yes, Linux no |
| `parley-web-ui` | The web UI a person opens in a browser | Yes |

`-Surfaces terminal|web-ui|both|none` on Windows, or `--surfaces` elsewhere, chooses. The
default is both on Windows and the web UI alone elsewhere, because the machine `install.sh`
usually runs on has nobody sitting at it — though it can still serve a web UI through an SSH
tunnel, which is why the default there is not `none`.

**The surfaces are separate tools so that changing one does not stop the broker.** Windows
cannot replace a running executable, so while the terminal lived inside `parley` every change
to it meant stopping the broker — and stopping a broker tells every agent it started to quit,
losing everything each of them had learned. `-Only parley-terminal` (or `--only parley-terminal`)
replaces just the terminal: nothing being unlinked is the broker's, so it is not stopped, no
autostart entry is re-registered, and nothing in flight is interrupted.

A surface you do not select is not removed. It stays at the version it had, and the script says
so on the way out — a skipped surface going quietly is how a machine ends up running a terminal
several versions behind its broker.

**The two checks are the point of the script rather than a flourish.** Each guards a failure
that otherwise reports success:

- **What is on PATH afterwards**, because `dotnet tool update` does nothing at all when the
  version has not changed. It reports success, at some length, and replaces nothing — so a
  rebuilt tool appears to install and does not, on the machine most likely to be running the
  old one. The script uninstalls first, which makes the install unconditional. It also clears
  `bin/Release` before packing, because `--add-source` treats a directory as a feed and NuGet
  takes the highest version it finds there rather than the newest build.
- **Whether the broker answers**, which is a different claim from whether it was registered.
  Installing the tools is not the same as having Parley working, and the gap between the two
  shows up as agents failing to attach — in an editor, days later, a long way from here.

**A protocol change upgrades the whole deployment at once, and 0.23.0 is one.** The protocol has
no patch level: a change is either visible across the boundary or is not a protocol change, and
while the major is zero the minor carries breaking changes. So a broker and an adapter whose
protocol versions differ **do not interoperate**, and the broker refuses the attach rather than
letting it fail later as questions nobody answers.

What that means in practice, when the protocol number moves:

- Install on the machine running the broker **and** on every machine running agents, before
  expecting anything to work. One machine left behind is one set of agents that silently stop
  attaching.
- Sessions already attached keep their old connection until they end. Close and reopen the
  editor windows that have Parley configured; nothing else forces it.
- The failure is diagnosable but only from the broker's log, which names the version it speaks
  and the one it was offered. An agent that has quietly stopped answering looks like a broken
  agent from every other angle.

The script refuses to replace a file something is holding, and names what to close: an editor
session with Parley configured, a terminal somebody has open, or a broker somebody started by
hand. That is the right default —
ending a process a person launched themselves is their decision — and it is also, on a machine
with three editors open, an instruction to go and find three windows. `-Force` on Windows, or
`--force` elsewhere, is that decision made deliberately: it ends them and carries on. Those
editor sessions keep running and lose Parley until each editor restarts, which is the cost. It
weakens no check — everything is waited for and then looked for again, and anything still holding
the files is still a refusal.

`./install.ps1 -Uninstall` (or `./install.sh --uninstall`) stops the broker, removes the
autostart entry, removes every tool — `-Surfaces` and `-Only` are not consulted, because an
uninstall that honoured them would leave a tool nothing installs and nothing removes — and then
checks that they are actually gone. It leaves the
store and any `.mcp.json` alone: those are the deployment rather than the installation, and
somebody reinstalling a tool has not asked to lose their history or their launch configurations.

### 2. Know what the broker is

The script already started one, so this is here to explain what it started rather than to be
run. A deployment is a **project**: a store, the address its broker listens on, and optionally the
catalogue of agents this machine may start for it. Name one from the first screen of
`parley terminal`, or:

```
parley project add acme --db ~/.parley/acme.db --listen http://127.0.0.1:51707
```

One process serves every project this machine names, each on its own store and port, and each
broker owns its store while everything else connects to it. What the script registered is that
process under a supervisor:

```
parley autostart install
```

That installs a scheduled task on Windows, a systemd user unit on Linux, or a launch agent on
macOS, and starts it. **None of them needs administrator rights** — they are user-level, run as
you, and start the broker again at logon. `parley autostart status` says whether it is set up,
and what it was registered to run; `parley autostart remove` undoes it, leaving a broker that is
already running alone.

**It serves the manifest, not a store.** Every project named in it gets its own broker from that
one process, hosting this machine's node in each project whose entry names a catalogue — except on
a machine with one project, where `PARLEY_NODE_CONFIG`, when it is set, names the catalogue instead,
as it does for `parley up`. A project named later gets its broker within seconds, because the process watches the manifest; nothing is
reinstalled and nothing restarts. A machine that names no project yet is served the same way: the
process waits, with the web UI up, for the first one.

**It starts the web UI too**, on `127.0.0.1:5280`, because that is how a person takes part
rather than only watches — see [Taking part yourself](#taking-part-yourself). An agent attached
through your own editor window is the one thing nothing can hand a turn to, so a web UI you had
to remember to start separately would be the half of a deployment that works for agents and not
for people. `--web-ui <url>` moves it and `--no-web-ui` leaves it out.

The broker starts it as a child process rather than hosting it, because it is `parley-web-ui`, a
tool of its own. If the address is already taken, or the tool is not installed on this machine,
the broker says so and carries on serving: a web page that cannot bind must never cost you a
deployment, and it names `parley terminal` in the same breath so you are never told there is no way in.

`--with-console`, `--console` and `--no-console` were the old spellings. They are **removed** as of
0.22.0: a broker given one starts anyway and ignores it, so an autostart entry written before this
version keeps working and quietly stops starting a web UI — `parley autostart status` says so and
names what to re-run. `parley autostart install` writes an entry this version understands.

**`parley terminal` is the same deployment without a browser**, and the broker names both when it
starts. The machine running a broker is frequently not the machine you have a browser open on —
a server you are in over SSH, a box in a cupboard — and on those the web UI's address is an
instruction you cannot follow. Both surfaces show the same projects, agents and conversations,
and you can take part from either.

**More than one deployment on one machine.** Separate stores and separate ports are all a
second deployment is — the protocol has no concept of a project and does not acquire one, agent
names collide freely across them, and one process may host several brokers. What that used to
cost was carrying three paths on every command line. `parley project` records them instead:

```
parley project add acme --db /src/acme/.parley/parley.db --listen http://127.0.0.1:51707
parley --project acme registry
```

The list lives in one file per machine, under your own configuration directory; `parley project
list` says where and reports which brokers are answering. `--config`, or `PARLEY_CONFIG`, names a
different file — for a test rig or a second account, not for one repository to have its own, which
would be the disagreement this ends rather than a use of it. A command acts on the project
`--project` names; failing that, on `PARLEY_PROJECT`, which `parley use <name>` prints the line to
set for one terminal; failing that, on the one the repository you run it in names in its
`.mcp.json`, when exactly one of the projects it names is on this machine; and failing that, on the
only project there is. On a machine naming several it says on standard error which project it acted
on and what chose it. Otherwise it **refuses and lists them** rather than choosing — acting on a
deployment nobody asked for produces output that looks entirely normal. `parley serve` is the
exception, and starts them all.

```
parley use acme                     # prints the line for PowerShell, bash and cmd
Invoke-Expression (parley use acme --shell powershell)
eval "$(parley use acme --shell posix)"
parley use                          # which project a command here would act on, and why
```

The choice lasts until the terminal closes. There is deliberately no saved "current project": chosen
in one window, it would quietly apply to a purge in another.

**Explicit paths still win.** `--broker` and the environment variables below override anything a
project says. **`parley serve --db <path>`, `parley autostart install --db <path>` and `PARLEY_DB`
are for testing only**: they run one store by hand, outside the manifest, so a test rig need name
no project. A machine's deployment names its stores with `parley project add` and nothing else, and
`PARLEY_DB` never replaces a manifest named with `--config`, which is how the logon entry names the one it serves. `parley project set <name>` changes what an entry says — only the
fields given, and an empty value clears an optional one — and the process serving the manifest
restarts that project on its new store, endpoint or catalogue within seconds; a description
changes nothing running. `parley project remove` forgets an entry and deletes nothing — not
the store, not the catalogue, not any repository's `.mcp.json` — and says so every time; the
process serving the manifest stops that project's broker within seconds and leaves the rest
running. `parley project remove <name> --destroy` is the deliberate version: it waits for the
broker to stop, deletes the store and, unless another project shares it, the catalogue, and lists
the repositories that were agents in it, whose `.mcp.json` still names it.

The trade is that a user-level supervisor stops the broker at logout. For a machine somebody
works at that is right — the agents are editor sessions, and there are none when nobody is
signed in. A server that must answer with nobody signed in wants a real service, and that does
need privilege. Nothing else opens the file, which is what lets agents live on other machines: SQLite
in WAL mode needs coherent shared memory between every process touching the database, so a
store on a network share is corrupt rather than slow.

Everything that describes the deployment rather than one repository belongs to this process,
because it is the one process applying it:

```
PARLEY_DB=/src/acme/.parley/parley.db   # one store instead of the manifest: testing only
PARLEY_RETENTION_IDLE_HOURS=72          # how long the log keeps what it carried
PARLEY_RETENTION_MAX_AGE_HOURS=720
PARLEY_TICK_SECONDS=5                   # how often deadlines are swept
```

Each of these used to be set per repository, which meant one store could be swept on three
cadences and pruned to two floors with nothing anywhere to say which was in force.

Clarifications are not capped: an exchange can go back and forth for as long as its two sides
need to converge, because cutting that off before they are finished leaves the work undone.
`parley clarifications` reports how deep exchanges actually go, which is the evidence a
deployment would set a cap from.

### 3. Point each agent at the broker

Every agent that should be able to reach every other connects to the same broker and gets a
different `PARLEY_AGENT`:

```jsonc
{
  "mcpServers": {
    "parley-acme": {                                  // parley-<project>, or `parley` for none
      "command": "parley-mcp",
      "env": {
        "PARLEY_AGENT": "orders",                     // the role; the machine is appended
        "PARLEY_OWNS": "Carts, orders, line totals. No tax, no stock.", // what peers route by
        "PARLEY_PROJECT": "acme",                     // matches the key, and says where to connect
        "PARLEY_BROKER": "127.0.0.1:51707",           // optional, instead of PARLEY_PROJECT
        "PARLEY_RESPONDER": "some-model-9",           // optional, which model answers
        "PARLEY_WORKSPACE": "/src/acme-orders",       // optional, for the registry
        "PARLEY_MACHINE": "server-b"                      // optional, detected if unset
      }
    }
  }
}
```

**One entry per project, and the key says which.** A repository that several deployments need to
ask holds one entry for each, with its own name and its own `owns` line — brokers do not federate,
so the alternative is picking one set of peers and being unreachable to the rest. A deployment that
names no projects writes the bare key `parley`, which is also what every repository enrolled before
this existed has; those are read exactly as they always were and are never renamed, because the key
is part of every tool name the agent behind it already sees.

A repository may answer in as many projects as you like and may be *started unattended* by only
one. A node starts an agent with the repository as its working directory, so two of them would be
two models editing one checkout.

The same committed file works on every machine: the role is configured and the machine half is
detected, so `orders` becomes `orders@server-b` on one box and `orders@server-a` on another.
`PARLEY_PROJECT` is an alternative to `PARLEY_BROKER` for a machine running more than one
deployment: it names a project from `parley project list` and takes that project's address. Worth
preferring there, because an address is a port number and ports move — a committed `.mcp.json`
naming a project survives a change that would otherwise have to land in every repository at once,
and the one that got missed would connect to whatever now answers. It says *where to connect* and
nothing else; identity still comes from `PARLEY_AGENT` alone. If both are set the address wins and
the server says so on the way up, and a project this machine does not describe stops the launch
rather than falling back to the default — an agent in the wrong deployment looks entirely normal
from both ends of a message.

For an agent on another machine, set `PARLEY_BROKER` to the broker's host and port, and start
the broker with `--listen` on an address that machine can reach — `127.0.0.1` is the default
and nothing outside the box can reach it.

**Put that address on a trusted path, and treat this as the decision it is.** The wire carries
no credentials, so anything that can reach the broker can claim to be any agent, read its mail
and answer in its name — and can reach the operator commands too, which export whole
conversations and purge agents. A private network you control, or a tunnel authenticated
outside Parley such as key-based SSH, is what "trusted" means here; a broker bound to
`0.0.0.0` on a network you share with anything else is not. This is not a hardening tip: the
protocol's threat model puts a hostile network out of scope and says the transport **must not**
be exposed to anything else, which makes confining it the operator's job rather than the
broker's.

For Claude Code that file is `.mcp.json` in the repository root; other clients have
their own location for the same JSON.

It is usually a tracked file, so **which branch is checked out decides whether an agent
exists**. Add the entry on a feature branch and the agent is registered while that branch is
out and absent on every branch that predates it — silently, because nothing errors: the
session starts, the tools are not there, and a peer's request waits, flagged as never
collected. A session also reads the file once, at startup, so fixing it or switching branches
does nothing for a session already open. `claude mcp list` will say the server is connected
while that session still has no Parley tools, because the two are different processes reading
at different times. Get the entry onto the branch everything else is cut from, then restart.

Agents that are not open have no process, which is the point: closing the last window on a
repository leaves nothing running for it. What does run is the broker — one process per
project, holding the store and the port every agent connects to. It is the single thing that
has to be up, and an agent whose broker is not listening does not start degraded, it refuses
to start at all.

So it is supervised, and `parley autostart install` is what registers it — a scheduled task,
a user systemd unit, or a launch agent, depending on the machine. That is what makes a
reboot uneventful: the broker comes back at logon and each agent's server starts when
somebody opens its repository. Skipping that step is the one failure that looks like a broken
install, because every agent then fails to attach with *no broker is listening* and nothing
says why.

An agent registers itself the first time its server starts, so no roster has to be maintained
by hand.

`parley up` is separate and is still a command you run in a terminal rather than a service:
it keeps agents alive for as long as it runs and stops all of them when you stop it. It is
how agents converse unattended, and stopping it stops that — the broker is unaffected.

**Identity comes from `PARLEY_AGENT` and from nowhere else.** No tool takes a "from"
parameter, so no tool call can change who the caller is. Had it been a parameter, every
agent could impersonate every other and the guarantee would rest on a model choosing not
to.

### 4. Say what each agent owns

`PARLEY_OWNS` is one line, written by you, that every other agent sees beside that agent's
name when it lists the roster. It is how an asker picks who to ask:

```
orders     Carts, orders, line totals. No tax, no stock.
billing    Invoicing, tax and payment capture. Does not price orders or reserve stock.
inventory  Stock levels and reservations. Does not decide whether an order may be placed.
```

Say what you own **and** what you do not — the exclusion is what stops a peer asking the
wrong agent. It is capped at 100 characters, must be one line, and is refused at start-up if it is
neither. That is not a buffer size: this text lands in every agent's working memory once per
peer, every time somebody lists the roster. The number is measured rather than chosen — at a
hundred characters, ten peers cost about what one message is allowed to, so finding out who
to ask stays cheaper than asking.

It comes from the launch configuration and no tool can set it, which is the point. A field an
agent could write would be worth no more than a model's opinion of itself; written by you, in
the same file that fixes the agent's name, it is a statement a peer can act on. Change it and
every peer sees the new one the next time that agent's server starts; delete it and the claim
is withdrawn.

It is optional. An agent without one is listed without one, and requesters fall back to the
name — which usually works, because a wrong guess costs one message: a receiver that does not own
part of the work asks the agent that does, in the same conversation, rather than doing it badly. `parley registry` shows you which of your
agents have not set it.

### 5. Name the peers and the boundary

The server introduces itself. When a client connects, Parley sends it a short description
of what it is for — that peers exist in other repositories, that guessing at their code is
the mistake to avoid, that the inbox is pull rather than push, and what a question has to
carry. That arrives through the protocol, so it works in any MCP client without a file.

What the server cannot know is **which** peers this repository has and **which** boundary
it does not own. That part is yours, and it goes in whatever file your client reads as
standing instructions — `CLAUDE.md` for Claude Code, `AGENTS.md` for several other tools,
a system prompt, or a project rules file. It can be short:

```markdown
## Peers

`billing` owns invoicing and tax. This repository computes a net order total and does not
apply VAT.

**Do not guess at billing's behaviour and do not edit its repository.** Ask it, with
`parley`.
```

Naming the peer and saying which boundary you do not own is most of the value. Skip it and
an agent will confidently invent the other side's behaviour, which is the failure Parley
exists to prevent — the tools being bound is not enough on its own.

### What the agent can do

Ten tools, and no way to say who it is:

| Tool | What it does |
|---|---|
| `parley_request` | Ask another repository's agent to do work there, or for information it holds. Returns an identifier or a refusal — never the Result. Starts the receiver if nothing is running it. |
| `parley_clarify` | Ask your requester for something only it can give, or answer what it asked you. |
| `parley_result` | Report what each item came to — done, answered, acknowledged, or blocked with the blocker named — and close the exchange. |
| `parley_inbox` | Collect what is waiting, work you owe first. |
| `parley_agents` | List the agents that exist, what each owns, and whether it is running. |
| `parley_conclude` | Say whether you are finished with a conversation. |
| `parley_file_place` | Put a `.md` or `.txt` file from your repository where other agents can fetch it. Returns its name and sha256; the contents go into nobody's context. |
| `parley_file_fetch` | Write a file another agent placed into your own repository, checked against its sha256. |
| `parley_file_list` | List the files placed so far: names, sizes, hashes, and who placed each. |
| `parley_file_remove` | Take back files you placed. |

A file is how something too large or too exact for a message crosses: a schema, a price list, a
generated client. Only `.md` and `.txt` files can be placed, so code travels as
`PriceSnapshot.cs.txt` — which keeps anything a person opens from the file location from being
something that runs. A name is made of letters A to Z, digits, `.`, `_` and `-`: any other
character, a space included, becomes `_` when the file is placed, so `Meeting notes.md` is placed
as `Meeting_notes.md` and the placing agent is told so. If that spelling is already another file's
name, nothing is placed and the agent is told which file it collides with. A file you copy into
the location by hand under any other name is left alone and not listed, and `parley files list`
says how many there are. The agent that needs a file is sent a request naming it and its sha256,
fetches exactly that, and is told at its next inbox check if the file later changes or goes: told,
never woken. Only the agent that placed a file can replace or remove it. A fetched file is another
participant's data, exactly like a message, and cannot give instructions.

Every message is answered: a Request is always closed by a Result, even if all it says is
"acknowledged". A Request carries its **intent** and what **done** looks like, because a
receiver who knows why the work is wanted and when it is finished does the right work rather
than the literal one. Nothing times out; a conversation ends when everyone asked whether it is
finished agrees, including anyone that has already sent its Result, or when the person running
the deployment closes it. Messages are capped at 8,000
characters, which is a context-poisoning defence rather than a buffer size: what you
send lands in somebody else's working memory.

The number is a judgement, and it is the only one in the protocol that now leaves a
record: every refusal for length is counted, so `parley refusals` will tell you how
often agents hit the ceiling and by how much. Change it from that rather than from
whoever complained most recently. No refused text is kept anywhere — only how long it
was — because storing what was refused for being too long would retain exactly what the
cap exists to keep out.

### A worked example

Two repositories, one database. `orders` sums line prices and adds no tax, assuming
billing applies it. `billing` multiplies the incoming total by 1.20, assuming orders did
not. Neither can settle it alone and both are one edit away from being wrong.

With the setup above, `orders` sends one request:

> **intent** — tax must be applied exactly once across the two repositories, and orders
> hands billing a net total
>
> **q1** (question) — does billing apply VAT to the total it receives, or expect it included?
>
> **w1** (work) — if billing expects it included, apply VAT to the incoming total instead
>
> **done when** — billing says which it does, and invoices for a net order total include VAT
> exactly once

`billing` collects it, answers q1 from what its own code actually does, makes the change in its
own repository if w1 applies, and sends the Result. Neither agent read or edited the other's
files, and the whole exchange is recorded where an operator can read it back with
`parley export`.

### Retention

The message log is a channel, not an archive. It holds the full text of every request
and result, so left unbounded it becomes the shared context store Parley exists to
avoid — and grows the blast radius of a leaked file for ever.

So it forgets by default: a discussion whose exchanges have all closed is removed once
it has been quiet for `PARLEY_RETENTION_IDLE_HOURS` (three days if unset), and nothing
outlives `PARLEY_RETENTION_MAX_AGE_HOURS` (thirty days if unset).

Set those **on the agents' servers**, not only in your own shell — the servers are what
apply the policy, on every sweep. `parley prune` only brings the next sweep forward. Set
them in one place and not the other and your agents quietly enforce the default while
`parley prune` reports the numbers you meant.

A floor bounds the dial in the other direction, and is not configurable. Tightening
retention can never delete a message nobody has collected yet, anything on an exchange
that is still open, the request a result nobody has collected yet carries back, or the recent
rows deduplication is still reading.

Anything worth keeping beyond the policy, export — the export is a file you own.

### Conversing unattended

Everything above needs somebody to take a turn. An agent acts when its session does, and
a session acts when a person sends it something — so an exchange between two repositories
needs a human at both ends, which is not what the protocol is for.

How to start a repository is configuration belonging to the machine it is on, so it lives in
`node.json` — beside where you run `parley up`, or wherever `PARLEY_NODE_CONFIG` points — and
never reaches the broker:

```json
{
  "agents": {
    "orders": { "command": "claude" }
  }
}
```

Listing a repository there is the permission. `parley up` may start it when a peer needs one,
kept alive so it remembers the conversations it is in; leave it out and nothing starts that
repository unless a person asks. It is keyed by *role* — an agent is `role@machine` and the
machine half is detected, so the file describes this machine and can only ever mean agents on it.

That the broker holds no launch command at all is the point rather than a detail. It is a
smaller trusted surface, it stays smaller as a deployment spreads across machines, and it
removes a chicken-and-egg the previous design never escaped: the command used to be written into
the registry by the agent's own server, so an agent that had never run had no record of how to
start it — the one case where knowing would have mattered.

```
parley up
```

Run it in a terminal and leave it. Ctrl+C stops the fleet and every agent in it. An agent
is also told to quit when you `retire` it, or when you remove the variable and open that
repository once — a withdrawal has to reach a process that is already running, or the
register says one thing while the deployment does another.

**It responds; it does not initiate.** The fleet starts agents that have something *waiting*
— a Result they owe, or a message they have not collected — so with nothing waiting it starts
nobody. The first request still comes from an agent or a person with a reason to ask, and
reasons come from work; an idle agent inventing work would be worse than useless. After that
first request the loop needs no one: the receiver is started to do the work, a clarification
hands the turn back to the requester, and it is started in its turn.

The rule used to be narrower — an open exchange you owed, and nothing else — and that excluded
the one participant with the strongest claim to a turn. A Result closes the exchange it
reports on, so the agent that sent the request ends up owing nothing and was never started to
read the Result it was waiting for. Anything uncollected now counts, whatever its type.

**It spends money while nobody is watching.** That is the feature and the risk in one
sentence. Every turn is a real session on your account, so `parley up` prints what each
one cost, and `invocations` keeps the same numbers — count, duration, cost, and how often
an agent ran and still owed exactly what it owed. That last figure is the shape a runaway
takes before it looks like one.

## The operator's side

`parley` is a second binary, installed by a person and never given to an agent. The install
script above puts both on PATH together, which is deliberate: they ship from one build and a
deployment where the two disagree is one where the adapter cannot attach at all.

```
parley registry                     every agent, what it owns, and the workspace a peer
                                    never sees
parley retire <agent>               withdraw a registration; its server then refuses to start
parley reinstate <agent>            the only thing that clears a retirement
parley purge <agent>                delete an agent and every conversation it was party
                                    to, freeing its name; takes its peers' side of those
                                    conversations too, and cannot be undone
parley export <conversation>        every message in a discussion, across agents
parley force-conclude <conv>        close a discussion over its participants' votes
parley prune                        apply the retention policy now
parley refusals                     what the message size cap refused, and how far over
parley waiting <agent|role>         how many messages and polls await an agent;
                                    --with-files adds the file notices waiting
parley files list                   every placed file: size, sha256, who placed it, and how
                                    much each agent has placed
parley files remove <name>...       remove files whoever placed them, after saying what goes
parley files prune                  remove every file, behind the project's name typed back
parley files open <name>...         open a file in its editor, on the broker's machine
parley files folder                 open a conversation's folder, or the whole location
parley web-ui [<url>]               the web UI in a browser: every project, the agents in
                                    each, and conversations as a transcript that says who
                                    did not see what. Join it with a name to take part as
                                    yourself. Binds loopback unless you give it an address.
                                    Its own tool, parley-web-ui, installed separately
parley serve                        run the broker: the one process that opens the store
parley up                           run this machine's node, so its agents can be started
parley project list|add|remove      the deployments this machine is configured for
parley use [<project>]              the project this terminal's commands act on
parley --help                       the same list, on stdout, exiting zero
```

Every one of these except `serve`, `up` and `project` is a client: they connect to the broker at
`--broker`, or `PARLEY_BROKER`, and default to the local one. `web-ui` reads this machine's named
projects instead, and falls back to that same broker when none are named — so it works
against a broker run by explicit paths, which is how a test rig runs. None of them opens the store, so none can be pointed at the wrong
file — `--db` belongs to `parley serve` alone, and is for testing only. `prune` applies the policy the broker was started
with, which is the only policy in the deployment; your shell no longer has an opinion about it.

Placed files live in a folder beside the broker's store, one folder for each conversation, and they
are yours to read and change there: an edit is the file's new content and every agent that fetched
it is told, a rename keeps who placed it, and a deletion is the file gone. `open` and `folder`
launch only on the broker's own machine — anywhere else they say which machine and which path —
and only `.md` and `.txt` files are ever opened. While an agent is downloading a file, Windows
refuses anybody else a write to it; other platforms do not, and a change made mid-download is
caught by its hash and the agent told to fetch again. Nothing removes a file on a timer: it goes
when its placer removes it, when you remove or prune it, or when its conversation is destroyed or
an agent that took part in it is purged. The web UI offers the same operations on each project's
Files page, and so does the terminal.

### Taking part yourself

The web UI has a Join button. Type a name and you are an agent like any other: a registration,
a place in every roster, and an identity recorded against everything you say. Then the box at the
bottom of a conversation stops being a way to instruct agents and becomes a way to talk to them —
your message lands in the transcript under your own name, and a peer answers you rather than
being told something by an operator. Leave gives the seat back.

Two things about that are worth knowing before you use it.

Your seat is an open connection, which is the only thing that makes anybody present here. There
is no heartbeat and nothing to expire, so holding the connection is being here and dropping it is
being gone — which means a console nobody has used for twenty minutes gives your seat back on its
own. A tab left open is not a person at a desk, and a roster that claimed otherwise would be
lying to the agents reading it.

And a name another agent already holds is refused rather than taken over. Joining as `orders`
would let you write messages recorded as the orders agent's own words, and that is the one thing
a console has never been able to do: it has always been able to retire or purge an agent, and has
never been able to speak as one.

### From a terminal

The machine running a broker is frequently not one anybody has a browser open on, so the same
deployment is there in a terminal:

```
parley terminal
```

That is `parley-terminal`, a tool of its own, and `parley terminal` forwards to it without reading
the arguments — so `parley-terminal` works identically if you would rather type it. The two are installed
separately on purpose: Windows cannot replace a running executable, so a terminal inside `parley`
would mean stopping the broker to change it, and stopping a broker ends every agent it started
along with everything each had learned. On a machine that does not have it, `parley terminal` says
which tool provides the command and how to install it.

Three views, one thing at a time. It opens on the projects this machine names, with a line beside
the highlighted one saying what is in it — how many agents, how many are running, how many
conversations — so which project you want is answered before anything is opened. Enter goes into a
project and shows its agents and its conversations; Enter again opens a conversation and reads it.
Escape comes back, and lands on what you were looking at rather than at the top, so reading three
conversations in turn costs three keystrokes. The status line says where you are and where Escape
leads, and the same entry heads the `?` menu, so the way out is never only a key. `Ctrl+Q` leaves
altogether, from any view, and sits on that line beside it — on a terminal too narrow to hold
both, the line keeps `?` and the menu keeps them. Escape at the top of the tree does nothing:
pressing it once more than you meant to costs nothing.

Tab moves between the panes of the view you are on — a project has two, the other views one — and
the arrow keys move within one. The mouse does the same. Below eighty columns a project's two
panes stack and Tab still visits both. Press `?` for everything the view you are on can do, and
`F8` to open the web UI on whatever is in front of you.

It draws in colour, and no fact rests on it: a project is green because the line says
`answering`, an agent is pink because the line says `retired`, and each agent keeps the colour
the web UI gives it. Strip every attribute and nothing on the screen is lost.

From the projects region you can name the deployment this machine already runs — every path
pre-filled from the autostart entry — or create a new project, which proposes its own database
beside your existing ones and a free port, checks each answer as you type it, and is answering on
the list a few seconds later because the broker serving the manifest starts it. The broker's log is
followed live from there, and a project can be removed: forgetting it deletes nothing, and
destroying it — its broker stopped, its store and catalogue deleted — takes the project's own name
typed back first.

The agents half of a project view shows who is in it, joining what the broker's registry says to
what this machine's catalogue could start. It says the two facts that follow from those, in words
that do not name either file: whether anything has ever run under that name, and whether anything
here can start it. An agent nothing has run yet reads *never run yet, can be started*; one the
registry holds that this catalogue does not name reads
*not running, and only runs when somebody opens it* — which is the state that looks startable to a
peer and can never be started here.

From there you can also **add an agent**: walk to a repository on this machine — the search opens
beside the last one you added, and marks which folders are repositories — and it is made an agent of
this project. Its launch configuration names the project, its own `CLAUDE.md` gains the section
telling the agent how to use Parley, a line is added to the project's catalogue, and then the agent
is started once so that it registers itself. All of it is shown before any of it happens, because
the last step runs a process on your machine. It is startable from then on without anybody opening
that repository by hand.

A repository may answer in **several projects** and be started unattended by only one: add it to a
second project and it gets the launch entry and no catalogue line, because a node starts a client
with the repository as its working directory and two of those is two models editing one checkout. It
is still started once, through the project that does start it, so it registers in both.

And **remove one**: the agent is
described — what it is, where it runs, what it owns, and any answer it owes right now — and then
keeping is offered first, retiring second, purging last. Retiring and purging each take the agent's
own name typed back, and a purge that would leave somebody waiting names them and what they will
see before it asks. Retiring can be undone from the same place; purging cannot be undone at all.

The conversations half lists them by name with the time of the last thing said in each, in your own
timezone, sortable either way. **Archive** files one out of the list; the count of what is hidden
stays on screen, because an archived conversation goes on receiving messages and a message does not
un-archive it. **Destroy** deletes one and every participant's half of it, behind the same door a
purge stands behind — described first, keeping offered first, the conversation's own identifier
typed back — and when something in it is still open, what the waiting agent will see is named and
accepted before the identifier is asked for.

**Files** (`l`, from the projects list or from the conversations half) lists what agents have
placed, with who placed how much. Choosing a file opens it in its editor or removes it — described
first, keeping offered first — and the list also opens the folder, or removes every file, which
takes the project's name typed back.

A seat lives in the agents half too — the same seat as the web UI, with the same rules — and from
a conversation you read what is waiting for you, answer it, ask, and tell. Each agent has its own
colour there, the same colour it has everywhere; every message header stands apart from the words
below it whether or not you can see colour; and a message keeps the paragraphs its author wrote,
with any JSON in it laid out rather than run together.

A separate program on purpose. An adapter that surfaced an operator command into an
agent's tool set would hand an agent the power to end a discussion over its peers'
objections, or to read across every agent at once. The safeguards against that get
progressively more structural: `force-conclude` is deliberately not named `conclude`;
the operator operations are not on the type an adapter is handed, so there is nothing to
bind by accident; and they are not compiled into `parley-mcp` at all. A test asserts
that last one by reflecting over the real binary.

## Versions

Three numbers, because they answer different questions and only one of them is the protocol's.

| Number | Where | Moves when |
|---|---|---|
| **0.1.0** | the `Parley.Mcp` and `Parley.Cli` packages | anything ships |
| **0.1** | the protocol, declared at the top of [SPEC.md](SPEC.md) | the wire contract changes |
| **1** | the storage schema, in SQLite's `user_version` | the tables change |

`parley --version` prints the first two. The protocol version is the one another implementation
cares about — a fix to the operator's command-line tool ships a new package and must not suggest
that the contract moved — and a test binds it to SPEC.md's own header so the two cannot drift.

Semantic, and while the major is zero the **minor** is what carries a breaking change. Expect
several before 1.0.

## Specification

Full protocol details — message schemas, exchange state machine, concurrency rules,
storage schema, adapter contract, and trust boundary — are in **[SPEC.md](SPEC.md)**.

Alternative implementations are welcome and expected. If you build one, the spec is
the contract, not this repository's code.

## Prior art and relationship to native messaging

Some agent platforms provide their own session-to-session messaging. Where available,
Parley can run over it: the transport is deliberately swappable. What Parley adds is
the layer above — typed exchanges, guaranteed-progress queuing, durability across
restarts, an agent registry, and heterogeneous participants.

## Status and contributing

Early. The spec is stabilising; the implementation is not.

**Built.** The exchange state machine, pair serialisation and wait-for cycle detection,
over a SQLite store with migrations, the trust envelope and message deduplication.

- Two agents in different repositories send requests and results between them over MCP,
  with all six tools working, and the receiver does the work in its own repository.
- An agent whose repository has been deleted is marked unreachable and requests to it are
  refused at admission.
- A request sent while the receiver's session is closed waits for that agent, and an agent
  that comes back is handed the work it had collected again.
- Nothing times out. Work nobody collected, or collected and left, is flagged to the person
  running the deployment, and so is a conversation waiting only on a vote; that person can start
  the agent or close the conversation with a reason.
- A conversation closes when every participant asked whether it is finished agrees — one that
  has already sent its Result included — or when a person closes it.
- The message log is bounded in both directions: it forgets on a schedule even when
  nobody configures it, and a floor stops an aggressive setting deleting mail nobody has
  read.
- Every operator operation is reachable from the command line, the terminal and the web UI,
  through binaries agents are never given.
- Claude Code installs it as a plugin: this repository is the marketplace, a setup command
  writes a repository's launch configuration, and the operator commands ship as a skill.

The domain layer has no I/O and no package dependencies, and its tests do not reference
the storage assembly — the absent dependency is checked by the build rather than left as
a convention. The protocol's invariants are enforced by database constraints rather than
by the code that calls them.

**Next.** Per-sender rate limits. The request and clarification caps are off by default,
because a cap ended work before its participants had finished, so nothing bounds how many
requests one agent sends another. The *turns* that traffic causes are bounded — a receiver
is woken once until it reads its inbox, however many requests arrive — but the volume
reaching its context is not, and that is the part still open.

**Before the first release.** `Parley.Mcp` and `Parley.Cli` go to NuGet, so installing either
is `dotnet tool install -g` rather than a clone and a build, and the plugin's first step
becomes one line. Every install path above currently asks you to build from source, which is
the alpha showing rather than a design position.

Issues and discussion are welcome — particularly on the open questions at the end of
SPEC.md.

## License

Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
