# Feature Specification: Work comes back when the session that took it is gone

**Feature Branch**: `027-session-owned-delivery`
**Created**: 2026-10-23
**Status**: Draft
**Input**: User description: "Work comes back when the session that took it is gone (SPEC.md §14). Today a delivered message records only that it was delivered, not which session of the agent collected it. On attach, the broker gives owed work back only when no other session of that agent is attached, so two editor windows on one repository do not both do the same work. This guesses from timing and loses a race: when an agent restarts quickly, the new session can attach before the broker has noticed the old session leave; the broker treats the new session as a second window, gives nothing back, and nothing re-checks when the old session's departure is recorded. The feature: each delivery records the session that collected it, and work delivered to a session that is no longer attached is given back. Work held by a session that is still attached stays with it. No 'working' state is added; nothing times out; schema 1 and the wire change in place."

## Why this exists

SPEC.md already promises this. §6: *"An exchange collected by a session that ended before sending its
Result is delivered again the next time the receiver collects, ahead of anything new."* §7.3 repeats it
for *"an earlier session of the same agent that ended before sending a Result."*

The reference implementation keeps that promise only when the old session's departure has been
recorded before the new session attaches. It decides at attach time, from a count of open sessions:
if any session is attached, it gives nothing back, so that a second editor window does not repeat the
first window's work. A fast restart breaks that guess. The new session attaches while the broker still
counts the old one, nothing is given back, and nothing looks again once the old session is gone. The
work stays marked as delivered to a session that no longer exists.

The work is never lost, because the exchange stays open and §6 eventually flags it to the person. But
the agent sits idle with work it does not know it owes, which is the failure §7.3 exists to prevent.

The fix is to stop guessing. A delivery records which session took it, so whether that session is
still attached is a fact the broker holds, not an inference from timing.

Knowing a session is gone is not enough on its own. A session can end three ways, and the broker
must treat them differently:

| How it ended | Example | Is the process coming back? | What happens to its work |
|---|---|---|---|
| Controlled | The editor is closed; the adapter shuts down normally | No | Handed back at once, to any session |
| Abrupt, process alive | A network blip, laptop sleep, a broker restart | Yes | Kept for that process, which reclaims it when it reconnects |
| Abrupt, process dead | A crash, or the process is killed | No | Handed only to a session that opened after the work was collected |

The broker cannot tell the last two rows apart without waiting, and nothing times out. So an abrupt
end holds the work for its own process, and otherwise lets only a *newer* session take it: a
restarted process is newer, a window that was already open is not.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A fast restart gets its unfinished work back (Priority: P1)

An agent collects a Request and starts on it. Its editor is closed and reopened, or the node restarts
it after a crash, quickly enough that the new session attaches before the broker has noticed the old
one leave. On its next collection the new session receives the unfinished Request ahead of anything
new.

**Why this priority**: This is the defect. Without it, work silently waits until a person notices a
stall flag.

**Independent Test**: Hold the old session's departure back so it is recorded after the new session
attaches (the race made deterministic), then collect from the new session and see the Request.

**Acceptance Scenarios**:

1. **Given** an agent's session collected a Request and has not answered it, **When** that session ends
   and a new session for the agent attaches before the broker has recorded the end, **Then** once the
   end is recorded, the new session's next collection returns the Request ahead of new work.
2. **Given** the same race, **When** the end is recorded before the new session collects, **Then** the
   first collection returns the Request.
3. **Given** a session ended with no new session attached, **When** a session attaches later (the slow
   restart that works today), **Then** its first collection returns the Request, as today.

---

### User Story 2 - Two open windows do not both get the same work (Priority: P1)

A person has two editor windows open on one repository, so the agent has two sessions. One window
collected a Request and is working on it. The other window collecting does not receive that Request
again.

**Why this priority**: The fix must not break the rule the current guess was protecting. Losing it
means the same work done twice.

**Independent Test**: Attach two sessions, collect from the first, collect from the second, and see
that the second does not receive the Request.

**Acceptance Scenarios**:

1. **Given** two sessions of one agent are attached and the first collected a Request, **When** the
   second collects, **Then** the Request is not returned to it.
2. **Given** that state, **When** the first session ends in a controlled way without answering,
   **Then** the second session's next collection returns the Request.
3. **Given** that state, **When** the second session ends, **Then** nothing changes for the first: the
   Request stays with it.
4. **Given** that state, **When** the first session ends abruptly and never comes back, **Then** the
   second session does not receive the Request, because it was open before the Request was
   collected; the exchange is left to §6's stall flag, as today.

---

### User Story 4 - A dropped connection keeps its work (Priority: P1)

An agent's connection to the broker drops while it is working: a network blip, the laptop sleeping,
the broker restarting. The adapter reconnects a few seconds later in the same process. It keeps the
work it was doing, and another window on the same repository does not receive it in the meantime.

**Why this priority**: Without it, fixing the fast restart hands one window's work to another
whenever a connection blips, which is the duplicate work the old rule prevented.

**Independent Test**: Attach two sessions, collect from the first, drop the first's connection
without a controlled end, collect from the second, reconnect the first naming its old session.

**Acceptance Scenarios**:

1. **Given** a session collected a Request and its connection dropped, **When** the same process
   reconnects naming its old session, **Then** the Request is held by the new session and is not
   delivered to it again.
2. **Given** two windows, and the first's connection dropped after it collected a Request, **When**
   the second window (open before the collection) collects before the first reconnects, **Then** it
   does not receive the Request.
3. **Given** the broker restarted under two windows, **When** both reconnect, in either order, and
   both collect, **Then** each keeps its own work and neither receives the other's.

---

### User Story 3 - The race is held by a test (Priority: P2)

A test reproduces the fast-restart race on every run, rather than by luck of timing, and fails against
the current behaviour.

**Why this priority**: The race was found as a CI flake. A fix without a deterministic reproduction can
not be shown to work.

**Independent Test**: The test fails before the change and passes after it, on every run.

**Acceptance Scenarios**:

1. **Given** the current behaviour, **When** the test runs, **Then** it fails because the new session
   never receives the Request.
2. **Given** the change, **When** the test runs repeatedly, **Then** it passes every time.

---

### Edge Cases

- **The broker restarts.** Every session ends with it. All work delivered before the restart belongs to
  sessions that are gone, so each agent's next collection returns it. Whether agents are *woken* for it
  is still the resume policy of §14; giving work back is not waking.
- **An agent answers after its session has ended.** It cannot: an answer is sent by a session. A later
  session that received the Request again answers it, and the exchange closes once.
- **A crash with another window already open.** The crashed window's work is not handed to the
  older window (User Story 2, scenario 4). The broker cannot tell a crash from a connection that is
  coming back, and handing it over would duplicate work after every blip. It waits for a newer
  session or §6's stall flag. This is the one case the feature leaves as today.
- **A newer window opens while another window's connection is down.** If it collects before the
  first window reconnects, it receives that window's work, and the first window may carry on with
  it too. This needs a second window opened after the work was collected and an outage at the same
  moment; the first answer still closes the exchange once.
- **The editor kills the adapter rather than shutting it down.** Then every close is abrupt, and a
  fast restart is served by the newer-session rule instead of the controlled-end rule. Either way
  the new process receives the work.
- **The old session is still attached but stuck.** Its work stays with it. The broker cannot tell
  stuck from working (it sees only collection and answers), so this is §6's stall flag, unchanged.
- **The same session collects twice.** It is not given its own work again on every collection; only
  §7.3's ordering applies, as today.
- **A Clarify or Result owed to the agent.** Covered the same way: whatever the agent owes a move on,
  delivered to a session that is gone, comes back.
- **Work delivered before this change.** Nothing is released, so the store is rebuilt; there is no
  delivery without a recorded session to handle.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Every delivery of a message MUST record which session of the receiving agent collected it.
- **FR-002**: When a session ends in a controlled way, the latest message the agent owes a move on in
  each open exchange delivered to that session MUST be returned to the agent's next collection, from
  any session, ahead of new work (§7.3).
- **FR-003**: The broker MUST NOT return to one session a message that was delivered to another
  session of the same agent that is still attached.
- **FR-011**: When a session ends abruptly, its work MUST be returned only to a session of the agent
  that opened after that work was collected, or to the process that reclaims it (FR-012).
- **FR-012**: A process that reconnects MAY name the session it had; the broker MUST then hand that
  session's work to the new session without delivering it again, and the new session MUST keep the
  old one's place in line for FR-011.
- **FR-013**: How an adapter ends a session in a controlled way MUST NOT be a second way of saying it
  is gone. Presence stays the open connection (§4.0); a missing controlled end is read as abrupt,
  never as lost work.
- **FR-004**: Whether work comes back MUST NOT depend on the order in which the broker learns of one
  session ending and another starting.
- **FR-005**: Giving work back MUST NOT write a new message; it re-uses the existing one, as §14's
  *Re-delivery is not re-sending* requires.
- **FR-006**: Giving work back MUST NOT by itself start or wake an agent. What makes an agent a
  candidate for a start (§11) and what a broker restart wakes (§14's resume policy) are unchanged.
- **FR-007**: The broker MUST NOT add any state claiming an agent is "working" on something. It
  records only collection and answers, which are what it observes.
- **FR-008**: No timeout is introduced. Work comes back because a session ended, never because time
  passed.
- **FR-009**: SPEC.md MUST state the rule exactly: §14 replaces "nothing is given again if a session is
  already attached" with the session rule, and the `agents.connected` comment, §6 and §7.3 agree with
  it. The old wording is removed, not kept beside the new.
- **FR-010**: A test MUST reproduce the fast-restart race deterministically (User Story 3).

### Key Entities

- **Session**: one connection of an agent's adapter to the broker, from attach to detach. An agent has
  zero or more at once. Today the broker counts them; this feature needs each to be identifiable for as
  long as a delivery refers to it, to know whether it ended in a controlled way, and to know its place
  in line (which sessions are newer than a collection).
- **Delivery**: a message's collection by a session. Today it is a time; it gains the session that
  collected it.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In the reproduced fast-restart race, the new session receives the unfinished work on
  100% of runs (today: 0% once the race is hit).
- **SC-002**: With two sessions open, the second never receives work the first collected and is still
  attached to, on 100% of runs.
- **SC-003**: The test that reproduces the race passes 3 consecutive CI runs on Windows, Linux and
  macOS at one commit.
- **SC-004**: No stall flag is raised for work that was only waiting on a fast restart.
- **SC-005**: With two windows open, a dropped connection that comes back never hands its work to the
  other window, on 100% of runs.

## Assumptions

- **A controlled end hands work back when it happens; an abrupt end is decided at collection.** A
  controlled end is the adapter saying it will not finish what it holds, so its work is returned at
  once. An abrupt end is only known when somebody next collects, and the newer-session rule decides
  then. Handing work back does not wake or start an agent: what makes an agent a candidate for a start
  is the work it owes, which does not change (§11).
- **Whether the editor ends the adapter cleanly is not known.** The adapter has a clean shutdown path,
  but an editor that kills it never reaches it. The design does not depend on the answer (see Edge
  Cases); it is measured during implementation.
- **The person seat follows the same rule, as it does today.** Its surfaces attach and collect like any
  session, and today's give-back already applies to it; excluding it would be a change of its own. The
  facilitator never receives work, so nothing changes for it.
- **Every collection happens inside an attached session**, for every adapter tier. Planning confirms
  this; if some path collects without one, it needs a rule of its own.
- **Session identity** is resolved in research.md R1: the broker issues one when a session opens.
- **No compatibility:** nothing is released, so schema 1 and the wire change in place and the store is
  rebuilt.
