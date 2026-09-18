# Feature Specification: A Request is the ask

**Feature Branch**: written on `015-file-store`, which carries uncommitted work; the branch this ships on is the owner's call
**Created**: 2026-09-17
**Status**: Draft
**Input**: The owner's call after composing a Request in the terminal: "There are too many fields that are too closely related… We should end up with 2 fields: 1) name of the conversation, 2) the ask."

## Why

A Request carries four things beyond its recipient today: an intent, one or more items each marked `work` or
`question` and each with an id, optional gates between those items, and a `done_when` of at least twenty characters
that storage refuses a Request without. The owner met all of them in one form and read them as one thing asked four
times:

- **`work` and `question` are not two kinds.** A receiver that reads its own repository and answers has done work;
  so has one that changes a file. The distinction decides nothing — both open an exchange, both are carried to an
  outcome, and both are closed by a Result.
- **The intent and the ask say the same thing.** "Why you want this" and "what you want done" are one sentence in a
  person's head, and splitting them makes the composer ask twice for one answer.
- **`done_when` is that sentence a third time.** Where it genuinely adds something — how far the work goes — the
  protocol already has the move: the receiver asks, with a Clarify (§5.2), and may ask other agents too. A receiver
  that cannot tell when to stop is a receiver with a question.

The point of Parley is agents working with each other as people do. A form that demands three restatements before a
sentence can be sent is not how people ask, and it is the first thing anybody using the surfaces meets.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A person asks an agent something (Priority: P1)

A person seated in a project opens the composer, names the conversation and writes the ask. Nothing else is asked
of them. The receiving agent gets the ask, and answers it or asks about it.

**Why this priority**: it is the report this feature comes from, and the shortest path to the whole value.

**Independent Test**: take a seat in the terminal, start a conversation with one sentence, and read the receiver's
Result in the transcript.

**Acceptance Scenarios**:

1. **Given** a seated person in a project with a registered agent, **When** they start a conversation with a
   conversation name and an ask, **Then** the Request is sent, the exchange is open, and no other field was asked for.
2. **Given** that Request, **When** the receiver answers it, **Then** the Result closes the exchange whole, and the
   transcript shows the ask and the answer with nothing else between them.
3. **Given** an ask the receiver finds unclear, **When** it sends a Clarify, **Then** the person answers in the same
   exchange and the exchange stays open.

---

### User Story 2 - An agent asks another agent (Priority: P1)

An agent calls `parley_request` with a recipient and an ask. It states no intent, no items, no ids and no
`done_when`, because there are none.

**Why this priority**: every agent-to-agent conversation runs through this, and an adapter still asking for removed
fields would keep the old shape alive whatever the surfaces do.

**Independent Test**: from a repository with the adapter, send a request with two arguments and watch the receiver's
Result arrive in the inbox.

**Acceptance Scenarios**:

1. **Given** an attached agent holding no requests, **When** it sends a Request naming only a recipient and an ask,
   **Then** the broker assigns the conversation and returns the exchange identifier.
2. **Given** an attached agent holding requests from one conversation, **When** it sends a Request naming only a
   recipient and an ask, **Then** the Request joins that conversation, exactly as §5.0 already decides.
3. **Given** a receiver with the answer, **When** it sends a Result with one outcome, its text and its sources,
   **Then** the exchange closes and the requester collects it with the Request attached.

---

### User Story 3 - A conversation written under 0.5 or 0.6 still reads (Priority: P2)

A person opens a conversation from before the change. Its Requests carry an intent, items and a `done_when`; its
Results carry an outcome per item.

**Why this priority**: the deployment this was reported from holds such conversations, and a transcript that dropped
half of a stored message would be a worse defect than the one being fixed.

**Independent Test**: open a conversation stored under 0.6 in the terminal, the web console, and `parley export`.

**Acceptance Scenarios**:

1. **Given** a stored 0.6 Request, **When** it is rendered in a transcript or an export, **Then** its intent, items
   and `done_when` are all shown, marked as the sender's words as they are today.
2. **Given** a stored 0.6 Result with several outcomes, **When** it is rendered, **Then** every outcome is shown
   against the item it answered.
3. **Given** a store written under 0.6, **When** the broker migrates it, **Then** no stored message is rewritten and
   nothing is lost.

---

### Edge Cases

- **An empty ask** is refused, as an empty item is today: there is nothing to answer.
- **An ask over the cap** (rule 8.4, 8,000 characters) is refused with the existing refusal and counted in
  `parley refusals`, unchanged.
- **A Result on an exchange whose Request was written under 0.6** answers it whole, under the same single-item
  spelling a person's close already uses; the stored 0.6 items remain visible in the transcript.
- **An agent built against 0.6** attaches to a 0.7 broker and is refused at attach, as §18 requires — this is a minor
  version, and an adapter is upgraded with its broker.
- **A conversation with several open exchanges** is unaffected: the change is to what one Request carries, not to how
  many there may be.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: A Request MUST carry exactly one ask, as text, and the conversation it belongs to; it MUST NOT carry an
  intent, items, item ids, item kinds, gates or a `done_when`.
- **FR-002**: The conversation a Request belongs to MUST be decided exactly as it is now (§5.0): the broker assigns
  it, a sender holding requests in one conversation names nothing, a sender holding several names one, and only a
  sender holding none, or a person, may start a new one.
- **FR-003**: An ask MUST be non-empty and MUST be bounded by the existing message cap; the refusal and its recording
  MUST be the ones that exist today.
- **FR-004**: A Result MUST close the exchange whole, carrying one outcome from the existing set, the text of the
  answer, the sources it relied on, and a blocker when the outcome is `blocked`.
- **FR-005**: The outcome set MUST stay as it is — `done`, `answered`, `acknowledged`, `blocked`, `closed` — and
  `closed` MUST remain the broker's alone.
- **FR-006**: Clarify MUST be unchanged, and MUST be the documented way a receiver settles an ask it finds unclear or
  unbounded, including by asking other agents.
- **FR-007**: The rules of §8 that tell a sender to state an intent and a finish line MUST be replaced by what a
  sender can act on under this shape: say what you want, in a sentence a receiver could act on; ask when you do not
  know how far it goes.
- **FR-008**: Storage MUST stop requiring an intent, a `done_when` or per-item ids of a new message, and MUST keep
  every rule that is still meaningful: the cap, that a Result belongs to an open exchange the sender received, that
  sources name registered participants, and that a blocked Result names a blocker.
- **FR-009**: Messages written under 0.5 and 0.6 MUST be kept exactly as they were stored, and MUST render whole in a
  transcript, in the web console, and in an export.
- **FR-010**: Every composer MUST ask for two things and no more: the terminal's, the web console's, and the agents'
  `parley_request`. Every description, placeholder and standing instruction that names a removed field MUST be gone.
- **FR-011**: `parley_result` MUST take one outcome, its text, its sources and its blocker, and MUST NOT ask for an
  item id.
- **FR-012**: The protocol version MUST become 0.7, and an adapter that speaks 0.6 MUST be refused at attach, as §18
  already requires.
- **FR-013**: Every document that shows the shape of a Request MUST be brought into line: SPEC.md, the README, the
  plugin's commands and skill, and CLAUDE.md's own "Surveying the peers" instruction.

### Key Entities

- **Request**: an ask, in one conversation, from one participant to one other. Opens an exchange.
- **Result**: one outcome, its text, its sources, and a blocker when blocked. Closes the exchange it answers.
- **Clarify**: unchanged; hands the turn back with a question or an answer, and is how an unclear ask is settled.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A person starts a conversation by filling two fields, and a request that would have needed four
  statements is sent in one.
- **SC-002**: An agent sends a Request with a recipient and an ask, and nothing in its tool set names an intent, an
  item, a kind, a gate or a `done_when`.
- **SC-003**: A conversation stored under 0.6 renders with every field it was stored with, on all three surfaces.
- **SC-004**: No surface, document, tool description or standing instruction asks for a field this removes; a search
  of the repository for the removed names finds only migration code, history in SPEC.md §18, and the record of the
  change.
- **SC-005**: A Request and its Result travel between two agents over a real broker with no field beyond the ask and
  the answer, and the exchange closes.

## Assumptions

- **The removed fields are not replaced by anything.** Nothing new is added to carry what they carried; Clarify is
  where a receiver goes when it needs more.
- **A Result keeps the `request` spelling** the protocol already uses for answering a Request as a whole, so the
  stored shape gains no new vocabulary.
- **Stored history is not rewritten.** The migration stops applying the old requirements to new messages and leaves
  every stored message as it is, which is what keeps 0.6 conversations readable.
- **Group requests to a role (§5.5) are unchanged**: one ask opens one exchange per registered agent under the role.
- **The 8,000 character cap is unchanged**, and stays where it is (in storage) for the reason the constitution gives.
- **This is a protocol change, so the version moves and adapters are upgraded with the broker.** There is no
  compatibility mode: §18 already says a minor version is breaking while the major is zero.

## Out of scope

- Any change to Clarify, to closure and consensus (§5.0), to files (§9.5), to the operator's operations (§10.2), or
  to how an agent is woken.
- Any new field, and any optional survival of the removed ones — the owner's decision was to remove them, and a
  field that survives as optional is the same form with a longer description.
