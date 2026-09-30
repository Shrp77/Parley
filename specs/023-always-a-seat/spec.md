# Feature Specification: The person always has a seat

**Feature Branch**: `019-layout` (no branch of its own, following 022. 019 to 022 share one branch and one
combined review gate. Whether 023 joins that gate is the owner's call and is not assumed here.)
**Created**: 2026-09-29
**Status**: Draft
**Input**: User description: "A project is only useful when a person has a seat - so by default we should always
take a seat on all projects and we'll call that 'human-user@[machinename]'." Refined in conversation the same
day: "There is a difference between having a seat and being in a seat ... the 20 minutes of no activity is a good
indicator that a person has vacated the seat, but that does not mean the seat should be removed. The seat is
still there for a reason - agents may actually need the person to arbitrate or make a decision and they are
stalled until that decision is made. They need to be able to ask the user what they want to do even if it's 7
hours into a marathon session."

## Why this exists

Today a person takes part in a project by joining under a name they type. The seat lasts while they are active
and is released after 20 minutes of inactivity. The rule behind that is recorded in CLAUDE.md: *a seat must not
outlive the person*, because a seat is a connection and SPEC.md §4.0 treats a connection as proof that someone
can answer.

That rule mixes up two different things:

- **Having a seat.** The person exists in the project. Agents can address them and their mail waits for them.
- **Being in the seat.** Someone is at a screen right now.

Releasing the seat when the person steps away removes the first because the second has stopped. The cost falls
on agents. Principle 2.6 tells an agent that cannot get information from another agent to ask the person
seated. If nobody is seated, it must hand the need back to whoever asked it. So when the person steps away for
20 minutes, every agent waiting on a decision stops waiting for the one participant who can make it.

There is a second defect under the first. When an agent sends a message to a person who is not at a screen, the
broker tries to wake them the way it wakes an agent: it asks a machine to start them. Nothing can start a
person, so the sender is told the message is **unreachable**. The message is in fact stored and waiting. But an
agent told "unreachable" behaves as if it will never be read.

## Decisions already made

These were settled by the owner on 2026-09-29 and are not reopened here.

- **D1: Having a seat and being in the seat are separate.** A person always has a seat. Whether they are in it
  is shown, and never removes the seat.
- **D2: The name is `human-user@<project>`**, for example `human-user@alpha`. `user` was considered and rejected
  because an agent could legitimately be called `user`. The name does not start with `parley-`, so it cannot be
  mistaken for the facilitator or for a launch key in `.mcp.json`. **The part after `@` is the project, not a
  machine**: a person is not hosted on a machine the way an agent is, so a machine there would say something
  untrue. See D7.
- **D3: `human-user` is a reserved role**, the way `parley-facilitator` is. Enrolment, attach and storage refuse it
  to anything that is not the person seat. See *What the reservation does not do*, below.
- **D4: Everyone using one project is the same `human-user`.** This is a deliberate choice, not an oversight.
  Parley is used by one person running many agents. Telling several people apart is not a goal.
- **D5: An agent can always ask the person.** A decision may be needed hours into a session. The agent waits for
  it rather than giving up.
- **D6: Mobile surfaces and push notifications are out of scope, and must not be ruled out.** They would be
  further ways to reach the same waiting mail. Nothing here may depend on the person being at a terminal or a
  browser on the broker's machine.
- **D7: The facilitator is renamed the same way**, from `parley-facilitator@<machine>` to
  `parley-facilitator@<project>`. Both participants the broker provides are named for the project they belong to,
  so the two are named by one rule. This reopens 022's built naming inside this feature, deliberately.

## What this changes in the recorded design

Each of these is argued rather than stepped around.

- **CLAUDE.md: "a seat must not outlive the person".** Replaced by: *a seat outlives the person's presence; what
  must not outlive it is the claim that they are present.* The reason for the old rule survives: a tab left open
  is not somebody at a desk. So the screen must not say *here* when nobody is. What changes is that the seat
  itself stays.
- **SPEC.md principle 2.6, §5 "Blocked", and rule 10 of §8.** "The person seated" now means the project's person
  seat, whether or not the person is at a screen. "If nobody is seated" now means the project has no person seat
  at all, which a served project never lacks (see the clarification below).
- **SPEC.md §17 question 2.** Its second open problem is closed. It said that a seat "makes `startable` read
  oddly ... a peer is told an absent human can be woken". A person is now shown as *here* or *away*, never as
  *startable*, and a message to them is never answered with *unreachable*.
- **SPEC.md §4.0 and §14: what a connection means.** For an agent, a connection still means it can be reached
  now. For a person, reachability comes from having a seat, and a connection only says whether they are *here*.
  This is the first participant whose reachability is not a fact about a socket, and it is written down as that.
- **SPEC.md §3 and §12.3: the part after `@`.** For an agent it is the machine that runs it. For the two
  participants the broker provides, it is the project. Both are written by the broker, so nothing that attaches
  can choose between the two readings.
- **The typed-name Join.** No longer how a person takes part. There is one name per project and it needs no
  typing.

## What the reservation does not do

Recorded here because an earlier draft of this spec claimed more than the design delivers (`/speckit-analyze`,
2026-09-29).

The person seat exists before anything attaches. So when something attaches under `human-user@<project>`, the
broker finds the name already registered and writes no new row, and **no storage rule sees the attach at all**.
What refuses a wrong claimant is a check in the broker's code, and it compares the adapter the caller reports
about itself. A caller that reports the seat's adapter is admitted as the person.

That is not new. SPEC.md §16 already concedes that anything able to reach a broker can claim to be any agent, and
§17 question 2 records that `human:` is a self-report. What the reservation guarantees is narrower, and it is
still worth having:

- no repository can be **enrolled** as `human-user`;
- no row other than the broker's own can be **stored** as `human-user`, or under the seat's adapter;
- nothing can put an **ownership line** on the person seat or the facilitator, whoever attaches, because storage
  refuses one;
- nothing **attaching as an agent** can be `human-user`, unless it lies about being the seat.

Whether peers should be able to rely on "a person wrote this" stays open in §17 question 2. The owner's call of
2026-09-29: record it honestly rather than add machinery that would move the check without strengthening it.

## Clarifications

### Session 2026-09-29

- Q: Who creates the person's seat, and when? → A: **The broker provides it**, the way it provides the
  facilitator. Every project it serves has `human-user@<project>` from the moment the project is served, whether
  or not anybody has opened a screen. There is one per project, and every surface speaks as it, including one on
  another machine reaching the broker through a tunnel. So a project always has a person to ask, and principle
  2.6's hand-it-back branch no longer arises in a served project.
- Q (from `/speckit-analyze`): What is the part after `@`? → A: **The project**, for both the person seat and the
  facilitator (D2, D7).
- Q (from the owner's first use, 2026-09-30): What does Enter on an agents-list row do? → A: Opens that row's
  actions (User Story 6). Retire and purge stay out of it; "conversations it is in" is included, which needs a
  filter the conversations list does not have yet.
- Q (from `/speckit-analyze`): Does the person's mail expire? → A: **Normal rules, no special promise.** The
  person's mail follows the same retention as everybody's. `prune` never deletes a message in open work, and
  removes old uncollected mail from finished work like anyone else's.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - An agent asks the person for a decision, hours later (Priority: P1)

An agent working on a request needs a decision only the person can make. The person opened Parley in the morning
and has not touched it for hours. The agent sends its question to `human-user`. It is told the person has it and
will answer. It does not hand the need back to whoever asked it, and it does not give up. When the person comes
back, the question is waiting for them. When they answer, the agent carries on.

**Why this priority**: This is the reason for the whole change. Without it an agent stalls or bounces its need
back up the chain every time the person steps away.

**Independent Test**: Seat the person, let them go idle past 20 minutes, have an agent send them a request, and
check what the agent is told, that the message waits, and that an answer from the person reaches the agent.

**Acceptance Scenarios**:

1. **Given** a project whose person has been inactive for over 20 minutes, **When** an agent sends a request to
   `human-user`, **Then** the request is accepted and the agent is told the person will answer, not that the
   person is unreachable.
2. **Given** that request is waiting, **When** the person opens the terminal or the web UI, **Then** they are
   shown that something is waiting for them without having to open a conversation first.
3. **Given** the person answers, **When** the agent next collects its mail, **Then** it receives the answer, and
   the transcript shows it as the person's words.
4. **Given** no person has been active for seven hours, **When** an agent sends to `human-user`, **Then** the
   outcome is the same as after one minute.
5. **Given** the person is at a screen, **When** an agent sends to `human-user`, **Then** the agent is told the
   same thing as when the person is away.

---

### User Story 2 - The person never has to join (Priority: P1)

A person opens `parley` or the web UI. They are already `human-user@<project>` in every project. They can send a
message, answer one, and collect their mail without typing a name or pressing Join.

**Why this priority**: The owner's opening point: a project is only useful when a person has a seat. Joining is a
step that exists only because the seat did not.

**Independent Test**: Open a surface on a machine with two projects and send a message in each without any join
step.

**Acceptance Scenarios**:

1. **Given** a machine with projects `alpha` and `beta`, **When** the person opens the terminal, **Then** they can
   send a message in either project straight away, as `human-user@alpha` in one and `human-user@beta` in the
   other.
2. **Given** the same, **When** the person opens the web UI instead, **Then** the same is true there.
3. **Given** the person uses both surfaces at once, **When** they send in `alpha` from each, **Then** both
   messages are from the one `human-user@alpha`.
4. **Given** a project is created while a surface is open, **When** the person moves to it, **Then** they have a
   seat in it without joining.

---

### User Story 3 - Everyone can see whether the person is here (Priority: P2)

The person, an operator and the agents can all see whether the person is **here** (at a screen and active in the
last 20 minutes) or **away**. Being away never removes the seat and never changes what an agent may send.

**Why this priority**: The old rule was right that a screen must not claim somebody is present when they are
not. This keeps that truth without paying for it with the seat.

**Independent Test**: Open a surface, check the person shows as here; leave it idle past 20 minutes, check they
show as away and are still listed and addressable; touch the surface again, check they show as here.

**Acceptance Scenarios**:

1. **Given** the person is active, **When** an agent lists its peers, **Then** `human-user` is listed and shown
   as here.
2. **Given** 20 minutes pass with no activity, **When** the list is read again, **Then** `human-user` is still
   listed, now shown as away.
3. **Given** every surface is closed, **When** the list is read, **Then** `human-user` is listed and shown as
   away.
4. **Given** the person is away, **When** anyone looks, **Then** nothing describes them as startable, stranded
   or unreachable.

---

### User Story 4 - The name cannot be taken by mistake (Priority: P2)

No repository can be enrolled as `human-user`, no agent attaching as an agent can be it, and nothing but the
broker's own row can be stored as it. Each refusal says why. What this does not cover is written down in *What
the reservation does not do*.

**Why this priority**: The seat's words are meant to be the person's. The checks that can be made are cheap, and
they stop every honest mistake.

**Independent Test**: Attempt to enrol a repository as `human-user`, attach an agent under it, and write a row
for it directly. Every attempt is refused.

**Acceptance Scenarios**:

1. **Given** a repository, **When** someone enrols it as `human-user`, **Then** enrolment refuses and says the
   name belongs to the person.
2. **Given** an agent's launch configuration names `human-user`, **When** it attaches as an agent, **Then** the
   attach is refused.
3. **Given** a direct write to storage registers `human-user` under any other adapter, or any other role under
   the seat's adapter, **Then** storage refuses it.
4. **Given** the facilitator, **When** anything tries to make it the person seat, or the person seat the
   facilitator, **Then** it is refused. The two are never the same participant.

---

### User Story 5 - A flag about the person reads as the person's to-do (Priority: P3)

Work that is stalled on another participant is flagged to the person (§6). When the participant is the person
themselves, the flag reads as *waiting on you*, not as a stalled agent to be started or chased.

**Why this priority**: Useful and cheap, but the core value is stories 1 and 2.

**Independent Test**: Leave a request to `human-user` unanswered past the flag threshold and read the flags on
each surface.

**Acceptance Scenarios**:

1. **Given** a request to the person has waited past the flag threshold, **When** the person reads the flags,
   **Then** it is listed as waiting on them, with who asked.
2. **Given** the same, **When** anything suggests a remedy, **Then** it is not "start the agent".

---

### User Story 6 - Enter on a row opens what can be done with it (Priority: P2)

Added 2026-09-30 from the owner's first use of 0.36.0: *"it felt more natural to be in the agents section and hit
enter on my user in order to start a conversation. I think the enter key should pop open the popup with the commands
that are relevant to the agent you're on."*

In the terminal's agents list, Enter on a row opens a short menu of what can be done with that row, and nothing
else. On the person's own row: start a conversation, their conversations, and collect what is waiting. On an agent's
row: start a conversation with it, the conversations it is in, ask again what it owns, review its ownership line,
start it when it is not running, and instruct it. Retiring and purging are **not** offered there: they stay in the
Agent menu and on their own keys, behind the typed name.

**Why this priority**: Enter on a seat row used to *take that seat*, which this feature removes, so the key needs a
new meaning anyway; and it is where the owner reached first.

**Independent Test**: In a project with the person and two agents, press Enter on each row and read the menu; choose
"conversations it is in" on an agent and see only that agent's conversations, with the filter named and Esc clearing
it.

**Acceptance Scenarios**:

1. **Given** the cursor is on the person's row, **When** Enter is pressed, **Then** a menu offers start a
   conversation, your conversations, and collect what is waiting.
2. **Given** the cursor is on an agent's row, **When** Enter is pressed, **Then** a menu offers that agent's
   everyday actions, and neither retire nor purge.
3. **Given** "conversations it is in" is chosen for `orders`, **When** the conversations list opens, **Then** it
   shows only conversations `orders` is part of, says so, and Esc shows them all again.
4. **Given** an action in the menu cannot be done right now (the agent is already running, say), **When** the menu
   opens, **Then** that action is not offered, rather than offered and then refused.

---

### Edge Cases

- **Two machines, one project.** A person using a surface on another machine, through a tunnel, speaks as the
  project's one `human-user@<project>`. The name says which project, not where the person is sitting. A project
  never shows an agent two people.
- **A machine that shares a project's name.** A computer called `alpha` and a project called `alpha` give
  `orders@alpha` (an agent on that machine) and `human-user@alpha` (the person in that project) the same second
  half. Nothing routes on it, because the broker never asks a machine to start the person or the facilitator, so
  the overlap affects only how the names read.
- **A seat created before this change under a typed name.** Nothing has been released, so there is nothing to
  migrate. Such a row is left as it is; an operator may purge it. It is not treated as the person seat.
- **The person answers from one surface while another is open.** The other surface stops showing the item as
  waiting on its next refresh. Mail is collected once.
- **A request sent to the person while the broker is stopped.** Nothing changes. It is sent when the broker is
  back, the same as any request.
- **An agent asks the facilitator something back.** Unchanged from 022: flagged to the person, answered from the
  ownership review, and recorded as the person's words — now under `human-user`.
- **Existing tests seat people as `arvind@test` and similar.** These describe the typed-name design. They move
  to `human-user` or go, depending on what each one checks.

## Requirements *(mandatory)*

### Functional Requirements

**The seat exists**

- **FR-001**: The broker MUST provide every project it serves with exactly one person seat, named
  `human-user@<project>`, from the moment the project is served and whether or not any surface has opened.
- **FR-001a**: Every surface, on any machine, MUST speak in a project as that project's one person seat, reading
  its name from the broker. A surface MUST NOT register a person seat of its own.
- **FR-002**: Once created, the person seat MUST NOT be removed by inactivity, by closing a surface, by a broker
  restart, by any timer, or by an operator. It is never retired and never purged. (Narrowed during planning,
  research R2: the broker recreates the seat on its next start, so a purge would only cut the person's words out
  of every transcript.)
- **FR-003**: `human-user` MUST be a reserved role. Storage MUST refuse it under any adapter but the seat's, and
  MUST refuse the seat's adapter under any other role. Enrolment and attach MUST refuse it to an agent, with a
  sentence naming why. The limits of this are recorded in *What the reservation does not do*.
- **FR-004**: The person seat and the facilitator MUST NOT be the same participant, and neither may take the
  other's role.
- **FR-004a**: The facilitator MUST be named `parley-facilitator@<project>` (D7).
- **FR-004b**: Neither participant the broker provides may cause a machine to be recorded under its project's
  name. The part after `@` is a project for these two and is not a machine.
- **FR-004c**: Neither participant the broker provides may have an ownership line. Storage MUST refuse one,
  whatever writes it.
- **FR-004d**: The facilitator MUST be reported as present whenever its project is served (SPEC.md §14), not as
  stranded for having no machine.

**Being in the seat**

- **FR-005**: A person MUST be shown as **here** when a surface is open and has been used within the idle window
  (20 minutes, the existing `SeatBook.IdleAfter`), and as **away** otherwise.
- **FR-006**: Every place that lists participants — the agents' own peer list, the operator's registry, the
  terminal and the web UI — MUST show the person seat with here or away, and MUST NOT show it as startable,
  stranded or unreachable.
- **FR-007**: Becoming away MUST NOT change what may be sent to the person, or what a sender is told.

**Messages to the person**

- **FR-008**: A message to the person seat MUST be accepted whether they are here or away.
- **FR-009**: The sender of a message to the person MUST be told, in plain words, that the person has it and will
  answer. It MUST NOT be told the person is unreachable, and the broker MUST NOT ask any machine to start the
  person.
- **FR-010**: The person's mail MUST follow the same retention rules as every participant's. No special expiry
  and no special protection.
- **FR-011**: When a surface opens, or while one is open, it MUST show the person how much is waiting for them,
  without requiring them to open a conversation first.

**Principle 2.6**

- **FR-012**: "The person seated" in principle 2.6, §5's `blocked` rule and §8's rule 10 MUST mean the project's
  person seat whether or not the person is here. Every served project has one (FR-001), so the hand-it-back
  branch no longer arises. SPEC.md keeps it only for an implementation that provides no person seat.
- **FR-013**: The standing instructions an agent receives MUST tell it that the person may be away, that its
  message will wait, and that waiting for the person's decision is correct rather than a reason to send
  `blocked`.

**Surfaces**

- **FR-014**: The typed-name Join, and every step that exists only to take or give back a seat, MUST be removed
  from both surfaces.
- **FR-015**: A flag on work waiting for the person MUST read as waiting on the person, and MUST NOT suggest
  starting them.
- **FR-016**: The person's words MUST continue to be recorded as a person's (§11.3 `human:` responder), under
  the `human-user` name.

**Enter on a row** (User Story 6)

- **FR-018**: Enter on a row of the terminal's agents list MUST open a menu of the actions that apply to that row,
  and MUST NOT act by itself.
- **FR-019**: The menu MUST NOT offer retire, reinstate or purge. Those stay where they are, behind the typed name.
- **FR-020**: The conversations list MUST be able to show only the conversations one agent is part of, MUST say on
  screen which agent it is filtered to, and MUST return to every conversation on Esc.
- **FR-021**: The label for starting a conversation MUST NOT mention taking a seat, and MUST say what it costs in
  plain words — the other side will owe an answer — rather than "opens an exchange they owe a result".

**Not ruling out later surfaces**

- **FR-017**: Nothing in this feature may require the person to be at a surface on the broker's machine in order
  to have a seat, to have mail wait, or to be told something is waiting. A later mobile surface or notification
  system must be able to read the same waiting mail and the same here/away state without a change to what an
  agent sees.

### Key Entities

- **Person seat**: The project's one participant that is a person. Named `human-user@<project>`, reserved, never
  retired or purged. Has mail and a here/away state. Is not an agent and is not the facilitator.
- **Facilitator**: Unchanged from 022 except its name, now `parley-facilitator@<project>`.
- **Presence**: Whether the person is here or away. Derived from surfaces being open and used, never stored as a
  claim nothing withdraws, and never a reason to remove the seat.
- **Wake outcome for a person**: What a sender is told when its message is to the person: the person has it and
  will answer. Distinct from the outcomes an agent's wake can have.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A person opening either surface can send their first message in any project with zero join steps
  and no name typed.
- **SC-002**: A message sent to the person after 1 minute, 20 minutes, 1 hour and 7 hours of inactivity is
  accepted every time, and its sender is told the person will answer every time. No sender is ever told the
  person is unreachable.
- **SC-003**: Every attempt to enrol a repository as `human-user`, to attach an agent as it under any other
  adapter, or to store it under any other adapter, is refused.
- **SC-004**: After the idle window passes, every participant list still shows the person seat, marked away,
  within one refresh of each surface.
- **SC-005**: A request waiting for the person is visible to them within one refresh of opening a surface,
  without opening the conversation it is in.
- **SC-006**: An agent that needs the person's decision waits for it and does not send `blocked` merely because
  the person is away, in a walked end-to-end test with a real agent.

## Assumptions

- The idle window stays at 20 minutes. It now decides only here versus away.
- Mail for a person already waits without a seat (since 0.29.1), and the count is already read without one. This
  feature relies on both and does not rebuild them.
- Nothing has been released, so there are no stores to migrate. **A store created before 0.36.0 keeps its old
  shape with no warning**, because schema 1 is edited in place and its number does not move: it has none of the
  person seat's rules, a real machine entry named for the project, and the facilitator under its machine name. The
  owner's call (the second `/speckit-analyze`): reset with `reset.ps1` on upgrade, and say so in the installers,
  the README and CLAUDE.md, without the broker refusing an old store.
- The `human:` responder of §11.3 stays a self-report. Refusing `human:` from anything but the person seat is
  recorded in §17 question 2 as a candidate and is **not** adopted here.
- The version moves as CLAUDE.md requires. Whether the protocol number moves is decided in the plan, by §18's
  precedent that an unreleased protocol version is recorded rather than spent.
- The constitution restates the standing constraints and mentions seats in its principles. The plan checks
  whether this needs an amendment before implementation, as 015, 020 and 022 each did.
