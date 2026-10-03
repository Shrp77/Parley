# Feature Specification: Report agent turns, so working and stopped are told apart

**Feature Branch**: `019-layout` (no branch of its own; 019–023 live there too, the owner's arrangement)
**Created**: 2026-10-03
**Status**: Draft
**Input**: User description: "Report agent turns to the broker so surfaces can tell an agent that is working from one that has stopped with work still owed. […] the owner saw an agent reported as working for nearly three hours after a permission guard refused its command and its turn ended silently. […] A turn that ends still owing work with permission refusals should also be flagged to the person at once rather than after the 4-hour no-progress threshold. […] this is node→broker reporting plus an operator read, so it is a protocol change and must be argued in SPEC.md first."

## Why

An agent was asked to do something by another agent in the same project. It picked the request up, tried a
command its repository's settings did not allow, and — running with nobody at the keyboard — had the command refused
rather than paused. Its turn ended without a word. The person's screen said the agent was **working, for nearly three
hours**, because the only thing the broker knows is "picked up and not answered", and that is what an agent mid-task
and an agent that has stopped both look like.

The machine that runs the agent already knows the difference. It runs every turn, sees it start and finish, and counts
the tool calls that were refused for want of permission. None of that reaches the broker on the path a deployment
actually uses, so no surface can show it and nothing can flag it.

**A documented promise is also untrue today.** SPEC.md §17 question 1 says the turn record "records every turn with its
duration and cost". On the node path, nothing writes it. Either the spec stops claiming it or the code starts doing it;
this feature is the second.

0.37.9 already tells agents to report a refused permission themselves. That relies on the model; this feature makes the
screen truthful whether or not the agent co-operates.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - See that an agent has stopped, and why (Priority: P1)

A person watching a conversation sees, beside each agent, whether it is **working** — a turn is running right now — or
**stopped with work still owed** — its last turn ended and it has not answered — and, when tool calls were refused for
want of permission, that this is why.

**Why this priority**: It is the defect the owner met. Without it, the screen keeps saying "working" about an agent
that will do nothing until somebody intervenes, and three hours pass before anybody suspects.

**Independent Test**: An agent picks up a request, its turn ends with a refused tool call and without a reply; the
conversation's participant column says it stopped, says a permission was refused, and does not say working.

**Acceptance Scenarios**:

1. **Given** an agent has picked up a request and a turn is running, **When** the person looks at the conversation,
   **Then** the agent is shown as working, with how long the turn has been running.
2. **Given** that turn ends without the agent answering and with no tool call refused, **When** the person looks,
   **Then** the agent is shown as stopped with work still owed, with how long ago it stopped — not as working.
3. **Given** that turn ended with two tool calls refused for want of permission, **When** the person looks, **Then**
   the agent is shown as stopped and the screen says permissions were refused and how many.
4. **Given** the agent answers in a later turn, **When** the person looks, **Then** it is no longer shown as stopped.

---

### User Story 2 - Be told at once when a refused permission stops work (Priority: P2)

When a turn ends with work still owed and with tool calls refused for want of permission, the person is told
immediately, in the same place they are told about every other stalled piece of work — not four hours later when the
"no progress" threshold is crossed.

**Why this priority**: Seeing it requires looking at the right conversation. Being told requires nothing. A refused
permission is something only a person can fix, so there is no point in waiting to see if it resolves itself.

**Independent Test**: A turn ends owing work with a refused permission; the person's list of things needing attention
shows it straight away, naming the agent, the conversation and that a permission was refused.

**Acceptance Scenarios**:

1. **Given** a turn ends with work owed and one permission refusal, **When** the person opens the list of work needing
   attention, **Then** that exchange is there, naming the agent and saying a permission was refused.
2. **Given** a turn ends with work owed and no refusal, **When** the person opens the list, **Then** it is not
   flagged early: the ordinary thresholds apply, because a turn that ended without refusals may be legitimately pausing.
3. **Given** the flagged agent later answers or is started and answers, **When** the person opens the list, **Then**
   the early flag is gone.

---

### User Story 3 - Both surfaces agree (Priority: P3)

The web UI shows the same working and stopped states as the terminal, with the same words.

**Why this priority**: Two surfaces describing one agent differently is a defect this repository has recorded before.
It follows the terminal because the terminal is where the owner met it.

**Independent Test**: The same agent, in the same state, is described identically on both surfaces.

**Acceptance Scenarios**:

1. **Given** an agent stopped with a refused permission, **When** the person opens the conversation in the web UI,
   **Then** it says stopped and permission refused, as the terminal does.

---

### Edge Cases

- **The machine that ran the turn goes away mid-turn.** A turn reported as started and never reported as ended must not
  read as working for ever. When the machine's connection to the broker ends, its running turns are no longer claimed to
  be running, and the agent falls back to what the registry already says about it — its machine is not connected — rather
  than working. No new state: presence already says the true thing. (`/speckit-analyze` U1.)
- **The broker restarts during a turn.** The machine reports the end of a turn the broker never saw start; the end is
  still recorded, and nothing is shown as running that the broker did not hear begin.
- **Two turns overlap or arrive out of order** (a slow report of the end of one after the start of the next). The latest
  start is what "running now" means; an end older than it does not stop the newer turn.
- **An agent attached through somebody's editor** (not run by a machine's node) has no turns reported. It is shown as
  it is today — the screen does not claim working or stopped for an agent whose turns nobody reports — and says so
  rather than guessing.
- **A turn ends with work owed but the agent asked somebody else and is waiting for their answer.** That is not
  stopped work; it is waiting. An agent whose open request to another participant is unanswered is shown as waiting on
  that participant, not as stopped.
- **The person themselves is the participant.** Nothing here applies to the person's seat: the person has no turns.
- **A turn record arrives for an agent the broker does not know** (purged meanwhile). It is ignored, not stored.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The machine that runs an agent's turn MUST tell the broker when the turn starts and when it ends.
- **FR-002**: The end of a turn MUST carry: how long it ran, whether it finished cleanly, whether it was cut off for
  running too long, and how many tool calls were refused for want of permission. How much work the agent had waiting
  before and after MUST be recorded with the turn, **counted by the broker**, which holds the inbox — the machine running
  the agent cannot know it (research R2).
- **FR-003**: The broker MUST record each ended turn in the turn record SPEC.md already defines, so that §17 question
  1's claim that every turn is recorded becomes true on the path deployments use.
- **FR-004**: The broker MUST be able to say, for every agent, whether a turn is running now and when it started, and
  how its most recent turn ended.
- **FR-005**: That answer MUST be readable by the operator surfaces, and MUST NOT be readable by agents: what other
  agents' turns cost and how they ended is the operator's view (§10.1/§10.2's boundary).
- **FR-006**: A turn claimed to be running MUST stop being claimed when the machine that reported it disconnects from
  the broker.
- **FR-007**: The participant column MUST show **working** only while a turn is running for that agent, with how long
  it has been running.
- **FR-008**: It MUST show **stopped** when the agent's most recent turn ended after it picked up work it still owes,
  with how long ago it stopped, and MUST say how many tool calls were refused for want of permission when that number
  is not zero.
- **FR-009**: An agent waiting on another participant's answer MUST NOT be shown as stopped.
- **FR-010**: An agent whose turns nobody reports MUST NOT be shown as working or stopped on the strength of a guess;
  the screen says it cannot tell.
- **FR-011**: A turn that ends with work still owed and at least one permission refusal MUST be flagged to the person
  at once, as its own kind of flag, naming the agent, the conversation and the number of refusals.
- **FR-012**: That flag MUST clear when the agent answers, or when the exchange closes by any route.
- **FR-013**: Both operator surfaces MUST describe working and stopped in the same words.
- **FR-014**: Nothing about how messages are addressed, admitted or delivered changes.
- **FR-015**: SPEC.md MUST be amended first — the node contract (§11/§12), the operator read (§10.2), the flags (§6)
  and the closing of §17 question 1's gap — before any code depends on it.

### Key Entities

- **Turn**: one run of an agent by the machine hosting it. Has an agent, a start, and once ended an end with its
  duration, whether it finished cleanly, whether it was cut off, its permission refusals and the work still waiting.
- **Agent's turn standing**: per agent, whether a turn is running now (and since when) and how the last one ended.
- **Permission-refused flag**: a new kind of flag on an exchange, raised when a turn ends owing it with refusals.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: An agent whose turn ended silently with work owed is shown as stopped within one screen refresh (a few
  seconds) of the turn ending — not after hours.
- **SC-002**: When the reason was a refused permission, the person learns that, and how many, without opening anything
  but the conversation they were already looking at.
- **SC-003**: The person is told about a permission-refused stop immediately, where today the earliest they are told is
  four hours later.
- **SC-004**: "Working" is never shown for an agent with no turn running, in any of the edge cases above.
- **SC-005**: Every turn a node runs appears in the turn record, so the evidence §17 question 1 relies on exists.

## Assumptions

- The machine hosting an agent is the only reliable source of turn facts; an agent attached from an editor is out of
  scope and is shown honestly as unknown.
- The permission refusal count the node already measures is accurate enough to report as-is.
- "Immediately" for the flag means at the broker's next look at flags after the turn's end is recorded, not a push to
  the person's device: notifications stay out of scope, as in 023.
- No retention change: ended turns are kept and pruned by the rules the turn record already has.
- This is a protocol change to the node and operator contracts only. **The protocol version does not move**: it is what
  an agent's adapter announces, the agent-facing surface does not change, and moving it would refuse every adapter until
  upgraded (research R9).
