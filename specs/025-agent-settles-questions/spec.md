# Feature Specification: The person talks; the agent decides what it settled

**Feature Branch**: `019-layout` (no branch of its own; 019–024 live there too, the owner's arrangement)
**Created**: 2026-10-05
**Status**: Draft
**Input**: User description: "The person's reply goes to the agent, and the agent decides what it settles. […] the
composer binds a typed line to one of them — whichever edge the wait-for graph happens to list first — and sends it as
that Request's Result. […] (1) a person's line to an agent always goes as a new Request, so the agent must reply […]
(2) when the agent replies, it may name which of its own open Requests to that person the person's line settled, and the
broker closes them […] (3) the terminal room's 'waiting on you' group shows each agent once with a count […] Enter on
that row opens a list of those questions; choosing one moves the transcript to that question […] The web UI must offer
the same. Also fix in passing: the registry reports the person seat as open_outside."

## Why

On 2026-10-05 an agent had asked the person five questions over three days, all in one conversation. The person read the
newest one and typed a reply that was itself a question. The terminal had to tie the line to one of the five, and it took
whichever the broker happened to list first — a question about something else entirely, two days old and
unrelated. It sent the line as that question's answer. An answer closes a question and asks nothing back, so the agent
read it, owed nothing, and said nothing. The four questions it had really asked went on reading *waiting on you*, and the
person's actual question went unanswered.

Two things were wrong, and the owner's decisions fix both:

- **The surface guessed which question a line answers.** It cannot know. The agent may have asked something long ago
  while the person moved on to other things, and only the agent knows what its own questions were for. So the surface
  stops guessing: **everything the person types to an agent is a new request**, and the agent must reply to it.
- **Only the person could close a question the agent asked.** SPEC.md §5.3 gives a request's answer to its receiver
  alone. With the person no longer answering by guesswork, nothing would ever close the agent's questions. So **the agent
  may say which of its own questions to the person a line of theirs settled**, and the broker closes those, recorded as
  settled by the agent that asked them and pointing at the person's line.

And the person needs to see what is waiting on them without hunting through a long transcript: one row per agent with a
count, and a list of the questions that takes them to each one in its context.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A reply is never filed as the answer to the wrong question (Priority: P1)

The person, with several of an agent's questions waiting on them, types a line to that agent. The line reaches the agent
as a request the agent must reply to. No question of the agent's is closed by it, and the agent replies.

**Why this priority**: This is the defect that made a live agent go quiet and lost the person's question. Everything else
builds on the person's line arriving as something the agent owes a reply to.

**Independent Test**: Seat a person in a conversation where an agent has three requests open to them. Type a line to that
agent. Check that a new exchange opened with the agent owing it, that all three of the agent's requests are still open,
and that the agent's turn was asked for.

**Acceptance Scenarios**:

1. **Given** an agent has several requests waiting on the person, **When** the person types a line to that agent and sends
   it, **Then** it is sent as a new request from the person to the agent, and none of the agent's requests is answered or
   closed by it.
2. **Given** an agent has exactly one request waiting on the person, **When** the person types a line to it, **Then** it is
   still sent as a new request — one waiting question is not a reason to guess either.
3. **Given** the person had asked an agent something and the agent asked back (a clarification), **When** the person types
   a line to that agent, **Then** it is sent as a new request, and the earlier exchange is left as it was until the agent
   says the line settled it (User Story 2).
4. **Given** the line is addressed to several agents, **When** it is sent, **Then** each receives its own new request, as a
   line to one agent does.

---

### User Story 2 - The agent says which of its questions the person's line settled (Priority: P1)

The agent reads the person's request. Its reply may name any of its own open questions to the person that the line
settled. The broker closes each one, recorded as settled by the agent and pointing at the person's line. Those questions
stop reading *waiting on you*.

**Why this priority**: Without it, User Story 1 leaves every question an agent asks the person open for ever. The two
ship together.

**Owner's addition (2026-10-05)**: nothing can know whether the person answered a question or asked for more about it,
so the agent decides — and the person must be able to see what it decided, beside its reply, without trusting the
agent's words to say so.

**Independent Test**: An agent with two requests open to the person receives a request from the person. It replies naming
one of the two as settled. Check that the named one is closed, that the record says the agent settled it and names the
person's request, that the other is still open, and that the person's *waiting on you* count dropped by one.

**Acceptance Scenarios**:

1. **Given** an agent has requests open to the person and the person has sent it a request, **When** the agent replies to
   that request naming some of its own requests as settled, **Then** each named request is closed, recorded as settled by
   the agent, citing the person's request.
2. **Given** the person's own request to an agent is waiting on the person because the agent asked back, **When** the agent,
   replying to a later line from the person, names that exchange as settled, **Then** the turn on it returns to the agent,
   exactly as if the person had replied there, and the agent answers it as usual.
3. **Given** an agent names an exchange it may not settle — one it is not party to, one between it and another agent, one
   already closed, one in another conversation, or one where the person is not the one who owes the next move — **When** it
   replies, **Then** the whole reply is refused, nothing is stored, and the refusal names each exchange and why.
4. **Given** an agent names nothing as settled, **When** it replies, **Then** the reply is stored as before and every
   question stays open.
5. **Given** an agent receives a request from the person, **When** the request is delivered, **Then** the agent is told
   which of its own questions to the person are still open in that conversation, with enough of each to recognise it, so it
   can decide what the line settled without searching.

---

### User Story 3 - The person sees what is waiting on them, one row per agent (Priority: P2)

In the terminal's room, the *waiting on you* group lists each agent once, with how many of its questions are waiting on
the person. Pressing Enter on an agent's row opens a list of those questions. Choosing one moves the transcript to that
question, so the person reads it in its context.

**Why this priority**: The person can act without it, but at five questions in a long transcript, finding them is the
work. The owner asked for it by name.

**Independent Test**: In a conversation where one agent has three questions waiting on the person and another has one,
read the room: two rows, counts 3 and 1, no repeated "waiting for your reply". Press Enter on the first row, see three
questions, choose the second, and check the transcript shows that question.

**Acceptance Scenarios**:

1. **Given** an agent has questions waiting on the person, **When** the room is drawn, **Then** the *waiting on you* group
   shows that agent once, with the number of its questions waiting.
2. **Given** that row is selected, **When** the person presses Enter, **Then** a list opens with one entry per waiting
   question, oldest first, each showing when it was asked and the start of what was asked.
3. **Given** the list is open, **When** the person chooses a question, **Then** the list closes and the transcript shows
   that question, with the messages before it in view so its context can be read.
4. **Given** a question is settled while the list is open, **When** the room is next refreshed, **Then** the count drops and
   a stale entry chosen afterwards says it has been settled rather than moving the transcript somewhere misleading.

---

### User Story 4 - The web UI offers the same (Priority: P2)

The web UI's conversation page shows the same *waiting on you* list — each agent once with its count — and each question
links to its place in the transcript. A line the person writes there to an agent is a new request, as in the terminal.

**Why this priority**: The two surfaces must not disagree about what a person's line does or what is waiting on them.

**Independent Test**: The same conversation as User Story 3, on the web page: the same agents and counts, and following a
question's link shows that question.

**Acceptance Scenarios**:

1. **Given** the conversation in User Story 3, **When** the web page is read, **Then** it shows the same agents with the
   same counts as the terminal.
2. **Given** a question in that list, **When** the person follows it, **Then** the page shows that question in the
   transcript.

---

### User Story 5 - The person is never shown as open outside Parley (Priority: P3)

Only an agent a node could start can be *open outside Parley* (SPEC.md §11.7). The registry stops reporting the person
seat that way.

**Why this priority**: Found while investigating; nothing acts on it today, but a surface that one day reads the flag for
every row would mark the person with ◇.

**Independent Test**: Read the registry while the person is seated: the person's row does not say open outside Parley.

**Acceptance Scenarios**:

1. **Given** a person is seated, **When** the registry is read, **Then** the person's row is not open outside Parley.
2. **Given** Parley's facilitator, **When** the registry is read, **Then** its row is not open outside Parley either.

### Edge Cases

- **The agent names the same exchange twice** in one reply: it is settled once; naming it twice is not an error.
- **Two replies race to settle the same question**: the first closes it; the second is refused for that exchange as
  already closed, and nothing in it is stored.
- **The agent settles a question in a reply that is itself a clarification** of the person's request: allowed. The agent
  may need to ask the person something back and also say an earlier question is now answered.
- **The person's request is to several agents** and one of them settles its questions: only that agent's questions close.
- **An agent's question was asked in a different conversation** from the person's line: it cannot be settled from this
  one. Settling cites a request in the same conversation, so the record can be read in one place.
- **A question settled by the agent that the person still wanted to answer**: the person may still write to the agent;
  their line is a new request like any other.
- **Retention prunes the person's request** (§9.4) after a question was settled by it: the closed question keeps the record
  that it was settled by the agent and which exchange did it, even with the text gone.
- **The question list on a narrow terminal**: each entry is cut to the width with a mark that it was cut. The full question
  is read in the transcript, which is where the entry takes the person.
- **The person is away when the agent settles their questions**: the count they see on return is already lower; nothing
  is owed to them about it.

## Requirements *(mandatory)*

### Functional Requirements

**What the person's line is**

- **FR-001**: Every line the person types to an agent, on any surface, MUST be sent as a new request from the person to
  that agent, whatever is open between them.
- **FR-002**: No surface MUST ever choose, on the person's behalf, which open exchange a typed line answers or clarifies.
- **FR-003**: The preview of what a line will do MUST stop describing it as an answer or a clarification; it says who the
  line goes to, as it does today.

**What the agent may settle**

- **FR-004**: When an agent sends a Result or a Clarify on a request the person opened with it, it MAY name exchanges that
  the person's request settled.
- **FR-005**: An exchange the agent may name is one in the same conversation, still open, between that agent and the
  person, on which the person owes the next move. Either the agent is its requester (its own question to the person), or
  the person is its requester and the agent asked back (a clarification awaiting the person).
- **FR-006**: For the agent's own request, the broker MUST close it as **settled by its requester**, recording the agent as
  the one who settled it and the exchange whose request settled it.
- **FR-007**: For the person's request awaiting the person after a clarification, the broker MUST return the turn to the
  agent, as the person's own reply there would have.
- **FR-008**: If any exchange named is not one the agent may settle, the broker MUST refuse the whole reply before storing
  anything, and the refusal MUST name each such exchange and why.
- **FR-009**: Settling is the only thing a requester may do to close its own request, and only for requests to the person.
  Between agents, a request is still closed by its receiver's Result alone.
- **FR-010**: Storage MUST hold the settling rules itself (SPEC.md §9.3): an exchange closed as settled must name a settling
  exchange in the same conversation, opened by the person with the same agent; and only the requester's own outcome may be
  *settled*.
- **FR-011**: A request from the person, as delivered to an agent, MUST tell the agent which of its own questions to the
  person are open in that conversation — each one's exchange and the start of what was asked — and that it may name any its
  reply settles.
- **FR-012**: The agent's standing instructions and its reply tools MUST say what settling is for, that it is the agent's
  call, and that a question the person's line did not settle must be left open.

**What the person sees**

- **FR-013**: The terminal room's *waiting on you* group MUST show each agent at most once, with the number of its questions
  waiting on the person, and MUST NOT repeat the group's own words on each row.
- **FR-014**: Enter on an agent's row in that group MUST open a list of that agent's questions waiting on the person in the
  conversation, oldest first, each showing when it was asked and the start of the question.
- **FR-015**: Choosing a question in that list MUST move the transcript to it, with what came before it in view.
- **FR-016**: The web UI's conversation page MUST show the same agents with the same counts, and each question MUST lead to
  its place in the transcript.
- **FR-017**: A question settled by the agent MUST read on every surface as settled by that agent, not as answered by the
  person, wherever a closed exchange's outcome is shown.
- **FR-021**: Every surface MUST show, with the agent's reply that settled questions, which questions it settled (the
  start of each), taken from the broker's record and not from the agent's words, so the person sees what was settled in
  the same place they read the reply — even if the agent did not say so. The agent's instructions MUST also tell it to
  say in its reply which questions it settled and why.
- **FR-022**: Asking for more about a question is not answering it. The agent's instructions MUST say that a line from
  the person asking for clarification of a question leaves that question open: the agent answers what was asked and
  does not settle it.
- **FR-018**: Health and flag reports (SPEC.md §6) MUST stop counting a settled question as owed by the person.

**Housekeeping**

- **FR-019**: The registry MUST NOT report the person seat or the facilitator as open outside Parley.
- **FR-020**: The protocol version MUST move from 0.7 to 0.8 (§18), because an agent's adapter now carries a field and an
  outcome a 0.7 broker would not understand, and a person's line now arrives as a request where a 0.7 surface sent a Result.
  No compatibility with 0.7 adapters is kept (nothing is released).

### Key Entities

- **Settled question**: an agent's request to the person, closed because the agent said a later request from the person
  settled it. Carries who settled it (the agent), when, and which exchange's request settled it.
- **Settling request**: the person's request whose reply named the settled questions. It is an ordinary request; it is
  cited, not changed.
- **Waiting-on-you entry**: one agent, the number of its questions waiting on the person in a conversation, and the list of
  those questions (exchange, when asked, the start of the ask, and where it is in the transcript).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: With any number of an agent's questions waiting on the person, a line the person sends to that agent closes
  none of them, in 100% of cases, on both surfaces.
- **SC-002**: Every line the person sends to an agent produces a reply owed by that agent: none is recorded as an answer
  the agent owes nothing back for.
- **SC-003**: An agent can close any of its own questions to the person that a person's line settled in the same reply it
  sends to that line — no second step, and no action by the person.
- **SC-004**: From the room, the person reaches any question waiting on them in its transcript context with at most three
  keystrokes (select the agent, Enter, choose the question).
- **SC-005**: The terminal and the web UI show the same agents and counts for *waiting on you* in every conversation.
- **SC-006**: Re-running the 2026-10-05 conversation shape — five questions waiting, a reply to the newest — leaves all five
  open after the person's line, and the agent replies.
- **SC-008**: In 100% of cases where an agent settled questions, the person sees which ones beside that agent's reply on
  both surfaces, whatever the reply's own words say.
- **SC-007**: A real agent (this repository's own, or another live one) receiving a person's line that answers one of its
  earlier questions names that question as settled in its reply. Recorded in the ledger whichever way it goes, because a
  scripted agent cannot show it.

## Assumptions

- **The web UI's inbox keeps its explicit answer to a chosen request.** There the person picks the exact exchange they are
  answering, which is a choice rather than a guess, so the owner's rule — the surface never guesses — does not remove it.
  The box on both surfaces is what changes. If the owner wants the inbox to send requests too, it is a small change.
- **The terminal's question list moves the transcript and does not itself answer.** That is what the owner asked for; the
  person answers by typing to the agent, and the agent settles.
- **Lines to Parley's facilitator are unchanged.** It is answered through its own review (022) and is not offered as a
  recipient of the box.
- **Settling is limited to questions to the person.** Agents answer each other with Results as today. Widening it would
  let a requester close work another agent owes, which is a different question about authority (no agent has authority over
  another).
- **The schema is edited in place** (schema 1), as every unreleased change has been. A store from before needs a reset.
- **A clarification the person owes on their own request** is handled by the agent returning the turn to itself (FR-007),
  rather than by a new way for the person to answer it, because the person's line is always a request.
