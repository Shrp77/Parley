# Feature Specification: Answer in the conversation

**Feature Branch**: `031-answer-in-the-conversation`
**Created**: 2026-11-02
**Status**: Draft
**Input**: Owner, 2026-11-02: "Combine the Inbox and Conversation views - from the conversation I want to see what messages are requiring my input and to go to them in the conversation chain - that way I stay in the conversation and can see the additional context around the message."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - See and reach what needs me (Priority: P1)

In a conversation, the person sees what is waiting on them and moves from one to the next within the transcript, reading each with what surrounds it.

**Acceptance Scenarios**:

1. **Given** messages waiting on the person, **When** they read the conversation, **Then** each is marked on the message itself, and a list names them.
2. **Given** several, **When** they press next, on the web or on the terminal, **Then** the transcript moves to the next, oldest first and round again.
3. **Given** a Request sent to several agents and the person, **When** they follow its link, **Then** it reaches the message.

### User Story 2 - Answer it there (Priority: P1)

The person answers from the message, without leaving the conversation.

**Acceptance Scenarios**:

1. **Given** an agent's question or Request, **When** the person presses Reply, **Then** the box beneath is set to answer that message.
2. **Given** an agent's Request, **When** they reply, **Then** they can leave it open, or close it with an outcome.
3. **Given** a stale message, **When** they press Close without answering, **Then** it closes and the conversation stays open.
4. **Given** a refused reply, **When** the page comes back, **Then** the words, the message they answer and the outcome are all still set.

### User Story 3 - Get there from the Inbox (Priority: P2)

From the Inbox, which lists what waits across conversations and projects, the person goes straight to a message in its conversation.

### Edge Cases

- The transcript redraws itself every few seconds: nothing here makes it redraw when nothing has changed, and what is typed is safe, because the box is outside it.
- Nothing is waiting: no list, no marks, and the terminal says so.

## Requirements *(mandatory)*

- **FR-001**: A message waiting on the person MUST be marked on the message, on the web.
- **FR-002**: Both surfaces MUST offer a way to go to the next one.
- **FR-003**: The web MUST let the person reply, close with an outcome, or close without answering, from the message.
- **FR-004**: The person MUST choose which message their words answer (SPEC 5.6).
- **FR-005**: A refused reply MUST keep the person's words and what they answer.
- **FR-006**: The Inbox MUST link each item to its message in its conversation.

## Success Criteria *(mandatory)*

- **SC-001**: The person can answer everything waiting in a conversation without leaving it.

## Assumptions

- **The Inbox page stays, as the list across conversations and projects, and keeps its forms.** A conversation shows one conversation, so nothing else answers "what is waiting on me anywhere". Removing its forms is for the owner to decide once they have used this.
- **Taking delivery still happens on the Inbox and the terminal, not on a conversation's page.** A collection takes everything for the seat, not one conversation, so the page would mark as read what it does not show. The Inbox badge therefore stays up until the Inbox is opened. Delivery by conversation is a wire change, for the owner to ask for.
- **The terminal's Result (`Alt+a`) still offers only what this seat collected.** `Alt+q` and `Alt+j` offer everything waiting.
