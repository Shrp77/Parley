# Research: A Request is the ask

Ten decisions. Each says what was chosen, why, and what was rejected. Several were settled by reading the code rather
than by reasoning about it, and those say where.

## R1 — A new Request's payload is a flat `ask`

**Decision**: `{"type": "request", "to": "billing", "ask": "…"}`. No `items`, no `intent`, no `done_when`.

**Rationale**: the spec's own assumption was a one-element array, to spare the readers. Reading
`Broker.cs:495-505` reverses it: a Request whose items cannot be read is already answered under the single item
`request`, because 0.4's carried-forward questions needed exactly that. A flat `ask` therefore needs no new
machinery at all — `PayloadShape.ItemIds` returns nothing, the broker asks for one outcome named `request`, and
`Delivered.Items` already does the same on the client side. An array of one would keep the shape the owner asked to
remove, in the place people read it.

**Rejected**: `items: [{ "id": "request", "text": "…" }]`. It keeps ids alive in the stored document and in every
transcript, and the first agent to write a second element would be sending something the protocol no longer defines.

## R2 — Migration 011 replaces triggers and rewrites no row

**Decision**: drop `tr_request_is_well_formed` and `tr_result_is_well_formed`, create replacements that hold a new
Request to a non-empty `ask` and a new Result to at least one outcome with the existing item bound, and leave every
stored message exactly as it is.

**Rationale**: the old requirements live in those two triggers (`008_work_across_repositories.sql:322-360` and
`:394-405`), applied `BEFORE INSERT ON messages`. Messages are never updated, so replacing the triggers changes what
may be written from now on and nothing about what was written. That is the whole migration: no table is rebuilt, no
payload is reshaped, and a 0.6 conversation reads afterwards exactly as it read before.

**Rejected**: rewriting stored payloads into the new shape. It would destroy the sender's own words — an intent and a
`done_when` are things a participant wrote — and §9's own rule is that a message is immutable. FR-009 says so
outright.

**Rejected**: leaving the old triggers in place and having the code write a `done_when` of its own. That is storage
enforcing a rule the protocol no longer has, worked around by a surface inventing text nobody typed.

## R3 — A Result keeps `outcomes`, with one entry named `request`

**Decision**: `{"type": "result", "exchange_id": 47, "outcomes": [{ "id": "request", "outcome": "answered",
"text": "…", "sources": ["inventory"] }]}`.

**Rationale**: `result_items` is keyed `(message_id, item)` and the exchange's rolled-up outcome, the flags, the
health reports and `parley results` all read it. `request` is already the id the broker requires when a Request has
no readable items, and already what a person's close writes. Keeping the array costs one line in a composer and
keeps every reader, every table and every report untouched.

**Rejected**: a flat `{"outcome": "...", "text": "..."}`. It would need `tr_result_recorded` rewritten, every reader
taught a second shape, and the two shapes would then both exist for ever because 0.6 Results stay in the store.

## R4 — Gates go with the items, and §7.2 keeps its cycles

**Decision**: remove `gate` from the protocol and its trigger. §7.2's recorded cycle — one agent waiting on another
that is waiting on it — is about exchanges, not items, and is unchanged.

**Rationale**: a gate is a dependency between items of one Request. With one ask there is nothing to gate. Checked
rather than assumed: the only enforcement is the trigger clause at `008:350-357`; no C# reads `$.gate`
(`grep -r '"gate"' src` finds the trigger and the spec's own example). SPEC.md §5.1's argument for syntactic gates is
kept in §18's history, because it is the reason the field existed and a later reader will ask.

## R5 — Rule 8.1 is replaced rather than deleted, and §8 renumbers

**Decision**: the rule that tells a sender to state an intent and a finish line becomes one about saying what you
want in a sentence the receiver could act on, and asking when you do not know how far it goes. The rules of §8 are
renumbered contiguously and the enforcement table follows.

**Rationale**: `Parley.Spec.Tests` asserts the rules of §8 are numbered contiguously and all appear in the
enforcement table, so a deleted rule cannot simply leave a hole. And the advice is still worth giving — what changes
is that the receiver's question, not the sender's form, is where scope is settled.

## R6 — Protocol 0.7, and no compatibility mode

**Decision**: `ProtocolVersion.Current` becomes `0.7`; a 0.6 adapter is refused at attach.

**Rationale**: §18 gives a protocol no patch level and makes a minor breaking while the major is zero, and the
refusal is what already happens — `BrokerLink` attaches with its version and the broker refuses a mismatch. A second
implementation would observe the change, which is §18's own test.

**Rejected**: accepting 0.6 Requests from old adapters. Two shapes admitted at once is the compatibility mode the
version exists to avoid, and the deployment upgrades its tools together (one installer).

## R7 — What storage still holds, and what it stops holding

**Decision**: keep the cap, `json_valid`, a Result belonging to an open exchange its sender received, sources naming
registered agents, `blocked` carrying a blocker, and the broker alone writing `closed`. Stop holding: a `done_when`
of twenty characters, an items array, item kinds, item ids and gates.

**Rationale**: Principle V. Every rule that is still meaningful stays in storage where application code cannot route
around it; the rules that describe a shape the protocol no longer has are removed rather than left to be satisfied by
code that fabricates fields.

## R8 — `MessageLimits.LongestItemId` stays, and `PayloadShape` keeps reading old shapes

**Decision**: keep the 64-character bound and `ItemIds`'s reading of `items` and `sub_questions`.

**Rationale**: both exist to read what is already stored. `result_items.item` is still bounded, and a 0.6 Request
opened today is still answered by its ids if its exchange is still open — which it may be, because nothing times out
(§14). Removing the reader would make an open 0.6 exchange unanswerable, which is exactly the defect the review gate
found in 0.5's own migration.

## R9 — The person's composer keeps its conversation field, and its suggestion

**Decision**: two fields — the conversation name (pre-filled with the existing suggestion, with the same character
rule applied as it is typed) and the ask.

**Rationale**: §5.0 already decides when a sender may name a conversation, and a person starting one always may. The
suggestion and the correction are 0.25.2's work and are unaffected.

## R10 — CLAUDE.md's "Surveying the peers" instruction is rewritten with everything else

**Decision**: the standing instruction that tells this agent how to ask its peers whether requests were worth the
work is rewritten as one ask.

**Rationale**: it is written as an intent, a `q1` and a `done_when`, and it is the example this repository's own
agent copies. A document that teaches the old shape is the slowest kind of drift: it produces new traffic in a shape
the protocol has removed.
