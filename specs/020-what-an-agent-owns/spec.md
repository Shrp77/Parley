# Feature Specification: What an agent owns, asked rather than assumed

**Feature Branch**: `015-file-store` (this repository builds features on the working branch)
**Created**: 2026-09-22
**Status**: Draft
**Input**: User description: "Asking the user to tell the system what the agent owns is risky because the user may have forgotten what it is or what it was intended to own. Instead, add the ability in the UI to *query the agent*. The agent will then respond with what it thinks it owns. If the user thinks the agent is wrong it should *discuss* the change with the agent so that the agent is now on-board and agrees with the user. Who knows — maybe the *user* changes his/her mind and agrees with the agent!"

## The friction this starts from

An agent's `owns` line is how every other agent decides who to ask. It is one line, it is read once per
peer every time the roster is listed, and it is the only routing information the protocol offers.

**It is composed by the wrong party.** Today a person writes it, at enrolment, in a worksheet, and affirms
it with a checkbox. That assumes they remember what a repository was meant to own — which, on a machine
with nineteen of them, most of which they last opened months ago, they do not. The predictable outcome is
the one the setup guidance already warns about: the field gets left empty, and an empty field routes
nothing at all.

**And it cannot answer its own hardest half.** The guidance says so in as many words: *a repository can
show what it contains and cannot show what it has decided not to own; absence of tax code does not mean
tax is somebody else's.* A person guessing is no better placed than a tool guessing. The half that
actually stops a peer asking the wrong agent is the half nobody can derive.

**Feature 019 removes the worksheet**, which removes the place a new line was written. This is what
replaces it, and it replaces it with something better rather than equivalent: **ask the agent.**

## The decision this amends, and the argument for amending it

SPEC.md §17 question 10 is **closed**, and reads in part:

> It is not self-declared, and that is the whole of why it is worth reading. A field a model could write
> would be no more trustworthy than the prose it replaces, merely centralised.

**That is a real argument and this feature must answer it rather than step around it.** It is answered on
four grounds, and the honest cost is recorded below.

1. **Question 10 has already been amended on this exact axis.** Its own amendment permits a *draft* put in
   front of a person, on three conditions: the draft is carried beside its evidence; approval is a
   recorded positive act rather than a default; and the exclusion half is never invented. The settled
   question is therefore not *may a model's words reach the field* — a drafted line already may — but
   **whose draft, and checked against what**.
2. **The current draft source is weaker, and is already partly model-written.** It is the first
   substantial non-heading line of the repository's README plus a list of its top-level directories. In a
   repository an agent maintains, that README is in part prose the agent wrote. So "not self-declared" is
   already softer in practice than it reads, and the evidence a person checks against is thinner than the
   thing being proposed here.
3. **It is the only source that can answer condition 3 without guessing.** A draft may not invent the
   exclusion half. An agent need not invent it: it can say what it believes it does *not* own, and name
   who it thinks does — which turns the underivable half into a question for a named peer, and a check
   that can be run against that peer's own line.
4. **A discussion is a stronger form of condition 2 than a checkbox.** What the registry promises is that
   a person read the line and said yes. A checkbox can be ticked unread. A transcript in which somebody
   argued a proposal to a conclusion is the evidence that promise refers to, and it is durable and
   auditable where a boolean is not.

**What is claimed afterwards is narrower again, and this is the cost.** Question 10 already stepped back
from *the field was composed by a person* to *a person read it and said yes*. This steps back once more,
to **a person was shown a proposal, was free to argue with it, and agreed to what resulted**. The residual
risk question 10 accepted — anchoring — **gets worse, not better**: a fluent, reasoned proposal from a
counterparty that answers back anchors harder than a quoted README line. A person who does not engage has
rubber-stamped a self-declared charter, which is precisely the state question 10 says is not worth
reading. That risk is mitigated (FR-012, FR-013, FR-017) and is not eliminated.

**Nothing in the protocol changes.** The broker still receives one value from a launch environment, still
refuses it in the same schema, and still cannot tell how it was composed. No operation accepts an `owns`
line from an agent; §13 and §10.1 are untouched. As question 10 says of its own amendment, this binds the
tools that write launch configurations, which is where the guarantee always actually lived.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Ask an agent what it owns (Priority: P1)

An operator looking at an agent asks it what it owns. It answers in its own words. They read the answer
beside the agent's name and its current line, and either accept it or do not.

**Why this priority**: It is the whole of the idea in its smallest useful form, and it is independently
valuable with nothing else built — an operator who only ever accepts the first answer is already better
off than one facing a blank field.

**Independent Test**: With one enrolled and running agent, ask, read the answer, accept it, restart the
agent, and see the line in the roster.

**Acceptance Scenarios**:

1. **Given** a registered, running agent, **When** an operator asks what it owns, **Then** the agent's
   answer is shown, attributed to the agent, beside whatever line it currently has.
2. **Given** an answer on screen, **When** the operator accepts it, **Then** it is written to that
   repository's launch configuration and the operator is told it takes effect when the agent next starts.
3. **Given** an answer the operator does not accept, **When** they decline, **Then** nothing is written
   and the existing line is unchanged.
4. **Given** an agent that is not running or not registered, **When** an operator asks, **Then** they are
   told it cannot be asked and why, and nothing is written.
5. **Given** an agent that does not answer within the time allowed, **When** the operator looks, **Then**
   they are told it has not answered rather than that it declined.

---

### User Story 2 - Disagree, and settle it by discussing (Priority: P1)

The operator thinks the answer is wrong. They say so, in their own words, and the agent replies. This
continues until they agree — which may mean the agent revises, or may mean the operator does.

**Why this priority**: Equal to US1 because it is the half that makes the result trustworthy. Without it
the operator's only options are accept or reject, and rejecting leaves the field empty, which is the state
this feature exists to remove.

**Independent Test**: Ask, disagree with a reason, receive a revised answer, accept the revision, and find
the revised line in the launch configuration.

**Acceptance Scenarios**:

1. **Given** an answer on screen, **When** the operator replies disagreeing, **Then** the agent receives
   it as an ordinary message in the same conversation and may revise, may ask for clarification, or may
   argue its case.
2. **Given** a continuing discussion, **When** either party asks a question, **Then** it is carried as the
   protocol's ordinary clarification and does not close the exchange.
3. **Given** a discussion that reaches agreement, **When** the operator accepts, **Then** what is written
   is the agreed line and not the original proposal.
4. **Given** a discussion the operator abandons, **When** they leave, **Then** nothing is written and the
   conversation remains readable afterwards.
5. **Given** any accepted line, **When** an operator later asks how it was arrived at, **Then** the
   conversation that produced it can be found.

---

### User Story 3 - Ask everybody at once, after enrolling a machine (Priority: P2)

An operator has just enrolled nineteen repositories. Rather than opening each, they ask all of them what
they own, and work through the answers as they arrive.

**Why this priority**: It is where the feature earns its keep, and it is exactly the moment 019 leaves
every new agent with no line. Below the first two because it is those two repeated, and because it spends
real money.

**Independent Test**: Enrol several repositories, ask all of them, and confirm one exchange per agent and
one answer per agent.

**Acceptance Scenarios**:

1. **Given** several registered agents, **When** an operator asks all of them, **Then** each is asked
   separately and each answer is attributed to its agent.
2. **Given** that request, **When** it is offered, **Then** the operator is told how many agents it will
   ask and that each costs that agent a turn, before anything is sent.
3. **Given** answers arriving over time, **When** some agents have not answered, **Then** the ones that
   have can be acted on without waiting for the rest.
4. **Given** an agent that cannot be asked, **When** the others are asked, **Then** it is named and the
   rest proceed.

---

### User Story 4 - See the claim against its neighbours (Priority: P3)

When an operator reads a proposed line, they see the lines the other agents in that project hold. A claim
that overlaps another, or a gap neither claims, is visible at the moment of deciding.

**Why this priority**: It is the check that no previous source could perform, and it is what makes the
exclusion half actionable rather than merely honest. Third because the first three deliver value without
it.

**Independent Test**: With two agents holding overlapping lines, ask one and confirm the other's line is
shown beside the proposal.

**Acceptance Scenarios**:

1. **Given** a proposal on screen, **When** the operator reads it, **Then** the lines held by the other
   agents in that project are shown beside it.
2. **Given** an agent whose answer names another agent as owning something, **When** the operator reads
   it, **Then** they can go and ask that agent without losing the discussion they are in.
3. **Given** two agents claiming the same ground, **When** an operator looks, **Then** both claims are
   visible and neither is refused — the protocol arbitrates nothing here, and this surface does not either.

---

### Edge Cases

- **The agent proposes something far longer than a roster can afford.** The line is charged to every peer
  on every listing. It must be refused or trimmed at a point the operator sees, not silently truncated
  into something the agent did not say.
- **The agent proposes something that could break its own line** — a line separator, a
  direction override, an invisible or tag character. Refused outright. A roster entry that can break its
  line can imitate the entries around it, and every agent reads that list.
- **The agent declines to answer, or answers that it does not know.** A legitimate answer. Recorded as
  such, and the field left as it was.
- **The agent answers with something that is not a statement of ownership** — a question, an apology, a
  refusal. The operator sees what came back; nothing is written without their act.
- **Two operators ask the same agent at once.** Ordinary concurrent exchanges; the protocol already
  bounds this. What must not happen is two accepted lines racing into one launch configuration.
- **The agent's repository moved, or its launch configuration cannot be written.** Reported naming the
  file. Nothing partially written.
- **A line is accepted and the agent never restarts.** The registry keeps the old line, and the surface
  must say that what was accepted is not yet what peers read.
- **An agent is retired between being asked and being answered.** The answer is still readable; the write
  is refused, naming why.
- **The operator holds no seat.** Asking is sending a message, which needs one. Offered, rather than the
  action being hidden.

## Requirements *(mandatory)*

### Functional Requirements

**Asking**

- **FR-001**: An operator MUST be able to ask a registered agent what it owns, from the surfaces they
  already use to read a deployment.
- **FR-002**: The ask MUST be an ordinary message in the protocol, opening an ordinary exchange. No new
  operation, and nothing that bypasses what a message costs or how it is delivered.
- **FR-003**: The ask MUST tell the agent what the answer is for: that it is one line, read by every agent
  in the deployment whenever anyone lists the roster, that it costs each of them context every time, and
  that it is used to decide who to send work to.
- **FR-004**: The ask MUST request both halves — what the agent owns **and** what it does not — and MUST
  invite it to name who it believes owns what it does not.
- **FR-005**: An agent that is unregistered, retired or unreachable MUST be reported as unaskable, with
  the reason, and MUST NOT be asked.
- **FR-006**: An operator MUST be able to ask every agent in a project in one act, and that MUST be one
  ordinary exchange per agent rather than any new one-to-many operation.
- **FR-007**: Before asking more than one agent, the operator MUST be told how many will be asked and that
  each costs that agent a turn.

**Discussing**

- **FR-008**: An operator MUST be able to reply to an answer in their own words, in the same conversation.
- **FR-009**: Either party MUST be able to ask a question without closing the exchange.
- **FR-010**: What is written on acceptance MUST be the line as agreed at the end of the discussion, not
  the first proposal.
- **FR-011**: The conversation that produced an accepted line MUST remain readable afterwards and MUST be
  findable from the agent.

**Deciding**

- **FR-012**: Nothing MUST be written without a positive act by the operator. An answer arriving MUST NOT
  change anything by itself.
- **FR-013**: A proposed line MUST be shown as **the agent's words**, attributed, and distinguishable from
  text the surface wrote.
- **FR-014**: The operator MUST be shown the line the agent currently holds beside any proposal.
- **FR-015**: The operator MUST be able to edit a proposal before accepting it, and to write a line
  without asking at all.
- **FR-016**: The operator MUST be able to decline, leaving the existing line untouched.
- **FR-017**: The lines held by the other agents in the same project MUST be visible while deciding.

**Writing it**

- **FR-018**: An accepted line MUST be written to that repository's launch configuration, by the one
  component that already writes those files. **No operation may accept an `owns` line from the agent
  itself**, and nothing may write it into the registry directly.
- **FR-019**: On acceptance the operator MUST be told that it takes effect when the agent next starts, and
  MUST be offered the restart as part of the same act.
- **FR-020**: Until that restart, any surface showing the agent's line MUST make clear that what peers
  read is not yet what was accepted.
- **FR-021**: A launch configuration that cannot be written MUST be reported naming the file, and MUST NOT
  be left partly written.

**What the registry will accept**

- **FR-022**: The stored statement MUST go on refusing every character that could let a roster entry break
  its own line, hide itself, or reorder what follows. **This is not relaxed.**
- **FR-023**: The stored statement MUST no longer be bounded at one hundred characters.
- **FR-024**: The bound MUST remain expressible, so that a deployment may reinstate one, and MUST remain
  enforced in storage rather than only in the code above it.
- **FR-025**: A statement too long to be accepted MUST be refused where the operator can see it and act,
  and MUST NOT be silently shortened into something the agent did not say.
- **FR-026**: The lengths that agents actually propose MUST be observable, so that a bound can later be
  chosen from evidence rather than from judgement.

**The document**

- **FR-027**: SPEC.md §17 question 10 MUST be amended to record that a draft may come from the agent
  itself, on what conditions, and at what cost — including that the claim made by the field narrows again
  and that anchoring is worsened rather than relieved.
- **FR-028**: The schema in SPEC.md MUST move with the migration, so that the document and the storage
  cannot disagree.

**What must not change**

- **FR-029**: No agent-facing operation may set, propose or alter what any agent owns. The agent answers a
  message; a person decides; a tool writes a launch configuration.
- **FR-030**: The field MUST remain advisory. Nothing is routed by it, refused because of it, or compared
  against it, and two agents claiming one ground MUST NOT be arbitrated.
- **FR-031**: The field MUST remain optional. An agent with no line MUST go on being listed without one.

### Key Entities

- **Ownership statement**: One line per agent, in the registry, read by peers to choose a recipient.
  Sourced from the launch configuration, refreshed on every start, cleared when the launch stops supplying
  it.
- **The proposal**: What an agent says it owns, in answer to a question. Agent-written text, attributed,
  binding nothing until a person accepts it.
- **The discussion**: The conversation in which a proposal is agreed, revised or abandoned. The durable
  evidence that a person read the line and said yes.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: An operator can obtain an ownership line for an agent without composing one, and without
  opening that repository.
- **SC-002**: An operator who disagrees with a proposal can reach an agreed line without leaving the
  surface they are in.
- **SC-003**: After enrolling a machine's repositories, an operator can ask all of them in one act and is
  told the cost before it is paid.
- **SC-004**: Every accepted line can be traced to the conversation that produced it.
- **SC-005**: No line reaches the registry that an agent proposed and a person did not accept.
- **SC-006**: A statement containing any character able to break, hide or reorder a roster line is refused,
  at whatever length statements are allowed to be.
- **SC-007**: The lengths agents propose are visible, so the question of what bound to set can be answered
  from data.
- **SC-008**: An operator can tell, from the surface, whether the line peers currently read is the one that
  was last accepted.

## Assumptions

- **019 ships first.** It removes the worksheet this replaces, and its enrolment leaves agents registered
  and startable — which is what makes them askable at all. This feature assumes an agent can be reached.
- **The operator holds a seat.** Asking is sending a message, and a seat is how a person sends one. Where
  they hold none, the surfaces already know how to offer one.
- **Both surfaces get this.** The terminal and the web UI both bind every operator operation already, and
  a capability on one and not the other is the drift this repository has a test for.
- **The bulk ask reuses the existing plural ask**, which is defined as N ordinary exchanges rather than a
  one-to-many operation. Nothing new is added to the protocol for it.
- **A conversation is the record.** The existing approval flag disappears with the worksheet; what
  replaces it is a durable transcript, which is strictly more than a boolean ever carried.
- **The length bound is removed rather than raised**, on the ground that a cap censors the evidence that
  would justify it — the reasoning this repository used twice before, for the open-question cap and the
  clarification depth cap. Both kept the constraint and switched it off, and so does this.
- **Version and ordering are the plan's to settle**, except that this carries a migration and a change to
  the primary document, so it is not a patch.
