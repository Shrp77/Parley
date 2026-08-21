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

Parley ships an MCP server: one process per agent per project, launched by whatever MCP
client the agent lives in.

```
dotnet pack src/Parley.Mcp -c Release
dotnet tool install -g --add-source src/Parley.Mcp/bin/Release Parley.Mcp
```

That puts `parley-mcp` on PATH, which is what lets a launch configuration name it:

```jsonc
{
  "mcpServers": {
    "parley": {
      "command": "parley-mcp",
      "env": {
        "PARLEY_DB": "/src/acme/.parley/parley.db",   // one file per project
        "PARLEY_AGENT": "orders",                     // who this process speaks for
        "PARLEY_RESPONDER": "some-model-9",           // optional, see SPEC §11.3
        "PARLEY_WORKSPACE": "/src/acme-orders",       // optional, for the registry
        "PARLEY_TICK_SECONDS": "60"                   // optional, sweep cadence
      }
    }
  }
}
```

Point every agent on a project at the same `PARLEY_DB` and they can talk. Nothing else
is installed and nothing is left running: there is no daemon, no port, and no service to
supervise. Agents that are not open have no process, which is the point — a machine that
reboots comes back with zero Parley processes and loses nothing, because the state is in
the file and each agent's server starts when somebody opens that repository.

The agent gets seven tools — `parley_ask`, `parley_clarify`, `parley_answer`,
`parley_notify`, `parley_inbox`, `parley_agents`, `parley_conclude` — and no way to say
who it is. **Identity comes from `PARLEY_AGENT` and from nowhere else**, so no tool call
can change who the caller is; had it been a parameter, every agent could act as every
other and SPEC §13 would rest on a model choosing not to. The operator operations of
§10.2 are not on that surface at all, and not by naming convention: they live on a
different type, so an adapter has nothing to bind.

An agent registers itself the first time its server starts (§12.3). That is the whole
setup, and it means an agent is addressable once somebody has opened its repository —
addressing one that has never run needs the manifest, which is what the headless tier
and spawn-on-demand are for.

### The operator's side

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
parley prune                        apply the retention policy of §9.4 now
```

It reads `PARLEY_DB` — the same variable the agents' servers are launched with — or takes
`--db`. `prune` reads the retention policy from `PARLEY_RETENTION_IDLE_HOURS` and
`PARLEY_RETENTION_MAX_AGE_HOURS` the same way, and prints the policy it applied before it
prints what it removed: an operator's shell is not the environment their MCP client launched
the servers with, and a command that deletes on a configured schedule should say which
schedule it used.

These are the operations of SPEC §10.2, and a separate program on purpose. §10.2 warns that an
adapter surfacing one into an agent's tool set would breach §10.1, and the safeguards get
progressively more structural: `force_conclude` is deliberately not `conclude`; the operations
are not on `Broker`, so an adapter handed a broker has none to bind; and they are not in
`parley-mcp`, so the binary an MCP client launches has none of them compiled into it. A test
asserts that last one by reflecting over the real assembly.

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

**Built.** The exchange state machine, pair serialisation, and wait-for cycle
detection, over a SQLite store: schema, migrations, and the exchange, agent and message
repositories, including the trust envelope and message deduplication. Expired exchanges
are reaped, releasing the pair slot and the wait-for edge a vanished agent would
otherwise hold forever. An agent whose repository has been deleted is marked unreachable
and questions to it are refused at admission, rather than each one waiting out a full
inactivity window to learn nothing. The seven agent operations of §10.1 are built and
reachable over MCP, and two servers on one database hold a question and an answer between
them. A question asked while the recipient's session is closed waits for that agent rather
than being released when it opens one — the attach sequence of §14 releases only what an
agent has actually been handed. A question nobody ever answers ends by telling the asker so,
carrying its own stated consequences back — so "ask and do not block" holds in the case where
nothing arrives, which is the case an agent most needs to hear about. A discussion closes when its
participants agree, or when one of them says nothing for long enough — §5.0's silence-assents
window is enforced by the sweep, so a peer that never answers a closure poll does not hold a
conversation open indefinitely. The message log is bounded: §9.4's retention policy runs on the
ordinary sweep, so a deployment nobody configures still forgets — a channel that kept everything
would be the shared context store §16 says Parley is not, and the file already holds the full
text of every question and answer. A floor bounds the dial in the other direction, so tightening
retention cannot delete an undelivered message, a live exchange's question, or the rows
deduplication is still reading. All five operator operations of §10.2 —
`retire`, `reinstate`, `export`, `force_conclude`, `prune` — are reachable through a second
binary that agents are never given. The domain layer has no I/O and no package dependencies, and its tests
do not reference the storage assembly — the absent dependency is checked by the build
rather than left as a convention. The protocol's invariants are enforced by schema
constraints rather than by the code that calls them.

**Next, in order.**

1. **Per-sender rate limits.** The last unbuilt control in SPEC §13. Volume between a
   pair is already bounded by the state machine — one open exchange per pair, a
   clarification cap — so this is really about `Notify`, which has no such guard. It also
   brings the one clause §9.4's retention floor names and does not yet have: a rate-limit
   window, beside the deduplication window already there.

Issues and discussion are welcome — particularly on the open questions at the end of
SPEC.md.

## License

Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
