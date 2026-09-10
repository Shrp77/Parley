# Feature Specification: Waking notifications

**Feature Branch**: `010-waking-notifications`
**Created**: 2026-09-10
**Status**: Draft
**Input**: A notification must wake the agent it is addressed to, and a person must be able to send one to every agent at once.

## Why this reverses a written decision

SPEC.md §17 question 15 records today's behaviour as deliberate:

> only a Question, a Clarify and an Answer ask for one — a Notify nudges and never asks

and argues against changing it:

> Wiring Notify to it later, for the obvious-looking reason that a notified agent should read its
> mail, would make it unbounded and would be question 1's problem reached by a second route.

**That objection is answered rather than overruled**, and the answer comes from question 1's own
evidence. Question 1 records a probe in which sixty notifications from one agent to one peer were
accepted in a tenth of a second, and records that `inbox` with no limit "collects everything
pending". A turn therefore drains all sixty. **One start is the correct number for those sixty, not
a rationed one** — so a bound that coalesces starts costs the feature nothing and removes the
unboundedness entirely. Question 15 assumed wiring Notify to the wake path meant one turn per
notification. It does not have to.

The document is the authority, so this is a protocol change: §5.4 gains behaviour, and questions 1
and 15 are answered rather than left open.

**What is not claimed.** This does not bound Notify's *volume*, which is question 1's actual
subject and stays open. A peer can still put sixty distinct notifications into a recipient's next
turn. What this bounds is the number of *turns* those notifications can cause, which is the new cost
the change introduces and the only part question 15 was about.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A notification reaches an agent nobody has open (Priority: P1)

A person holds a seat and tells `orders` something. Nobody has that repository open. Today the
message is written, collected by nobody, and read only if somebody later opens the repository —
and the surface says "Sent.", which is true and misleading.

After this change the broker asks the agent's machine to start it, exactly as it would for a
question, and the agent collects the notification on its first turn.

**Why this priority**: It is the reported defect and the whole point of the feature. Without it
the other stories have nothing to deliver.

**Independent Test**: Send one notification to a catalogued agent that is not running, with a node
attached. The agent starts and the message is collected.

**Acceptance Scenarios**:

1. **Given** `orders` is catalogued on a machine whose node is attached and no session is running
   it, **When** a seated person sends it a notification, **Then** the broker requests a start and
   the agent collects the notification without anybody opening the repository.
2. **Given** `orders` is registered but this machine's node has no launch command for it, **When**
   a notification is sent, **Then** the message is still stored and the sender is told plainly that
   nothing here can start the recipient.
3. **Given** `orders` has been retired, **When** a notification is sent, **Then** it is refused as
   it is today (§14) and nothing is started.

---

### User Story 2 - Sixty notifications do not cost sixty turns (Priority: P1)

An agent sends many notifications to one peer in quick succession — the shape question 1 measured.
The peer is started once and its single turn collects all of them.

**Why this priority**: It is not a refinement of story 1, it is the condition on which story 1 is
allowed to ship. Story 1 without it is the unbounded turn cost §17 question 15 refuses.

**Independent Test**: Send sixty distinct notifications to one idle agent as fast as they will go
and count start requests. Exactly one.

**Acceptance Scenarios**:

1. **Given** an idle catalogued agent, **When** sixty distinct notifications arrive within the
   window, **Then** exactly one start is requested and every one of the sixty is stored and
   collectable.
2. **Given** ten different senders each notify one idle agent once, **When** all ten arrive within
   the window, **Then** exactly one start is requested — the bound is per recipient, not per pair.
3. **Given** an agent was started for a notification and has since drained its inbox, **When** a
   further notification arrives, **Then** it is free to wake the agent again rather than being
   suppressed by a timer that has not yet expired.
4. **Given** a wake was requested and the agent never drained — no node, a crashed launch, a
   process that attached and died — **When** the ceiling has passed, **Then** a further notification
   may wake it again, so a failed start does not silence waking permanently.
5. **Given** an agent that is already running and was just handed a turn for a question, **When**
   notifications arrive before it drains, **Then** none of them asks for a further turn, because the
   turn already coming will collect them.

---

### User Story 3 - Telling every agent at once (Priority: P2)

A person holds a seat, opens the composer to tell somebody something, and chooses **All agents**
instead of a single peer. Every agent in the project receives an ordinary notification.

**Why this priority**: It is the second half of what was asked for and it is independently
valuable, but a broadcast that woke nobody would be the reported defect multiplied. Stories 1 and 2
come first.

**Independent Test**: With several agents in a project, choose All agents in the composer and send.
Each agent receives one ordinary notification and the surface reports the count.

**Acceptance Scenarios**:

1. **Given** a project holding `orders`, `billing` and `inventory`, **When** a seated person sends
   to All agents, **Then** three ordinary notifications are sent, one per agent.
2. **Given** the sender holds a seat and another person holds one too, **When** All agents is
   chosen, **Then** neither seat receives a notification — All agents means agents.
3. **Given** one of the agents is retired, **When** All agents is chosen, **Then** it is not
   addressed at all rather than addressed and refused.
4. **Given** a broadcast where some recipients could be started and some could not, **When** it
   completes, **Then** the surface says how many were sent, how many were started, and names those
   nothing here can start.
5. **Given** a project with no agents other than seats, **When** All agents is chosen, **Then** the
   surface says so and sends nothing, rather than reporting a successful send to nobody.

---

### User Story 4 - The sender learns whether anything is going to read it (Priority: P2)

Sending a notification says what happened to the recipient, not only that the message was stored.

**Why this priority**: The reported defect was not only that nothing started — it was that nothing
*said* nothing had started. "Sent." was true and left the person believing a peer was reading it.

**Independent Test**: Send to an agent nothing can start and read the sentence returned.

**Acceptance Scenarios**:

1. **Given** the recipient was idle and a start was requested, **When** the send completes,
   **Then** the sentence says the agent is being started.
2. **Given** the recipient is already attached, **When** the send completes, **Then** the sentence
   says it is running and has been handed the message.
3. **Given** nothing on the recipient's machine can start it, **When** the send completes, **Then**
   the sentence says the message is waiting and nothing here will start the recipient — and the
   message is stored regardless.
4. **Given** a start was suppressed because one is already outstanding, **When** the send completes,
   **Then** the sentence says the agent is already starting rather than implying nothing happened.

---

### Edge Cases

- **A wake is requested and the agent is opened in somebody's editor window instead.** The pending
  marker is satisfied by the drain, whatever caused it. Nothing tracks *why* an agent drained and
  nothing should.
- **An agent attaches, and dies before draining.** The marker is not satisfied, because nothing has
  read the message. It expires at the ceiling and the next notification may wake again. This is the
  case that rules out treating an attachment as sufficient.
- **A running agent is handed a turn that does not drain.** §4.2 distinguishes waking an adapter
  from the model taking a turn, and only a drain proves the mail was collected. The marker keys on
  the drain for the same reason.
- **A notification is deduplicated (§13.1).** It carries no new information, so it must not wake
  anybody. A byte-identical repeat inside sixty seconds is exactly the case a wake would be spent
  on nothing.
- **A notification arrives for an agent on another machine.** The broker has no node for that
  machine; nothing is started and the sender is told so — the same outcome as today, said out loud
  instead of silently.
- **A broadcast is sent while the sender's seat is swept mid-send.** The rule is at the send
  (0.18.9): every individual notification re-checks the seat, so a broadcast stops rather than
  finishing under a released name.
- **A broadcast partially fails.** Each notification is an independent ordinary message; some
  succeeding and some being refused is an ordinary outcome and must be reported as counts rather
  than as a single success or failure.
- **The roster changes mid-broadcast.** The recipient list is taken once, when the send begins. An
  agent that registers during the send is not chased.
- **A broadcast to a large roster.** The person is told how many agents will be told, and how many
  of them this will start, before it is sent.

## Requirements *(mandatory)*

### Functional Requirements

**Waking**

- **FR-001**: A notification MUST request attention for its recipient on the same path a question
  does — a start when the recipient is not attached, a turn when it is.
- **FR-002**: A notification that is refused MUST NOT wake anybody. The routing refusals of §14 and
  the closed-conversation refusal are decided before any wake.
- **FR-003**: A notification that is deduplicated under §13.1 MUST NOT wake anybody, because it
  carries nothing the recipient has not already been sent.
- **FR-004**: Storing the notification MUST NOT depend on the wake succeeding. §17 question 1
  requires that whatever bounds this "must not make an event undeliverable"; a recipient nothing can
  start still receives the message and collects it whenever it next runs.

**The bound**

- **FR-005**: Wake requests MUST be coalesced **per recipient, across all senders**. A per-pair
  bound leaves one agent wakeable once per peer, which is the flood shape that matters because the
  recipient is the one that pays.
- **FR-006**: A notification MUST NOT ask for attention while attention is already pending for that
  recipient. The condition is pending-ness rather than elapsed time, because the question being
  asked is "is a turn already coming that will see this?" and that has an exact answer.
- **FR-007**: Pending attention MUST be considered satisfied when the recipient **drains its
  inbox**, not merely when it attaches. An agent that has attached and not yet drained has not seen
  the message, so treating the attachment as sufficient would suppress the wake that was supposed to
  deliver it.
- **FR-008**: Pending attention MUST expire after a ceiling, so that a wake which never produced a
  drain does not suppress waking indefinitely.
- **FR-008a**: **Every** wake MUST set the pending marker, including a question's, while only a
  notification MUST consult it. A question carries a debt and is already bounded by §7.1's per-pair
  cap, so it keeps today's behaviour; a notification that stacked a turn on top of one a question
  had just asked for would be buying a second turn to read mail the first will already collect.
- **FR-009**: The ceiling MUST be a named constant with its reasoning recorded beside it, not a
  schema fact. §13.1's window is in the schema because it changes the meaning of buckets already
  stored; this one governs no stored row and changing it invalidates nothing, so a migration would
  be ceremony.
- **FR-010**: The bound MUST hold regardless of which surface sent the notification. It is a
  property of the broker, not of a composer.

**Telling the sender**

- **FR-011**: A notification's reply MUST carry what happened to the recipient, distinguishing at
  least: started, already running, already starting, and nothing here can start it.
- **FR-012**: Both composing surfaces MUST report that outcome in the sentence they show, rather
  than reporting only that the message was stored.
- **FR-013**: The outcome MUST NOT be presented as a failure when the message was stored. A
  recipient nothing can start is a fact about the deployment, not a rejected send.

**All agents**

- **FR-014**: The composer MUST offer **All agents** as a target when telling somebody something.
- **FR-015**: All agents MUST resolve to every non-retired agent in the project, excluding every
  seat — the sender's own and anybody else's.
- **FR-016**: A broadcast MUST be sent as N ordinary notifications. No new message type, no new
  wire operation, no new broker operation. §5.5 settled this shape for questions and the argument is
  stronger here, since a notification opens no exchange at all.
- **FR-017**: Each notification in a broadcast MUST be subject to every ordinary rule — the size
  cap, deduplication, the routing refusals, and the seat check at the send.
- **FR-018**: A broadcast MUST report counts: how many were told, how many were started, and which
  recipients nothing could start.
- **FR-019**: A broadcast to an empty roster MUST say so and send nothing.
- **FR-020**: The composer MUST name how many agents a broadcast will reach before it is sent.

**The document**

- **FR-021**: SPEC.md §5.4 MUST record that a notification causes its recipient to be started, and
  MUST record the coalescing bound as part of the same rule.
- **FR-022**: SPEC.md §17 question 15 MUST record that it has been answered, what the answer is,
  and why its unboundedness objection does not survive the coalescing bound — kept and answered
  rather than deleted, because the trade it describes is the one being reversed.
- **FR-023**: SPEC.md §17 question 1 MUST record that a notification now costs a turn on the
  recipient's machine, and where that cost is measurable, since question 1's evidence-gathering was
  not counting it.
- **FR-024**: The plugin's skill description of `notify` MUST NOT continue to state the old
  behaviour if it states it.

### Key Entities

- **Pending attention**: what the broker remembers per recipient — that a wake has been asked for
  and the recipient has not drained since. Set by every wake, consulted only by notifications,
  cleared by a drain, expired by the ceiling. Held in memory, not stored: a broker restart has no
  residents to remember, so a remembered request would be a claim about a process that no longer
  exists.
- **Wake outcome**: what a notification's reply carries about its recipient — started, already
  running, already starting, or unstartable here.
- **Broadcast result**: counts and named exceptions for one All-agents send. A surface concern; the
  protocol sees N independent notifications.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A notification to a catalogued, idle, non-attended agent results in that agent
  running and collecting the message with no human action.
- **SC-002**: Sixty notifications to one idle agent within the window produce exactly one start
  **and exactly one turn**, and all sixty are collected on that turn. The turn count is the one that
  matters: a node gives a turn to a resident it already holds, so bounding processes alone would
  have bounded nothing.
- **SC-003**: Ten distinct senders notifying one idle agent within the window produce exactly one
  turn.
- **SC-003a**: Notifications arriving while a question's turn is already pending add no further
  turn.
- **SC-004**: Every notification sent is stored and collectable regardless of whether the wake
  succeeded — zero messages lost to the bound.
- **SC-005**: A person sending a notification can tell, from the sentence shown, whether anything
  is going to read it, without opening another surface.
- **SC-006**: A person can tell every agent in a project one thing in a single act, and is told how
  many received it and how many were started.
- **SC-007**: No seat receives a broadcast.
- **SC-008**: A start that fails to produce an attachment stops suppressing further wakes once the
  ceiling passes.

## Assumptions

- **The reported case is an operator-shaped one, but the change is not operator-only.** The person
  was seated, and a seat is an ordinary agent attachment rather than an operator surface, so the
  change lands on the agent path and applies to agent-to-agent notifications too. That is
  deliberate: an agent that tells a peer something important and is never read is the same defect
  from the other side.
- **The ceiling is provisional and set from judgement, then measured.** It follows the precedent of
  the message cap, which was set from `parley refusals` rather than argued in advance, and of
  §13.1's window, which is documented as a starting estimate to be tuned against real traffic. The
  planning phase picks the number; the record says it is provisional and on what evidence it moves.
- **`notify_volume` does not change.** It counts messages, and the new cost is turns. Turns are
  already recorded with their duration and cost, so the evidence question 1 now needs exists without
  a new table.
- **`parley fleet` is unaffected.** Its sweep already treats any uncollected message as a reason to
  start an agent, notifications included, and is a separate mechanism from the broker's wake path.
  This change makes the broker do promptly what the sweep would have done eventually, on deployments
  where the sweep runs at all.
- **The web console and the terminal share one implementation.** Both compose through the same
  client, so the outcome sentence and the broadcast are written once. Two surfaces implementing a
  report of the same send would disagree about what was sent.
- **No schema change is expected.** Nothing new is stored: the outstanding-start record is in
  memory and the wake outcome is carried on a reply. If the planning phase finds a cap or a column
  that must exist, that is a finding and a migration, as `005_instruction_reply_cap.sql` was in 008.

## Out of scope

- **Bounding Notify's volume.** §17 question 1 stays open. This bounds turns, not messages.
- **Topics and subscriptions.** Question 1's candidate answers, unaffected either way.
- **Making `parley fleet` part of the autostart entry.** A separate decision about unattended
  operation, surfaced while diagnosing this and recorded so it is not lost.

## Carried defect, unrelated to this change

`InvocationRepository.FindWaitingAsync`'s remark claims four conditions and names an
`invoke_when_idle` opt-in column. That column exists nowhere in the schema or in SPEC.md — consent
moved to the node catalogue — and the query has two conditions. Low severity and not caused here,
but it sits in the exact method this feature's policy discussion sends a reader to, so it is fixed
in passing rather than left to be rediscovered.
