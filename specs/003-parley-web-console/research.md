# Phase 0 Research: Parley Web Console

**Feature**: [spec.md](./spec.md) | **Plan**: [plan.md](./plan.md) | **Date**: 2026-08-30

The console renders what the operator surface already grants. So the first question is not what
to build with, but what that surface actually offers — and where the specification asks for
something it does not.

## 1. Audit of the existing operator surface

`service Operators` in `proto/parley.proto`, and `OperatorConsole` in `src/Parley.Broker`:

| Operation | Returns | Useful here? |
|---|---|---|
| `DescribeRegistry` | name, adapter, tier, status, presence, workspace, session_ref, owns, connected | Yes — the whole agent view |
| `Export(conversation_id)` | every message carrying that identifier, ordered by insertion id | Yes — the whole transcript, once you know the identifier |
| `DescribeRefusals` | size refusals per agent | Not in scope for this version |
| `DescribeClarifications` | clarify depth report | Not in scope |
| `DescribeReferrals` | referral report | Not in scope |
| `DescribeNotifications` | notify volume per agent | Not in scope |
| `ForceConclude`, `Retire`, `Reinstate`, `Purge`, `Prune` | state changes | Deliberately excluded — FR-023 makes this version read-only |

### Requirement coverage against that surface

| Requirement | Satisfied by | Verdict |
|---|---|---|
| FR-001…FR-004 (projects) | `Parley.Configuration` reading the manifest; one client per endpoint | Today |
| FR-005…FR-007 (agents) | `DescribeRegistry` — carries `owns`, `presence`, `status` | Today |
| FR-008 (time-ordered exchanges) | `Export`, ordered by insertion id | Today |
| FR-009 (name the pair) | `Message.from` / `Message.to` | Today |
| FR-010 (who could see it) | Derived: participants are the union of `from`/`to` across the conversation; a message's readers are exactly its own pair | Today, client-side |
| FR-011 (annotate inline) | Rendering concern | Today |
| **FR-012 (exchange state)** | — | **Gap 1** |
| FR-013 (responder identity) | `Message.responder_model` | Today |
| FR-014 (state the ordering) | `Export` orders by insertion id, not timestamp | Today, and better than expected — see §3 |
| **FR-015 (find a conversation)** | — | **Gap 2** |
| FR-016 (render as data) | Razor encodes by default | Today |
| FR-017…FR-020 (registration guidance) | Static text plus the selected project's endpoint | Today |
| FR-021…FR-023 (boundary) | Structural — client of `Operators` only, loopback binding, no mutating call bound | Today |

## 2. The two gaps

### Gap 1 — `Export` returns messages, and messages carry no exchange state

**Decision**: `SPEC.md` §10.2 gains the exchange state alongside the messages of an export.

**Rationale**: FR-012 requires an open exchange to be shown as open and a ghosted one as
ghosted. The wire `Message` carries `exchange_id` but nothing about the exchange, and the export
query selects from the message table alone. Without this, the console would render an abandoned
question and an answered one identically — the single most misleading thing it could do, because
the operator's reason for opening a conversation is usually that something went wrong.

**Alternatives considered**: Inferring state from the message sequence — if an `answer` exists
the exchange closed, otherwise it is open. Rejected: it cannot distinguish orphaned from open,
cannot see a ghost at all (a ghost closes an exchange and the §14 notice goes to the asker), and
would silently disagree with the store the moment the state machine gained a transition.

### Gap 2 — nothing lists conversations

**Decision**: `SPEC.md` §10.2 gains a paged listing of conversations, filterable by participant
and by time window, returning identifiers with enough summary to choose between them —
participants, message count, first and last activity, and whether anything in it is still open.

**Rationale**: `Export` takes a `conversation_id` and there is no operation that produces one. An
operator who does not already know the identifier cannot reach any conversation at all, which
makes User Story 1 — the reason to build this — unreachable. The identifier is chosen by an
agent and is opaque, so it is not something a person would know.

**Alternatives considered**:

1. *Export everything and index client-side.* Rejected: an unbounded read of the entire message
   log to answer a listing question, repeated per page view, and it grows without limit.
2. *Query the store directly from the console.* Rejected outright — §4 gives the file to one
   process, and a second reader is the corruption that section exists to prevent.
3. *Derive the list from the registry.* Rejected: the registry knows agents, not conversations.

**Bound**: the listing is paged from the first version. Constitution principle IV is about text
crossing into an agent's context and does not literally apply to an operator read, but the
discipline does: an unbounded operator query is the same mistake one level up, and a bound
retrofitted later is a bound nobody applies.

**Both gaps are specification changes first.** Neither alters protocol behaviour — no message
type, no state transition, no new invariant — but §10.2 is where operator operations are
described, and code that answers a question the document does not pose is code with no
authority behind it.

## 3. Ordering, and a finding that removes a stated risk

**Decision**: order the transcript by the export's own order and label timestamps as
informational.

**Rationale**: The spec's Edge Cases anticipated clock skew between machines corrupting a
timeline. It does not, because `Export` orders by insertion id — a single monotonic sequence
assigned by the one process that owns the store — rather than by `created_utc`. Every message in
a project passes through one broker, so the order the console renders is the order the broker
recorded, regardless of what any agent's clock said.

`created_utc` and `delivered_utc` are still shown, because the gap between them is the operator's
evidence of a message sitting uncollected. FR-014 is satisfied by saying which of the two the
ordering follows, rather than by any correction.

## 4. Stack

**Decision**: ASP.NET Core with server-rendered Razor Pages. No npm, no JavaScript build, no
client framework. Interactivity limited to progressive enhancement with hand-written script.

**Rationale**: The constitution makes a green build the whole lint gate — warnings are errors,
documentation is enforced, package versions are centralised, and `NuGetAudit` fails the build on
high or critical advisories. A JavaScript toolchain is a second build system with its own
dependency tree, its own advisories, and its own lint story, none of which that gate covers. For
a read-only console of a few screens, the cost is real and the benefit is small.

Razor's default HTML encoding also discharges FR-016 structurally rather than by discipline:
message text is untrusted input written by an agent, and the framework encodes it unless somebody
explicitly asks for raw output. The review rule becomes "no raw output on a message payload",
which is greppable.

**Alternatives considered**: Blazor Server (adds a stateful circuit and SignalR for a read-only
view that does not need one); a single-page app against a JSON API (the npm cost above, plus a
second contract to keep in step with the wire types); a terminal UI (the operator already has a
CLI — the request was explicitly for a web UI).

## 5. Binding and exposure

**Decision**: bind loopback by default; refuse a non-loopback binding unless explicitly
configured, and say so at startup when it is.

**Rationale**: §16 confines the transport to a trusted path, because anything able to reach the
broker can claim to be any agent. The console is a second door onto that same path. It is not an
authentication boundary and must not be mistaken for one, so the safe default is the one where
the question does not arise, and widening it is a deliberate act that announces itself.

**Alternatives considered**: shipping an authentication scheme. Rejected for this version: §16
names credential-per-agent as "a real design with real key distribution behind it and is not in
this document", and a console-only login would protect the web page while leaving the broker
endpoint beside it as open as before — security theatre with a maintenance cost.

## 6. Multi-project fan-out

**Decision**: one `Operators` client per configured project, constructed from the manifest, with
per-project failure isolation and a short connect timeout.

**Rationale**: §4.1 gives each project its own store and endpoint and states that the protocol has
no concept of a project and must not acquire one. So the fan-out is entirely client-side: the
console holds the mapping, the brokers know nothing of each other, and FR-003 is satisfied by
construction. SC-004's five-second budget for reporting an unreachable project argues for a
connect timeout in the low seconds, matching the two-second timeout `Parley.Mcp` already uses and
for the same reason — a stopped broker is stopped, and waiting longer does not find it.
