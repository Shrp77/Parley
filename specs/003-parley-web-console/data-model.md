# Phase 1 Data Model: Parley Web Console

**Feature**: [spec.md](./spec.md) | **Plan**: [plan.md](./plan.md) | **Date**: 2026-08-30

The console owns no storage. Everything below is a **view model** — shaped in the web process
from what the wire returns, held for the length of one request, and never written anywhere. The
authoritative definitions live in `proto/parley.proto` and `SPEC.md`; this document records the
shaping and, in two places, the derivation.

## Project

A deployment on this machine. Read from the manifest by `Parley.Configuration`; never from a
broker, because the protocol has no concept of a project (§4.1).

| Field | Source | Notes |
|---|---|---|
| `Name` | manifest | Unique on this machine only |
| `Endpoint` | manifest | The broker this project's agents connect to |
| `Reachability` | observed | `Reachable` or `Unreachable`, from whether the client connected |

**Rules**

- Two projects may each hold an agent of the same name. Nothing may key on an agent name alone;
  the key is always (project, agent). — FR-002, FR-004, edge case *duplicate names*.
- A project that names no agents is a valid, configured state and is not an error.
- Reachability is observed per request, never cached across requests, and never stored.

## Agent

One row of `DescribeRegistry`, scoped to a project.

| Field | Source | Notes |
|---|---|---|
| `Name` | `RegistryRow.name` | `role@machine` |
| `Owns` | `RegistryRow.owns` | May be empty — render as *not stated*, never invent |
| `Tier` | `RegistryRow.tier` | Full, Pull, Headless |
| `Presence` | `RegistryRow.presence` | Running, Startable, Stranded |
| `Status` | `RegistryRow.status` | Carries retirement, which outranks presence |
| `Workspace` | `RegistryRow.workspace` | Operator-only. Never rendered into anything an agent reads |

**Rules**

- Retired outranks presence: an agent that is retired is shown as retired regardless of what
  presence reports. — FR-006.
- An absent `owns` is displayed as absent. — FR-007.

## Conversation

A set of pairwise exchanges sharing an identifier chosen by an agent (§5.0). **Membership is
derived, never declared** — an agent is a participant because it is party to an exchange carrying
the identifier.

| Field | Source | Notes |
|---|---|---|
| `Id` | messages / listing | Opaque. Chosen by an agent, not by the broker |
| `Participants` | derived | Union of `from` and `to` across the conversation's messages |
| `FirstActivity` / `LastActivity` | derived or listing | Displayed; not the ordering key |
| `MessageCount` | derived or listing | |
| `HasOpenExchange` | listing | Needs Gap 1 |

**Rules**

- Participation is derived from exchanges and is **not evidence of agreement**. An identifier is a
  claim by the sender, and an agent may join a discussion by naming it. The view says who is party
  to what; it must not say that membership was agreed. — edge case *gate-crashed conversation*.
- A conversation of one exchange is ordinary and renders as one exchange, not as a thread.

## Exchange

One question and its resolution between exactly two agents. The unit visibility is scoped to.

| Field | Source | Notes |
|---|---|---|
| `Id` | `Message.exchange_id` | `0` means the message belongs to no exchange — a Notify |
| `Pair` | derived | The two agents party to it |
| `State` | **Gap 1** | Open, Answered, Concluded, Orphaned, Ghosted |

**Rules**

- State is read, never inferred. Inferring "answered because an answer exists" cannot distinguish
  orphaned from open and cannot see a ghost at all. Until Gap 1 is closed, the console shows state
  as unavailable rather than guessing. — FR-012.
- A Notify has no exchange. It belongs to the conversation directly and renders as a message with
  one sender and one recipient and no resolution. — §5.0.

## Message

One rendered item in the transcript.

| Field | Source | Notes |
|---|---|---|
| `From` / `To` | `Message.from` / `to` | Always both rendered — FR-009 |
| `Type` | `Message.type` | Question, Clarify, Answer, Notify |
| `Body` | `Message.payload_json` | Shaped per type for display; **always HTML-encoded** |
| `Responder` | `Message.responder_model` | A model id, `script:<name>`, `human:<name>`, or `parley:broker` |
| `CreatedUtc` / `DeliveredUtc` | wire | Displayed. The gap between them is evidence of a message left uncollected |
| `Order` | export order | Insertion order from the store. **This is the ordering key** |
| `SeenBy` | derived | Exactly `{From, To}` |
| `NotSeenBy` | derived | `Conversation.Participants` minus `SeenBy` |

**Rules**

- `SeenBy` is exactly the pair. There is no case in which a third agent saw a message, because no
  message ever addresses more than one peer (§5.0). — FR-010.
- `NotSeenBy` is rendered inline in the transcript, not behind a control the operator must open.
  — FR-011.
- Ordering follows `Order`, never a timestamp. Every message in a project passes through one
  broker, so insertion order is authoritative and immune to clock differences between machines.
  The view states this. — FR-014.
- `Body` is untrusted input authored by an agent. It is encoded on every path and never emitted
  raw. — FR-016.
- `Responder` is displayed as recorded. `parley:broker` is a legitimate value written by the
  broker for a forced `unknown` and is shown as such. — FR-013.

## The visibility derivation, stated once

For a conversation `C` with participants `P`:

```
P            = ⋃ {m.From, m.To}  for every message m in C
m.SeenBy     = {m.From, m.To}
m.NotSeenBy  = P \ m.SeenBy
```

This is the whole of the annotation FR-010 and FR-011 require. It is derivable entirely from what
`Export` already returns, needs no new field, and is the one piece of arithmetic that keeps the
chat transcript honest: it is what stops the operator reading a thread and concluding that
everyone in it had seen everything above.

## State transitions

The console adds no transition of its own to the exchange state machine of `SPEC.md` §6. It
renders that machine, and it can force-close a discussion its participants will not — which is an
existing operator transition, not a new one. Every change it makes goes through the broker
interface; there is no path from the console to a store. — FR-023.
