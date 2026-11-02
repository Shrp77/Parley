# Feature Specification: Close a request without answering

**Feature Branch**: `029-close-a-request`
**Created**: 2026-11-02
**Status**: Draft
**Input**: Owner, 2026-11-02: "Need to be able to close a request without providing an answer - the question may be stale / old / already answered etc."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Close a stale question (Priority: P1)

An agent's question is waiting on the person, and it no longer matters. The person closes it with no answer. The agent is told, and everything else in the conversation carries on.

**Independent Test**: With two questions waiting in one conversation, close one. It ends, the other stays open, and the conversation stays open.

**Acceptance Scenarios**:

1. **Given** a question waiting on the person, **When** they close it on the terminal or in the web console, **Then** it stops being shown as waiting on them.
2. **Given** they gave a reason, **When** it closes, **Then** the agent is told it was closed and is told the reason.
3. **Given** they gave no reason, **When** it closes, **Then** the agent is told it was closed.
4. **Given** it closed, **When** anyone reads the conversation, **Then** the record says the broker closed it for the person, and shows no answer in the person's name.

### Edge Cases

- Already closed, or no such request: nothing changes, and the person is told.
- A request between two agents is refused. The person can close the whole conversation instead.
- The broker cannot be reached: the person is told, and nothing changes.

## Requirements *(mandatory)*

- **FR-001**: The person MUST be able to close one open request they are a party to, without an answer, on both surfaces.
- **FR-002**: The conversation MUST stay open, and other requests in it MUST be unaffected.
- **FR-003**: Both sides MUST be told in the same write, with the reason when one is given.
- **FR-004**: No agent operation MAY do this.
- **FR-005**: The surfaces MUST offer it on what is waiting on the person.

## Success Criteria *(mandatory)*

- **SC-001**: The person can clear a stale question in under ten seconds without typing an answer.
- **SC-002**: In every case tested, a closed request leaves other work in its conversation unchanged.

## Assumptions

- The surfaces offer this on requests waiting on the person. The broker also accepts the person's own request to an agent, but no screen offers that yet, because the owner's words were about questions. Offering it is a small change if the owner wants it.
- The agent is told it was closed, and may ask again. Closing is not a block on the question.
- Closing one request needs no typed name, unlike closing a conversation: it ends one thing, loses no words, and the agent can ask again.
