# Parley

A durable, vendor-neutral coordination protocol for AI coding agents. Lets per-repo
agents ask each other questions and get answers without sharing context, files, or
history. Typed exchanges, deadlock-free queuing, and a persistent registry that
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

A broker sits between independent agents. They ask each other questions and receive
answers, and **only the question and answer text ever crosses the boundary** — never
conversation history, never files, never each other's context.

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

**Four message types.** Question, Clarify, Answer, Notify. Agents don't make small
talk, so there is nothing else to model.

**Deadlock-free by construction.** Traffic between any two agents is serialised on an
unordered pair key, and new questions are rejected if they would close a cycle in the
wait-for graph. Pairwise locking alone doesn't catch a three-way cycle; cycle
detection on insert does.

**Refusal returns work, not a retry instruction.** If you owe someone an answer, the
broker hands you their question instead of telling you to try again later. Telling a
language model to "retry in a bit" produces either a token-burning loop or silent
abandonment.

**Durable.** Exchanges live in SQLite. Agents are keyed on logical name, not session
ID, so a reboot orphans exchanges rather than losing them. Whether they're then replayed
is your call, not ours: killing the process is how you stop two agents burning a quota,
and a forced update at the weekend is how you lose one you wanted to keep. The broker
cannot tell those apart, so resumption is configured rather than assumed.

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
human with a notification — anything that takes a bounded question and returns text
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

That is the whole of it. The script packs both tools and installs them, registers the broker to
start when you log in, starts it, and then checks that it answers. It asks where the store
should go, defaulting to `.parley` in your home directory; `-Store <path>` on Windows or
`--store <path>` elsewhere answers that in advance, for an unattended run.

`parley` is the operator's tool and the broker itself; `parley-mcp` is the MCP server a launch
configuration names.

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

`./install.ps1 -Uninstall` (or `./install.sh --uninstall`) stops the broker, removes the
autostart entry, removes both tools, and then checks that they are actually gone. It leaves the
store and any `.mcp.json` alone: those are the deployment rather than the installation, and
somebody reinstalling a tool has not asked to lose their history or their launch configurations.

### 2. Know what the broker is

The script already started one, so this is here to explain what it started rather than to be
run. One process owns the store and everything else connects to it:

```
parley serve --db /src/acme/.parley/parley.db
```

It listens on `127.0.0.1:51707` unless `--listen` says otherwise, and runs until it is stopped.
What the script registered is that same command under a supervisor:

```
parley autostart install --db /src/acme/.parley/parley.db
```

That installs a scheduled task on Windows, a systemd user unit on Linux, or a launch agent on
macOS, and starts it. **None of them needs administrator rights** — they are user-level, run as
you, and start the broker again at logon. `parley autostart status` says whether it is set up
and `parley autostart remove` undoes it, leaving a broker that is already running alone.

**More than one deployment on one machine.** Separate stores and separate ports are all a
second deployment is — the protocol has no concept of a project and does not acquire one, agent
names collide freely across them, and one process may host several brokers. What that used to
cost was carrying three paths on every command line. `parley project` records them instead:

```
parley project add acme --db /src/acme/.parley/parley.db --listen http://127.0.0.1:51707
parley --project acme registry
```

The list lives in one file per machine, under your own configuration directory;
`parley project list` says where and reports which brokers are answering. `--config`, or
`PARLEY_CONFIG`, names a different file — for a test rig or a second account, not for one
repository to have its own, which would be the disagreement this ends rather than a use of it. With one project
configured nothing needs naming. With several and none named, every command **refuses and lists
them** rather than choosing — acting on a deployment nobody asked for produces output that looks
entirely normal. `parley serve` is the exception, and starts them all.

**None of this is required.** A machine with no projects behaves exactly as it always has, its
absence is never mentioned, and an explicit `--db`, `--broker` or environment variable still wins
over anything a project says. `parley project remove` forgets an entry and deletes nothing — not
the store, not the catalogue, not any repository's `.mcp.json` — and says so every time.

The trade is that a user-level supervisor stops the broker at logout. For a machine somebody
works at that is right — the agents are editor sessions, and there are none when nobody is
signed in. A server that must answer with nobody signed in wants a real service, and that does
need privilege. Nothing else opens the file, which is what lets agents live on other machines: SQLite
in WAL mode needs coherent shared memory between every process touching the database, so a
store on a network share is corrupt rather than slow.

Everything that describes the deployment rather than one repository belongs to this process,
because it is the one process applying it:

```
PARLEY_DB=/src/acme/.parley/parley.db   # instead of --db
PARLEY_RETENTION_IDLE_HOURS=72          # how long the log keeps what it carried
PARLEY_RETENTION_MAX_AGE_HOURS=720
PARLEY_TICK_SECONDS=5                   # how often deadlines are swept
```

Each of these used to be set per repository, which meant one store could be swept on three
cadences and pruned to two floors with nothing anywhere to say which was in force.

The clarify cap moved here too, and is not configurable yet: an exchange admits 200
clarifications before the broker forces an `unknown`. That number is a measurement rather than
a considered limit — a cap censors the evidence that would justify it, so it is set far above
any plausible need until `parley clarifications` says where depth actually settles.

### 3. Point each agent at the broker

Every agent that should be able to reach every other connects to the same broker and gets a
different `PARLEY_AGENT`:

```jsonc
{
  "mcpServers": {
    "parley": {
      "command": "parley-mcp",
      "env": {
        "PARLEY_AGENT": "orders",                     // the role; the machine is appended
        "PARLEY_OWNS": "Carts, orders, line totals. No tax, no stock.", // what peers route by
        "PARLEY_BROKER": "127.0.0.1:51707",           // optional, this is the default
        "PARLEY_PROJECT": "acme",                     // optional, instead of PARLEY_BROKER
        "PARLEY_RESPONDER": "some-model-9",           // optional, which model answers
        "PARLEY_WORKSPACE": "/src/acme-orders",       // optional, for the registry
        "PARLEY_MACHINE": "server-b"                      // optional, detected if unset
      }
    }
  }
}
```

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

It is optional. An agent without one is listed without one, and askers fall back to the name —
which usually works, because a wrong guess costs one message: the answerer refers the question
to whoever does own it rather than answering badly. `parley registry` shows you which of your
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

Seven tools, and no way to say who it is:

| Tool | What it does |
|---|---|
| `parley_ask` | Open an exchange. Returns an identifier or a refusal — never an answer. |
| `parley_clarify` | Ask for detail inside an exchange you owe, or supply detail you were asked for. |
| `parley_answer` | Resolve each sub-question and close the exchange. |
| `parley_notify` | Fire-and-forget event. No reply expected. |
| `parley_inbox` | Drain pending messages, work you owe first. |
| `parley_agents` | List the agents that exist and whether they are reachable. |
| `parley_conclude` | Say whether you are finished with a discussion. |

A question must carry its **consequences** — what the asker will do with each possible
answer. The broker refuses one that does not, because an answerer who knows what turns
on the reply gives a different and better answer. Messages are capped at 4,000
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

With the setup above, `orders` opens one exchange:

> **intent** — deciding whether the total handed to billing must be net or
> VAT-inclusive, so tax is applied exactly once across the two repositories
>
> **q1** — does billing apply VAT to the total it receives, or expect it included?
>
> **consequences** — if billing applies it, I keep the net total and record the
> contract; no code change. If it expects the total to include VAT, every invoice is
> short by the VAT and I report that before shipping.

`billing` collects it, answers from what its own code actually does, and the exchange
closes. Neither agent read the other's files, and the answer is recorded where an
operator can read it back later with `parley export`.

### Retention

The message log is a channel, not an archive. It holds the full text of every question
and answer, so left unbounded it becomes the shared context store Parley exists to
avoid — and grows the blast radius of a leaked file for ever.

So it forgets by default: a discussion whose exchanges have all closed is removed once
it has been quiet for `PARLEY_RETENTION_IDLE_HOURS` (three days if unset), and nothing
outlives `PARLEY_RETENTION_MAX_AGE_HOURS` (thirty days if unset).

Set those **on the agents' servers**, not only in your own shell — the servers are what
apply the policy, on every sweep. `parley prune` only brings the next sweep forward. Set
them in one place and not the other and your agents quietly enforce the default while
`parley prune` reports the numbers you meant.

A floor bounds the dial in the other direction, and is not configurable. Tightening
retention can never delete a message nobody has collected yet, the question of an
exchange that is still open, or the recent rows deduplication is still reading.

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

**It answers; it does not initiate.** The fleet starts agents that have something *waiting*
— an answer they owe, or a message they have not collected — so with nothing waiting it
starts nobody. The first question still comes from an agent that has a reason to ask, and
reasons come from work; an idle agent inventing questions would be worse than useless.
After that first question the loop needs no one: the answerer is started to answer, a
clarification puts the asker in debt, and it is started in its turn.

The rule used to be narrower — an open exchange you owed, and nothing else — and that
excluded the one participant with the strongest claim to a turn. An answer closes the
exchange it resolves, so the agent that asked the question ends up owing nothing and was
never started to read the reply it was waiting for. A ghost, which is the notice that
nobody answered, arrived the same way. So an unattended fleet could produce answers that
nothing ever collected. Anything uncollected now counts, whatever its type.

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
parley waiting <agent>              how many messages and polls await an agent
parley serve                        run the broker: the one process that opens the store
parley up                           run this machine's node, so its agents can be started
parley project list|add|remove      the deployments this machine is configured for
parley --help                       the same list, on stdout, exiting zero
```

Every one of these except `serve`, `up` and `project` is a client: they connect to the broker at
`--broker`, or `PARLEY_BROKER`, and default to the local one. None of them opens the store, so none can be pointed at the wrong
file — `--db` belongs to `parley serve` alone. `prune` applies the policy the broker was started
with, which is the only policy in the deployment; your shell no longer has an opinion about it.

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

- Two agents on one database hold a question and an answer between them, over MCP, with
  all seven tools working.
- Expired exchanges are reaped, releasing the pair slot and the wait-for edge a vanished
  agent would otherwise hold for ever.
- An agent whose repository has been deleted is marked unreachable and questions to it
  are refused at admission, rather than each one waiting out a full window to learn
  nothing.
- A question asked while the recipient's session is closed waits for that agent rather
  than being released when it opens one.
- A question nobody ever answers ends by telling the asker so, carrying the asker's own
  stated consequences back — so "ask and do not block" holds in the case an agent most
  needs to hear about.
- A discussion closes when its participants agree, or when one of them says nothing for
  long enough, so a silent peer cannot hold one open indefinitely.
- The message log is bounded in both directions: it forgets on a schedule even when
  nobody configures it, and a floor stops an aggressive setting deleting mail nobody has
  read.
- All five operator commands are reachable through a second binary agents are never
  given.
- Claude Code installs it as a plugin: this repository is the marketplace, a setup command
  writes a repository's launch configuration, and the operator commands ship as a skill.

The domain layer has no I/O and no package dependencies, and its tests do not reference
the storage assembly — the absent dependency is checked by the build rather than left as
a convention. The protocol's invariants are enforced by database constraints rather than
by the code that calls them.

**Next.** Per-sender rate limits. Volume between a pair is already bounded by the state
machine — one open exchange per pair, a cap on clarification rounds — so what remains is
`Notify`, which has no such guard: an agent can post into a discussion indefinitely, and
today only the retention age cap bounds it.

**Before the first release.** `Parley.Mcp` and `Parley.Cli` go to NuGet, so installing either
is `dotnet tool install -g` rather than a clone and a build, and the plugin's first step
becomes one line. Every install path above currently asks you to build from source, which is
the alpha showing rather than a design position.

Issues and discussion are welcome — particularly on the open questions at the end of
SPEC.md.

## License

Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
