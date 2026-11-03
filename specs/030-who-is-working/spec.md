# Feature Specification: Who is working

**Feature Branch**: `030-who-is-working`
**Created**: 2026-11-02
**Status**: Draft
**Input**: Owner, 2026-11-02: "Need to make it more clear what agents are currently working. Right now I cannot really tell."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - See who is working at a glance (Priority: P1)

The person looks at a project's agents, or at all agents, and sees at once which are working and for how long, without opening a conversation.

**Independent Test**: With one agent in a turn and one attached with none, the list shows the first as working, first in the list, with its duration, and the second as idle.

**Acceptance Scenarios**:

1. **Given** an agent in a turn, **When** the person reads either agent list, **Then** it reads as working, with how long, and comes before agents that are not.
2. **Given** an agent attached with no turn, **When** they read it, **Then** it reads as idle.
3. **Given** an agent whose last turn had a permission refused, **When** they read it, **Then** it reads as stopped, and the terminal groups it apart.
4. **Given** an agent whose turns nobody reports, **When** they read it, **Then** it reads by presence alone, and neither working nor idle is claimed.
5. **Given** a list left open, **When** an agent starts or ends a turn, **Then** the list catches up without the person doing anything: within seconds on the terminal and 10 seconds on the web.
6. **Given** the web Agents tab, **When** any agent is working, **Then** a line names who, per project; and the state filter can choose *working*.

### Edge Cases

- A retired agent or one open outside Parley never reads as working, whatever was last reported.
- The broker cannot be read: the terminal keeps the list it has rather than emptying it.
- The project changes while a read is in flight: the result is dropped.

## Requirements *(mandatory)*

- **FR-001**: Both agent lists MUST distinguish working, idle, stopped on a refused permission, and not running.
- **FR-002**: Both surfaces MUST use one derivation.
- **FR-003**: No surface MAY claim working, idle or stopped for an agent whose turns nobody reports (SPEC §11.6).
- **FR-004**: The lists MUST update on their own.

## Success Criteria *(mandatory)*

- **SC-001**: From either list, the person can name who is working in under five seconds, with no conversation open.

## Assumptions

- **Which conversation a turn is for is out of scope.** The broker does not know: a turn report does not carry it, and a turn may touch several. Showing it would mean the node reporting what the turn collected, which is a change to the wire. Recorded for the owner to ask for.
- The room's existing marks are unchanged, including that an agent busy elsewhere reads as working in every room it appears in.
- "Stopped" in a list means the last turn had a refused permission. The room's wider "stopped with work still owed here" depends on a conversation, so a list does not use it.
- The web Agents tab reloads every 10 seconds instead of 30, which closes an expanded row. A partial refresh would avoid that, and is a later improvement.
