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

Getting two agents talking takes three things: install the server, point each agent's
client at it, and **tell each agent that it has a peer**. The third is the one people
skip, and without it nothing happens — an agent with the tools bound but no idea a peer
exists will answer from its own repository and guess at the rest.

### 1. Install the server

Parley ships an MCP server: one process per agent per project, launched by whatever MCP
client the agent lives in.

```
dotnet pack src/Parley.Mcp -c Release
dotnet tool install -g --add-source src/Parley.Mcp/bin/Release Parley.Mcp
```

That puts `parley-mcp` on PATH, which is what lets a launch configuration name it.

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
        "PARLEY_RESPONDER": "some-model-9",           // optional, which model answers
        "PARLEY_WORKSPACE": "/src/acme-orders",       // optional, for the registry
        "PARLEY_TICK_SECONDS": "60",                  // optional, sweep cadence
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

### 3. Tell the agent it has a peer

**This is the step that makes Parley work, and nothing installs it for you.**

Binding the tools tells an agent *how* to ask. It does not tell it *that there is anyone
to ask*, or which questions are not its to answer. That belongs in whatever file your
client reads as standing instructions for a repository — `CLAUDE.md` for Claude Code,
`AGENTS.md` for several other tools, a system prompt, or a project rules file. The
content is the same wherever it goes:

```markdown
## Reaching billing

The `billing` agent works in a separate repository and a separate session. Reach it
with the `parley` tools:

- `parley_ask` — one question, with an intent, sub-questions, and consequences. State
  what you will do with each possible answer; a question that does not is refused.
- `parley_inbox` — collect what is waiting for you. Nothing arrives unprompted, so
  check when you start work.
- `parley_answer` / `parley_clarify` — when billing asks you something.
- `parley_conclude` — when a discussion is finished.

Asking does not block. If billing never replies you are told so rather than waiting.

**Do not guess at billing's behaviour and do not edit billing's repository.** Ask.
```

Two lines of that do most of the work: naming the peer, and saying which boundary the
agent does not own. Without them an agent will confidently invent the other side's
behaviour, which is the failure Parley exists to prevent.

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
on the reply gives a different and better answer. Messages are capped at 2,000
characters, which is a context-poisoning defence rather than a buffer size: what you
send lands in somebody else's working memory.

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
parley agents                       every agent, with the workspace a peer never sees
parley retire <agent>               withdraw a registration; its server then refuses to start
parley reinstate <agent>            the only thing that clears a retirement
parley export <conversation>        every message in a discussion, across agents
parley force-conclude <conv>        close a discussion over its participants' votes
parley prune                        apply the retention policy now
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

The domain layer has no I/O and no package dependencies, and its tests do not reference
the storage assembly — the absent dependency is checked by the build rather than left as
a convention. The protocol's invariants are enforced by database constraints rather than
by the code that calls them.

**Next.** Per-sender rate limits. Volume between a pair is already bounded by the state
machine — one open exchange per pair, a cap on clarification rounds — so what remains is
`Notify`, which has no such guard: an agent can post into a discussion indefinitely, and
today only the retention age cap bounds it.

Issues and discussion are welcome — particularly on the open questions at the end of
SPEC.md.

## License

Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
