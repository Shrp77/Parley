# Feature Specification: Parley as facilitator

**Feature Branch**: `019-layout` (no branch of its own — the owner's call of 2026-09-28: 022 joins 019, 020 and
021 on one branch, under one combined review gate covering all four)
**Created**: 2026-09-28
**Status**: Draft
**Input**: User description: "Parley should always have a seat in every project, just so it can act as the
facilitator of conversations. Parley should ask every agent what they are responsible for. It should not be an
explicit call from the user."

## Why this exists

020 made an agent's ownership line something the agent is *asked* rather than something a person writes from
memory. The ask is a message, and a message is sent as somebody — so today it can only be sent by a person who
has taken a seat. The owner chose "ask every agent what it owns" and got:

> An ask is a message, so it is sent as somebody, and no seat is held here. Take one with 'j' in this page and
> ask again.

That is 020 working as designed and failing the person anyway. Every enrolled agent starts life with no
ownership line, peers route work by that line, and getting one needed a person to know to take a seat, know to
ask, and then accept every answer by hand. The owner's direction is that this is Parley's job, not theirs.

## Decisions already made

These were settled by the owner on 2026-09-28 and are not reopened here.

- **D1 — The facilitator lives in the broker.** It is always on, and it asks whether or not any surface is open.
  An agent enrolled from the command line overnight is asked before anybody looks.
- **D2 — Auto Accept, "just like Auto Mode in Claude".** With it on, an agent's answer becomes its ownership
  line with no review, "because then the system just flows". The owner prefers this. With it off, answers wait
  for a person, as 020 designed.
- **D3 — Ownership is inspected and changed through a function of its own**, so a line accepted automatically
  can be checked and corrected at any time, and a person can see which lines are an agent's own words and which
  a person wrote or edited.
- **D4 — The facilitator is never "the person seated".** Principle 2.6 sends an agent's unanswerable questions
  to the person seated in the deployment, or back to the originator when nobody is. The facilitator does not
  receive those. It only facilitates.

## What this does not decide, and where it is argued

These are tensions with standing constraints. Each is argued rather than stepped around, and each has a
requirement below that holds the line.

- **"No agent has authority over another. There is no orchestrator and no lead."** The facilitator asks a
  question, which principle 2.6 lets any participant do. It does not assign, direct, prioritise or override
  anybody's work, and it cannot: it has no operation an ordinary participant does not have, and none of the
  operator's. What it adds is *who* asks, not what asking can do.
- **A seat is a person (SPEC.md §17 question 2).** A seat exists so a person can hold a position across turns
  and speak as somebody. The facilitator is neither a person nor an agent, so it is not a seat. SPEC.md gains a
  third kind of participant and says what it may and may not do.
- **An ownership line was made not self-declared, and then asked-and-accepted (SPEC.md §17 question 10).** Auto
  Accept removes the person from that loop. **That is a reduction in a control, and it is recorded as one**, on
  the terms the repository already uses for its other reductions (the ownership bound in 020, the pair cap in
  006). What stands in its place is provenance that says truthfully who decided each line, and a function that
  lets a person review and correct any line at any time.
- **The broker has been forged as a sender before.** A forged `parley:broker` responder was a past review
  finding. The facilitator's identity is in the same reserved namespace, which no agent may claim, and a surface
  shows it differently from any agent's.

## Clarifications

### Session 2026-09-28

- Q: When are agents that already exist asked? → A: Any attached agent with no accepted line and no open or
  waiting question is asked — when it registers, when it reattaches, and once when the broker starts. So after an
  upgrade every running agent without a line is asked shortly after, and an idle agent is never started just to
  be asked.
- Q: Where does the Auto Accept setting live, and what is its default? → A: Per project, on by default. Each
  project's own settings hold its switch.
- Q: What happens when an agent asks the facilitator a question back instead of answering? → A: It is flagged to
  the person, who replies from the ownership review; the facilitator sends that reply, and the transcript marks
  it as written by a person.
- Q: How does an auto-accepted line reach peers, given peers read it only from the launch environment at start?
  → A: For an agent's first auto-accepted line only, the broker restarts the agent once it is idle (no open work
  and no turn in progress). Every later change waits for a natural restart.
- Q (from `/speckit-analyze`): Is an agent whose answer was discarded asked again automatically? → A: No. Having
  been asked at all stops automatic asking; only "ask again" asks again. This narrows the first answer above,
  which read "no open or waiting question".

## User Scenarios & Testing *(mandatory)*

### User Story 1 — Every new agent is asked what it owns, with nobody asking (Priority: P1)

A person enrols four repositories. Each agent starts and registers. Without anybody doing anything else, each
agent receives one question from Parley asking what it is responsible for and what it is not, and answers it.
With Auto Accept on, each answer becomes that agent's ownership line, and peers read it the next time the agent
starts.

**Why this priority**: This is the whole of the owner's request. Without it every agent's ownership line starts
empty and stays empty until a person knows to go and fix it.

**Independent Test**: Enrol one repository into a project with Auto Accept on, with no surface open. The agent is
asked once, answers, and its accepted line matches its answer, with its provenance recorded as accepted
automatically.

**Acceptance Scenarios**:

1. **Given** a project with Auto Accept on, **When** an agent registers for the first time, **Then** Parley asks
   it what it owns, once, in a conversation of its own, without any person acting.
2. **Given** that agent answers, **When** the answer arrives, **Then** it becomes the agent's accepted ownership
   line, recorded as accepted automatically, and is written to the agent's launch configuration.
3. **Given** that line was the agent's first, and the broker started the agent, **When** the agent next has no
   open work and no turn in progress, **Then** it is restarted once, and from then on peers read its line.
4. **Given** an agent that already has an accepted ownership line, **When** it registers again or restarts,
   **Then** it is not asked again.
5. **Given** an agent that has been asked and has not yet answered, **When** it registers again, **Then** it is
   not asked a second time; the first question is still open.
6. **Given** a person's seat and a retired agent, **When** either registers, **Then** neither is asked.
7. **Given** an agent that was running with no accepted line before this feature shipped, **When** the broker
   starts serving its project, **Then** the agent is asked, without having to restart.
8. **Given** an agent with no accepted line that is not running, **When** the broker starts, **Then** it is not
   started just to be asked; it is asked when it next attaches.

---

### User Story 2 — A person can see and correct what every agent owns (Priority: P1)

A person opens a project and chooses to review ownership. They see every agent, its current ownership line, and
where that line came from: the agent's own words accepted automatically, the agent's words accepted by a person,
a person's edit of the agent's words, or a line a person wrote. They can change any line, and they can have
Parley ask an agent again.

**Why this priority**: Auto Accept is only safe to leave on if its result can be checked. D3 is the owner's
condition for D2.

**Independent Test**: With two agents whose lines were accepted automatically, open the ownership review, edit
one line, and confirm the edited line is recorded as a person's edit while the other still reads as accepted
automatically.

**Acceptance Scenarios**:

1. **Given** agents with ownership lines from different routes, **When** a person opens the ownership review,
   **Then** each line is shown with its provenance, and a line accepted automatically is visibly distinct from
   one a person accepted, edited or wrote.
2. **Given** an agent's line, **When** a person edits it and accepts the edit, **Then** it is recorded as edited
   by a person and written to the agent's launch configuration.
3. **Given** an agent, **When** a person asks for it to be asked again, **Then** Parley asks it — no seat is
   needed to do so — and the new answer is accepted or held according to the project's Auto Accept setting.
4. **Given** the ownership review, **When** it is opened from either surface, **Then** it offers the same
   information and the same actions (principle 2.9).

---

### User Story 3 — With Auto Accept off, answers wait for a person (Priority: P2)

A person who wants to approve every line turns Auto Accept off for a project. Agents are still asked
automatically, but their answers wait. The ownership review shows which answers are waiting, and the person
accepts, edits or discards each.

**Why this priority**: The owner prefers Auto Accept, and this is the setting that preserves 020's design for a
deployment that wants it.

**Independent Test**: Turn Auto Accept off, enrol one repository, and confirm the answer arrives, is shown as
waiting, is not written to the launch configuration until a person accepts it, and is recorded as accepted by a
person when they do.

**Acceptance Scenarios**:

1. **Given** Auto Accept off, **When** an agent answers Parley's question, **Then** nothing is written and the
   answer is shown as waiting in the ownership review.
2. **Given** a waiting answer, **When** a person accepts it unchanged, **Then** it is recorded as the agent's
   words accepted by a person.
3. **Given** a waiting answer, **When** a person discards it, **Then** nothing is written and the agent keeps
   whatever line it had.
4. **Given** Auto Accept is turned on while answers are waiting, **When** the setting changes, **Then** answers
   already waiting stay waiting; the setting governs answers that arrive afterwards.

---

### User Story 4 — The facilitator is visibly not an agent and not a person (Priority: P2)

Anybody reading a conversation Parley started can tell at a glance that the question came from Parley itself,
not from an agent or a person. No agent can register under Parley's name or send as it.

**Why this priority**: A message that looked like it came from a peer, or a peer able to send as Parley, would
turn a facilitator into a way to put words in the system's mouth. The broker has been forged as a sender before.

**Independent Test**: Try to register an agent under the facilitator's name and try to send a message claiming
to be it; both are refused. Open a conversation Parley started; its sender is shown as Parley, distinct from
every agent and seat.

**Acceptance Scenarios**:

1. **Given** an agent attaching, **When** it claims the facilitator's name, **Then** it is refused.
2. **Given** any participant, **When** it tries to send a message as the facilitator, **Then** it is refused.
3. **Given** a transcript or roster, **When** the facilitator appears in it, **Then** it is labelled as Parley
   and is not listed among the agents or the seats.

---

### Edge Cases

- **The agent asks back instead of answering.** Parley cannot answer a clarification itself. The question is
  written to be answerable without one; when an agent clarifies anyway, the question is flagged to the person
  (principle 2.8) and shown in the ownership review, where the person writes the reply. The facilitator sends
  it, because the exchange is the facilitator's, and the transcript marks it as a person's words — so a reader
  never mistakes a person's reply for Parley's own. No seat is needed. The agent's eventual answer is still the
  agent's words, whatever the person wrote in between.
- **The agent never answers.** Nothing times out (principle 2.8). The question stays open, is flagged to the
  person, and the agent is not asked again while it is open.
- **The agent's answer does not fit an ownership line** (too long for a configured bound, or containing
  characters an ownership line refuses). It is never written. It is held as waiting, whatever Auto Accept says,
  and the review says why.
- **The agent is on another machine.** Its launch configuration is a file on that machine, which the broker
  cannot write (the rule 020 applies to every surface: no surface writes for an agent whose machine is not this
  one). The answer is recorded and held as waiting whatever Auto Accept says, and the review says why: it is
  accepted from the ownership review on that agent's own machine, which can write the file. Nothing here
  invents a way for a surface to apply lines on its own later — that would be a writer nobody watched.
- **The agent is running when its line is accepted.** Peers read an ownership line from the agent's launch
  environment, so the new line reaches them the next time the agent starts. **For its first line accepted
  automatically, the agent is restarted once it is idle** — no open work, no turn in progress — because the
  question is asked just after it first attaches, when it has learned almost nothing, and a restart then costs
  little while leaving peers without the line would cost the whole point of asking. **Every later change waits**
  for a natural restart: by then the agent may hold what it has learned (SPEC.md §12), and throwing that away to
  update one sentence is the wrong trade. The review says a line is pending until the agent carries it, as 020
  already does.
- **The agent never becomes idle.** The restart waits; it is never forced mid-work. The line stays pending, and
  the review says so.
- **Two projects, one repository.** Each project asks its own agent in its own conversation; accepting one writes
  that project's launch entry only.
- **The broker restarts while a question is open.** The question is part of the durable record and is still open
  afterwards; nothing is asked twice.
- **An agent is purged and the repository enrolled again.** It is a new registration with no accepted line, so
  it is asked.

## Requirements *(mandatory)*

### Functional Requirements

**The facilitator**

- **FR-001**: Every project MUST have a facilitator participant, provided by the broker, present whenever the
  project is being served. No person or surface needs to be running for it to act.
- **FR-002**: The facilitator's identity MUST be in a reserved namespace that no agent or seat may register
  under, and every attempt to register or send under it MUST be refused.
- **FR-003**: The facilitator MUST be able to send a question (a Request), receive its answer, send a reply to
  a clarification that a person wrote, and propose and agree that its own ownership conversation is finished once the
  question in it is settled. It MUST NOT have any other participant operation, or any operator operation, and
  MUST NOT assign, direct, prioritise, close or override anybody's work. (The agreement is needed because a
  conversation ends by consensus of its participants, principle 2.8, and a participant that never agrees would
  hold every ownership conversation open for ever — found in planning, 2026-09-28.) An open question from it
  MUST NOT change which conversation an agent's own Requests are assigned to, and no participant may send it a
  Request (found in analysis, 2026-09-28).
- **FR-003a**: A reply the facilitator sends on a person's behalf MUST be recorded and shown as written by a
  person, never as the facilitator's own words.
- **FR-004**: The facilitator MUST NOT count as the person seated in the deployment for principle 2.6
  escalation. An agent's questions that need a person MUST go where they go today.
- **FR-005**: Surfaces MUST show the facilitator as Parley, distinct from agents and seats, and MUST NOT list it
  among the agents or the seats.

**Asking**

- **FR-006**: The facilitator MUST ask an active, non-retired agent that is not a seat what it owns whenever that
  agent is attached, has no accepted ownership line, and has never been asked by the facilitator — checked when
  the agent registers, when it reattaches, and once when the broker starts serving the project.
- **FR-006a**: The facilitator MUST NOT start an agent in order to ask it. An agent that is not attached is asked
  the next time it attaches.
- **FR-007**: The question MUST be the one 020 defined (what the agent is responsible for, and what it is not),
  sent as one ordinary Request in a conversation recognisable as an ownership discussion by its identifier, and
  never by a marker in the payload.
- **FR-008**: The facilitator MUST NOT ask an agent more than once while a question to it is open or its answer
  is waiting, and MUST NOT ask an agent that has an accepted line — unless a person asks for it to be asked
  again.
- **FR-009**: A person MUST be able to have the facilitator ask any agent again, from the ownership review,
  without holding a seat.

**Accepting**

- **FR-010**: Each project MUST have its own Auto Accept setting, held by that project's broker, on when the
  project is created, which a person can turn on and off from either surface. A project created before this
  feature shipped MUST read as on. (Held by the broker rather than in the project's configuration file because
  the web UI may not write that file, and the setting has to be changeable from both surfaces — planning,
  2026-09-28.)
- **FR-011**: With Auto Accept on, an agent's answer MUST become its accepted ownership line without a person,
  provided it is a valid ownership line and the agent's machine is this one.
- **FR-012**: With Auto Accept off, or when an answer is not a valid ownership line, or when the agent's machine
  is not this one, the answer MUST be held as waiting and MUST NOT be written.
- **FR-013**: Every accepted line MUST record its provenance truthfully, with **accepted automatically** as a
  route distinct from the three 020 records (the agent's words accepted by a person, a person's edit of them, a
  line a person wrote).
- **FR-014**: Accepting a line, by any route, MUST write it where 020 writes it and in the same order (the launch
  configuration first, then the record), so a record never claims a line the agent will not carry.
- **FR-015**: Changing the Auto Accept setting MUST NOT change answers already waiting.
- **FR-015a**: When an agent's first ownership line is accepted automatically, the broker MUST restart that agent
  once it is idle — no open exchange it owes work on and no turn in progress — so peers read the line. It MUST
  NOT restart an agent mid-work, MUST NOT restart for any later change of line, and MUST NOT restart an agent it
  did not start. This is the broker keeping its own Auto Accept promise, not an operation the facilitator holds:
  the facilitator gains nothing by it that FR-003 forbids.

**Reviewing**

- **FR-016**: Both surfaces MUST offer an ownership review for a project that lists every agent, its current
  line, the line peers read now, the provenance of each, and any answer waiting.
- **FR-017**: From the review, a person MUST be able to accept, edit, write or discard a line, ask an agent again,
  and reply to an agent that asked the facilitator a question back — without holding a seat.
- **FR-018**: The review MUST make lines accepted automatically visually distinct from lines a person decided.

**Protocol and record**

- **FR-019**: SPEC.md MUST describe the facilitator as a third kind of participant, beside agents and seats, and
  say what it may and may not do, how its identity is protected, and that it is not the person seated.
- **FR-020**: SPEC.md MUST record Auto Accept as a reduction in the control §17 question 10 describes, with what
  replaces it (provenance and review), in the terms the document uses for its other reductions.
- **FR-021**: SPEC.md §18 MUST record the change, because a second implementation can observe the broker sending
  a Request. It is recorded as a further amendment to 0.7 rather than a new version: 0.7 has never been released,
  and §18's own precedent (0.6, and 0.7's 2026-09-26 amendment) is to fold an unreleased change in rather than
  spend a number on it. (Corrected in planning, 2026-09-28; the spec first said the version must move.)

### Key Entities

- **Facilitator**: Parley's own participant in a project. Has an identity in the reserved namespace, can send a
  question and receive its answer, holds no seat, runs no agent, and has no authority.
- **Ownership question**: One Request from the facilitator to one agent, in an ownership conversation. Open,
  answered, or waiting on a clarification.
- **Ownership answer**: What an agent said in reply, before it becomes a line. Accepted, waiting, or discarded.
- **Accepted ownership line**: The line recorded as the agent's, with its provenance — accepted automatically,
  the agent's words accepted by a person, a person's edit of the agent's words, or a line a person wrote.
- **Auto Accept setting**: A per-project choice of whether answers become lines without a person.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: After enrolling any number of repositories into a project with Auto Accept on, every agent that
  answers has an accepted ownership line with no further action by the person.
- **SC-002**: No agent is asked what it owns more than once unless a person asks for it again, measured across
  restarts, re-registrations and broker restarts.
- **SC-003**: A person can find every agent's ownership line and where it came from in one place, from either
  surface, and correct any of them in under a minute.
- **SC-004**: Every attempt by an agent to register as, or send as, the facilitator is refused.
- **SC-005**: No question an agent could not resolve reaches the facilitator instead of a person.
- **SC-006**: The cost is one turn per agent the first time it registers, and no turn at all for any agent that
  already has an accepted line.
- **SC-007**: With Auto Accept on, peers can read a newly enrolled agent's ownership line as soon as that agent
  is next idle, with no action by the person, and no agent is ever restarted while it has work open or a turn
  in progress.

## Assumptions

- **Auto Accept is per project and on by default** (Clarifications, 2026-09-28), because the owner prefers it and
  it is the setting that makes enrolment flow. A project that wants every line approved turns it off.
- **Asking costs the agent one turn**, the first time it registers. That turn is spent on the agent's own model,
  which is the cost 020 already accepted for an ask, now spent without a person choosing to spend it. Said here
  so it is a decision rather than a surprise.
- **Agents registered before this feature ships** are asked like new ones, without waiting for a restart: the
  broker checks every attached agent once when it starts serving, and each one with no accepted line is asked
  (Clarifications, 2026-09-28).
- **"Facilitator of conversations" is broader than ownership**, and this feature delivers only the ownership ask
  and the identity it needs. Any other facilitation — opening conversations, chasing stalled work — is out of
  scope, and would be argued against the no-orchestrator constraint on its own terms.
- **The terminal's and web UI's existing manual ask** (020's "ask what this agent owns" and "ask every agent")
  becomes "ask again", sent by the facilitator rather than a seat.
- **No new bound is placed on ownership lines.** 020 removed the default bound; an answer is held rather than
  written only when it breaks a bound a deployment has chosen or the character rules every line obeys.
