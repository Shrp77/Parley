# Data model — 006

Phase 1. What changes in storage, and what the surfaces derive rather than store.

## Stored

### `conversations` gains one column

```sql
ALTER TABLE conversations ADD COLUMN archived_utc TEXT;
```

Archived is `archived_utc IS NOT NULL`. Nullable, no default, so every existing row is unarchived
without a backfill.

**Not a fourth value in `state`.** `state` is the closure lifecycle — `OPEN | CLOSING | CLOSED`,
bound to `closed_utc` by a check and driven by the participants' votes. Archiving is orthogonal: an
open conversation may be filed away and a closed one is not thereby filed. Folding them together
would make the first conversation that is both lose one of the two facts. `research.md` R2.

| Field | Type | Meaning |
|---|---|---|
| `archived_utc` | TEXT, null | When an operator archived it. Null means it is in the ordinary list. |

### `destroyed_conversations` — a new table, holding facts and no content

```sql
CREATE TABLE destroyed_conversations (
  id             TEXT PRIMARY KEY,
  destroyed_utc  TEXT NOT NULL,
  messages       INTEGER NOT NULL CHECK (messages >= 0),
  participants   TEXT NOT NULL
);
```

| Field | Meaning |
|---|---|
| `id` | The conversation's identifier, which is its name. Primary key, so the name cannot be reused by a new conversation under somebody's existing export. |
| `destroyed_utc` | When. |
| `messages` | How many were destroyed. A count, never the messages. |
| `participants` | Which agents were party to it, as names. Already public in the registry. |

**No message text, no payloads, no intents.** §9.4 opens *"Parley is a channel, not a store"* and
argues that an unbounded log grows the blast radius of a leaked database; keeping content past a
destroy is the exact opposite of what the operation is for. What is kept is what lets an operator
answer *"what happened to it"* — which is the failure `parley waiting` and `parley refusals` exist
to avoid elsewhere, an absence that could equally be a destroy or a store that lost something.

### The cascade is the invariant, and it lives in the schema

Destroying a conversation must leave no message of it. That is a foreign key with `ON DELETE
CASCADE` from `messages` (and from `exchanges`) to `conversations`, not a sequence of deletes a
repository method performs — Principle V, and §9.3's argument that application code can route around
a check it makes itself.

The test that carries it reconstructs the defect rather than asserting the happy path: a message of
the destroyed conversation surviving, **and** a message of a different conversation not surviving.
The second half is the one that catches a cascade written too wide.

## Read, not stored

### Agent standing — FR-009's one readable phrase

Two independent facts, and today's wording names the mechanism instead of either of them.

| Fact | Where it comes from | What today says | What it means |
|---|---|---|---|
| Has this agent ever announced itself? | the registry holds a row, or does not | *catalogued, not yet registered* | it has never run |
| May anything on this machine start it? | the project's catalogue names the role | *not in this catalogue* | nothing here can start it |
| Is it attached right now? | `AgentLiveness.Read` | *running* / *startable* / *stranded* | unchanged |

`AgentLiveness.Read(nodeConnected, agentConnected)` already derives presence, and its remarks record
that the agent's own connection is checked first — a correction made because the old order reported
`Stranded` for the agents most obviously reachable of all. That derivation is not touched.

What is added is the pairing of presence with catalogue membership into one phrase per row. The
exact words are chosen with the owner (`plan.md`, "What this plan does not decide"), because the
reason they are changing is that the last set was chosen without them.

### Conversation summary — nothing new is needed

§10.2's `conversations()` already returns *"the identifier, the participants derived from their
exchanges, how many messages each carries, when it was first and last active, and whether anything in
it is still open."* That is the whole of FR-013 and FR-014.

It gains one boolean — archived — and its request gains one option, whether to include archived
conversations. Nothing else. `research.md` R6.

### Colour

`Parley.Client.Rendering.Palette`, unchanged: eight slots, assigned from an ordinal hash of the
agent's whole name, so an agent is one colour on every page and in every conversation. Collisions are
survivable because every message names its sender in words, and FR-018 requires headers to stay
distinguishable with colour removed. `research.md` R7.

### Local time

Derived at the view from the stored UTC value, through one shared helper so the two surfaces cannot
write a local time differently. **The export is not a view** and keeps UTC: its reader is by
definition somebody else, somewhere else, and a file saying `14:03` without saying whose is worse
than one saying `14:03Z`. `research.md` R8.

### Message body layout

Derived from the stored text, which is unchanged. Line breaks and paragraphs are the author's own and
must survive; JSON payloads are laid out at render time. Nothing about how a message is stored
changes, and nothing an agent wrote may alter the screen an operator is deciding on — FR-021, which
is 005's `Safe` and is not weakened by any of this.

## Entity relationships

```text
conversations 1---* exchanges 1---* messages
      |
      | archived_utc (null = in the ordinary list)
      |
      +-- destroyed --> destroyed_conversations (id, when, count, participants)
                        content does not travel here

agents (registry)  ----+
                       |--> agent standing, per project
project catalogue  ----+     = has it ever run? may anything start it? is it attached?
```
