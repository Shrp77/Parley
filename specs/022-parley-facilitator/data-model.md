# Data model: Parley as facilitator

All storage is in a project's own store, added to `001_initial.sql` in place (research R5: nothing is released,
and the schema is one file). SPEC.md §9.2 carries the same fragments, and `SpecSchemaTests` executes them.

## The facilitator's row (existing table `agents`)

| Field | Value |
|---|---|
| `name` | `parley-facilitator@<machine the broker runs on>`; shown as **Parley** |
| `adapter` | `parley-facilitator` |
| `tier` | `pull` |
| `workspace`, `session_ref`, `adapter_config`, `owns` | NULL |
| `status` | `active`; never retired |

**Written by** the broker when it opens a store, if absent. Never by an attach. Found by its adapter, never by
its name, so a renamed machine does not make a second row. The role is not `parley`: this repository registers as
its own agent under that role, and enrolment turns a folder named `Parley` into it (research R1).

**Rules held in storage** (Principle V):
- A row whose `role` is `parley-facilitator` must have adapter `parley-facilitator`.
- A row whose adapter is `parley-facilitator` must have role `parley-facilitator`.
- The facilitator cannot be retired: `status` stays `active` for that adapter.

## `ownership_questions` (new)

One row per question the facilitator has asked. The latest row for an agent is its current question; earlier
rows are history, kept so "asked again" does not erase what was said before.

| Column | Meaning | Rule |
|---|---|---|
| `id` | Sequence | Primary key |
| `agent` | Who was asked | References `agents(name)`, cascade on purge; never the facilitator; never a seat |
| `conversation_id` | Its ownership conversation | References `conversations(id)`, cascade on destroy |
| `exchange_id` | The Request | References `exchanges(id)`, cascade |
| `state` | `asked`, `asked_back`, `answered`, `waiting`, `accepted`, `discarded` | CHECK |
| `answer` | The agent's words as written | NULL until answered |
| `flattened` | The answer with whitespace collapsed (research R7) | NULL until answered |
| `waiting_because` | Why it was not accepted automatically | Required when `waiting`, NULL otherwise |
| `asked_utc`, `settled_utc` | When asked, and when accepted or discarded | `settled_utc` required for `accepted` and `discarded` |
| `restart_sent` | Whether the node was asked to refresh the agent (research R9) | 0/1; at most one row per agent ever has 1 |

**States**:

```
asked ──(agent answers)──▶ answered ──(auto accept, valid, this machine)──▶ accepted
  │                            └────(otherwise)──────────────────────────▶ waiting
  └──(agent asks back)──▶ asked_back ──(person replies)──▶ asked
waiting ──(person accepts / edits)──▶ accepted
waiting ──(person discards)──────────▶ discarded
```

**Invariants**:
- At most one row per agent in any of `asked`, `asked_back`, `answered`, `waiting` (a partial unique index) — the
  "never asked twice" rule (FR-008) as a storage fact rather than a check in code.
- A row can be `accepted` only if `agent_ownership_accepted` holds a line for that agent.
- Only the facilitator's own exchange may be a question: `exchange_id`'s requester is the facilitator.
- A question in any state, `discarded` included, means the agent has been asked: automatic asking never follows
  it, and only `ask_again` opens another (FR-008).

## `agent_ownership_accepted` (existing, one change)

- `source` gains `automatic`: `CHECK (source IN ('proposed','edited','composed','automatic'))`.
- `automatic` follows `proposed`'s cross-checks: a `conversation_id` is required and `proposed_length` is recorded.
- `conversation_id` is `ON DELETE SET NULL` (research finding 14): destroying the conversation a line was agreed
  in keeps the line and loses only the link. So the requirement that `proposed` and `automatic` name a
  conversation moves from a CHECK to a BEFORE INSERT trigger — a CHECK would refuse the SET NULL. T046 probes
  destroy and purge, failing first.

## `facilitation` (new, one row)

| Column | Meaning | Rule |
|---|---|---|
| `id` | Always 1 | `CHECK (id = 1)` |
| `auto_accept` | Whether answers are accepted without a person | 0/1, default 1 |
| `changed_utc` | When it was last set | |

A store without the row reads as **on** (FR-010: a project created before this feature reads as on).

## Messages the facilitator writes (existing table `messages`)

| Attribution (`responder_model`) | Written when | Rendered as |
|---|---|---|
| `parley:facilitator` | The question itself | "Parley", never a peer |
| `parley:person` | A reply a person wrote to an agent that asked back | A person's words, sent through Parley |

Both are in the reserved `parley:` namespace, which `Broker.RequireAdapterResponder` refuses to any adapter, so
no agent can write either.

## Registry reads (§10.2 `registry()`)

Each row gains:
- `facilitator` (bool): show as Parley, not as an agent or seat.
- `owns_source`: the provenance of the accepted line, previously stored and never returned.

`agents()` (§10.1) omits the facilitator entirely.
