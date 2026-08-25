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
this repository's name and what it owns, and writes the launch configuration — then hands you
to `/parley:peers`, which builds the peers block from the roster rather than from what you can
remember. The plugin also carries the operator commands as a skill, so you can ask for a
roster or an export in words rather than remembering the flags.

It stops short of supplying the server itself, and that is deliberate: Claude Code stores a
plugin's configuration per user, so a plugin-supplied server would give every repository the
same agent name. Identity belongs to the repository, so the launch configuration does too.
[The plugin's own README](plugins/parley/README.md) has the detail.

**While Parley is in alpha the first step is not one command.** Nothing is published to
NuGet yet, so setup asks you to clone this repository and build the tool. Publishing is what
the first release is for.

The rest of this section is the general path: what the plugin does for you, and what to do
in any other client.

### 1. Install the server

Parley ships an MCP server: one process per agent per project, launched by whatever MCP
client the agent lives in.

```
dotnet pack src/Parley.Mcp -c Release
dotnet tool install -g --add-source src/Parley.Mcp/bin/Release Parley.Mcp
```

That puts `parley-mcp` on PATH, which is what lets a launch configuration name it.

**Reinstalling from a clone has two traps, and both fail quietly.** `--add-source` points at a
directory, and NuGet takes the *highest* version it finds there rather than the newest build —
so an old `.nupkg` left in `bin/Release` wins for ever, and the install reports success. Delete
the stale ones. And `dotnet tool update` does nothing at all when the version has not changed,
at some length, so a rebuilt tool appears to install and does not. `dotnet tool uninstall`
followed by `install` is the reliable pair. Check what you got with `parley --version`.

### 2. Point each agent at the same database

One file per project. Every agent that should be able to reach every other gets the same
`PARLEY_DB` and a different `PARLEY_AGENT`:

```jsonc
{
  "mcpServers": {
    "parley": {
      "command": "parley-mcp",
      "env": {
        "PARLEY_DB": "/src/acme/.parley/parley.db",   // one file per project
        "PARLEY_AGENT": "orders",                     // who this process speaks for
        "PARLEY_OWNS": "Carts, orders, line totals. No tax, no stock.", // what peers route by
        "PARLEY_RESPONDER": "some-model-9",           // optional, which model answers
        "PARLEY_WORKSPACE": "/src/acme-orders",       // optional, for the registry
        "PARLEY_TICK_SECONDS": "60",                  // optional, sweep cadence
        "PARLEY_CLARIFY_CAP": "3",                    // optional, clarifications per exchange
        "PARLEY_RETENTION_IDLE_HOURS": "72",          // optional, see Retention below
        "PARLEY_RETENTION_MAX_AGE_HOURS": "720"       // optional, see Retention below
      }
    }
  }
}
```

For Claude Code that file is `.mcp.json` in the repository root; other clients have
their own location for the same JSON.

There is no daemon, no port and no service to supervise. Agents that are not open have
no process, which is the point — a machine that reboots comes back with zero Parley
processes and loses nothing, because the state is in the file and each agent's server
starts when somebody opens that repository. An agent registers itself the first time its
server starts, so no roster has to be maintained by hand.

**Identity comes from `PARLEY_AGENT` and from nowhere else.** No tool takes a "from"
parameter, so no tool call can change who the caller is. Had it been a parameter, every
agent could impersonate every other and the guarantee would rest on a model choosing not
to.

### 3. Say what each agent owns

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

### 4. Name the peers and the boundary

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

## The operator's side

`parley` is a second binary, installed by a person and never given to an agent:

```
dotnet pack src/Parley.Cli -c Release
dotnet tool install -g --add-source src/Parley.Cli/bin/Release Parley.Cli
```

```
parley registry                     every agent, what it owns, and the workspace a peer
                                    never sees
parley retire <agent>               withdraw a registration; its server then refuses to start
parley reinstate <agent>            the only thing that clears a retirement
parley export <conversation>        every message in a discussion, across agents
parley force-conclude <conv>        close a discussion over its participants' votes
parley prune                        apply the retention policy now
parley refusals                     what the message size cap refused, and how far over
parley --help                       the same list, on stdout, exiting zero
```

It reads `PARLEY_DB` — the same variable the agents' servers are launched with — or
takes `--db`. `prune` reads the retention variables the same way, and prints the policy
it applied before it prints what it removed, because your shell is not the environment
your MCP client launched the servers with.

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
