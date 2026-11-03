# Feature Specification: Sub-conversations and moving exchanges

**Feature Branch**: `032-sub-conversations-and-moves`
**Created**: 2026-11-03
**Status**: Draft
**Input**: Owner, 2026-11-03: "I want to be able to spin off sub-conversations from a conversation. Example; one of the agents told me to fix some stuff in the infrastructure. I came across some issues and want to have a targeted conversation only on that. And then I want to be able to take some conversation entries and MOVE them into a new conversation because the topic of the entries doesn't align with the topic of the conversation they were in."

**Decided by the owner, 2026-11-03**:

- A move is a real move in the record, not a re-filing on screen and not a copy.
- Exchanges still in progress move too. The work carries on in the new conversation, and the agent is told the next time it collects, without being woken for it.
- A spin-off opens with the message it came from quoted in the box, for the person to edit. Nothing is sent until they press Send.

**This changes the protocol on purpose.** SPEC.md §5.0 says "Nothing can move an exchange from one conversation to another", and principle 2.10 says a request carries its conversation, as does everything it causes. Their reason is that an agent must not be able to split a line of work where the person cannot follow it. This feature keeps that reason and changes the rule: only the person may move, and every move is recorded and shown.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Spin off a focused conversation (Priority: P1)

The person is reading a conversation. An agent has told them to fix something in the infrastructure, they hit problems, and they want to talk about only that. From the message they start a new conversation. The box opens with that message quoted. They cut it down, add their question, choose who it goes to and send it. The new conversation and the old one each show the link to the other.

**Why this priority**: It is the everyday case, it carries no risk to existing records, and it delivers value without moves.

**Independent Test**: Spin off from one message and send. The new conversation holds a Request with exactly the words that were in the box, both conversations show the link, and the original conversation has not changed in any other way.

**Acceptance Scenarios**:

1. **Given** a message from an agent, **When** the person chooses to spin off from it, **Then** the box opens with that message quoted and attributed, that agent is ticked and nobody else is, and nothing has been sent.
2. **Given** the box open with a quote, **When** the person edits it, names the conversation and sends, **Then** the agent receives exactly what was in the box, in a new conversation under that name.
3. **Given** a spin-off was sent, **When** the person opens either conversation, **Then** the page says how the two are linked and leads to the other.
4. **Given** the person leaves without sending, **When** they look at the project, **Then** no conversation was created and no link exists.
5. **Given** the send is refused, **When** the page returns, **Then** the words, the name and the link they would have made are all kept, so fixing them does not lose the link.
6. **Given** the person is reading a conversation and no one message fits, **When** they start a sub-conversation from the conversation itself, **Then** the box opens empty and the link is still recorded.
7. **Given** a conversation that was spun off, **When** the person reads the conversations list, **Then** it reads `[its name] - spinoff from [parent's name]`, and one with no parent reads as its name alone.

---

### User Story 2 - Move entries to a new conversation (Priority: P2)

The person sees that some entries are about something else. They pick those messages and choose to move them to a new conversation. Before anything happens the page lists every exchange and message that will go, including any that must go along with what they picked. They name the new conversation and confirm. The entries now live there, and each conversation says what moved and where.

**Why this priority**: It is the second thing the owner asked for and the larger change, because it alters the record and a protocol rule.

**Independent Test**: Move one finished exchange out of a conversation of three. The source shows the other two and a note, the destination shows the moved one and a note, and every moved message has the same words, author, time and order as before.

**Acceptance Scenarios**:

1. **Given** the person picks one message of a longer exchange, **When** they choose to move it, **Then** the page shows the whole exchange will go, and says why.
2. **Given** a list of what will go, **When** the person has not confirmed, **Then** nothing has changed.
3. **Given** a confirmed move to a new conversation, **When** it completes, **Then** the source no longer shows those entries and notes what moved and where, and the destination shows them and notes where they came from.
4. **Given** a confirmed move to an existing open conversation, **When** it completes, **Then** the moved entries sit among its own in the order the broker first recorded them.
5. **Given** a move is under way, **When** anything stops it, **Then** nothing has moved: a move happens whole or not at all.
6. **Given** entries were moved by mistake from a conversation still open, **When** the person moves them back, **Then** they return to the source and both moves stay on record.
7. **Given** a conversation or its export, **When** anybody reads the export after a move, **Then** it records what moved in or out, when, and by whom.

---

### User Story 3 - Work in progress keeps going after a move (Priority: P2)

An exchange that is moved may still be open: an agent owes an answer, or the person does. The work carries on in the new conversation. Nobody loses their place, and the agent finds out the next time it collects.

**Why this priority**: The entries most worth moving are often the live ones. Without this the person would have to wait for answers before tidying up.

**Independent Test**: Move an open exchange. The agent is not started. At its next collection it is told the new conversation name, and its Result on that exchange lands in the new conversation.

**Acceptance Scenarios**:

1. **Given** an open exchange an agent owes, **When** it is moved, **Then** the agent is not woken for that, and its next collection tells it the exchange's new conversation.
2. **Given** that agent then answers, **When** the answer is recorded, **Then** it lands in the new conversation with no action by the agent beyond answering.
3. **Given** an exchange waiting on the person, **When** it is moved, **Then** it stays on the person's waiting lists, under the new conversation, and can still be answered.
4. **Given** a finished exchange, **When** it is moved, **Then** no agent is told anything.
5. **Given** an agent that now holds requests in two conversations, **When** it sends a new Request naming neither, **Then** it is refused with the conversations it may name, as SPEC §5.0 already says.

---

### Edge Cases

**What goes along with what is picked**

- A send to several agents is drawn as one entry but is one exchange per agent. Picking it takes all of them.
- A Request to a role (§5.5) is one exchange per agent, sharing a group. Picking any one takes the whole group, so the count of who has answered stays true.
- Parley does not record which exchange caused which, so nothing else is inferred or pulled along. An agent's follow-up exchange stays behind unless the person picks it, and the list says so.

**Which conversations can take part**

- Into a closed conversation: refused, because a closed conversation stays closed (§5.0).
- Out of a closed conversation: allowed, and it does not reopen it. A move out of a closed conversation can only hold finished exchanges.
- A destroyed conversation has nothing to move, and its name can never be a destination.
- A roadmap item's conversation and the facilitator's `owns-` conversations are refused in both directions, with the reason. Both have rules of their own about who opens them and what closes them (§5.7, principle 2.11).
- A new name that is taken, destroyed, reserved for a roadmap item or not a valid name: refused at confirmation, with the choices kept.
- Destination the same as source: refused.
- Everything moved out: the source stays, open or closed as it was, holding only its notes. The person may close it.

**What else a move touches**

- A closure poll running in the source or the destination is voided and every answer discarded, as new activity does (§5.0), because who would be asked has changed.
- Membership is derived (§5.0), so it follows. An agent with nothing left in the source stops being its participant, unless it placed a file there.
- Files stay in the source. The list says which, and the destination's note leads to them. An agent told of a move is told that files named in that exchange stayed behind, under the old conversation name, where it can still fetch them.
- An exchange settled by another (§5.6) stays linked to it if only one of the two moves, and a surface says which conversation the other is now in.
- Length records (§17 question 16) follow their messages. None is lost.
- Retention (§9.4) treats moved messages as the destination's from then on. A move cannot make a message prunable that the floor protects.
- Exports taken before a move no longer match the conversation. This is accepted, and the move record is what explains the difference.

**Timing and failures**

- An agent sends on an exchange while it is moving: the message lands either before the move or after it, in the conversation the exchange is then in. It is never lost or refused because of the move.
- A page open on the source or destination catches up by itself, keeping the reader's place as following already does.
- An address carrying a picked message that has since moved: the page says it may have moved, and to look at the conversation again.
- A message that has been pruned cannot be picked. Its exchange may still move when another of its messages is picked, and the list says how many messages it has left.
- The broker cannot be reached: nothing changes, and the surface says so.

**Links between conversations**

- A conversation has at most one parent and any number of children, to any depth.
- Nothing cascades. Closing or destroying one does not close or destroy another.
- A destroyed parent or child still reads as linked, by its name. Following the link says it was destroyed.
- A conversation made by a move is linked to its source, as a spin-off is, and reads the same way in a list.
- A list names only the immediate parent, however deep the chain goes.
- A destroyed parent's name is still shown in a list, because the link outlives it.
- A long name and parent are cut to fit the same way any list cuts a name, and the conversation's own name comes first so it is the last thing lost.

## Requirements *(mandatory)*

### Functional Requirements

**Spin-off**

- **FR-001**: The person MUST be able to start a new conversation from a message, and from a conversation with no particular message.
- **FR-002**: Starting from a message MUST open the box with that message quoted and attributed, and tick its author if the author is an agent. It MUST tick nobody else.
- **FR-003**: Nothing MUST be sent, created or linked until the person sends. The agent MUST receive exactly the words in the box.
- **FR-004**: What opens in the box MUST come from the broker's record of the named message. Words or recipients in a link MUST NOT be able to fill it, because an agent's message can contain live links.
- **FR-005**: The ordinary limits and refusals for a person's Request MUST apply unchanged (§5.6, §7).
- **FR-006**: The broker MUST record the link between the two conversations, and every surface showing either MUST show the link and lead to the other.
- **FR-007**: The link MUST be recorded before anything is sent, so a Request can never be delivered unlinked. If it cannot be recorded, nothing MUST be sent and the surface MUST say why. If the Request then reaches nobody, the link MUST be taken back and the name left free.

**Move**

- **FR-008**: The person MUST be able to pick one or more messages in a conversation and move their exchanges to a new conversation or an existing open one.
- **FR-009**: The unit of a move MUST be the whole exchange, with the group rules under Edge Cases. Before confirming, the surface MUST list every exchange and message that will go, say which were added and why, say which are still open, and name the files that stay.
- **FR-010**: A move MUST need a description and a deliberate act, as other serious acts do: the list above, plus typing the new name or choosing the destination in that same step. A single key or click MUST NOT do it.
- **FR-011**: A move MUST be all or nothing, and MUST NOT change any moved message's words, author, recipient, time, delivery state or place in the broker's order.
- **FR-012**: The broker MUST keep a record of every move: what moved, from where, to where, when and by whom. It MUST hold no message text. Both conversations MUST show it where the entries were, and exports MUST carry it.
- **FR-013**: Moves MUST follow the refusals under Edge Cases, each with a sentence saying why.
- **FR-014**: A move MUST void a closure poll running in either conversation.
- **FR-015**: Moved entries MUST be movable again, including back unless their source has closed, with each move on record. Surfaces MUST say that limit where they say a move can be undone.

**Agents**

- **FR-016**: Only the person MAY move an exchange or record a link. These MUST be operator operations (§10.2), and no agent operation, adapter or tool MUST be able to do either or to read the move and link records beyond FR-018.
- **FR-017**: A move MUST NOT wake an agent or start a turn, and MUST NOT count as work waiting for it.
- **FR-018**: At its next collection, an agent party to a moved exchange that is still open MUST be told the exchange's identifier, its old and new conversation, and that files stayed behind. It is a notice like a file's gone notice: not a message, nothing owed back. Agents party only to finished exchanges MUST NOT be told.
- **FR-019**: An agent MUST NOT learn from a link or a notice anything about exchanges it is not party to. A link MUST give it no access to the other conversation.
- **FR-020**: An agent's Clarify or Result on a moved exchange MUST take the exchange's current conversation, and the holding rules of §5.0 MUST read current conversations.

**Surfaces and record**

- **FR-021**: Every operation MUST be on both the terminal and the web console (§10.2). The command line MUST refuse them by naming the terminal.
- **FR-022**: What is waiting on the person, on every surface, MUST reflect a move at its next read.
- **FR-023**: An upgrade MUST keep every row of a live store, and the change MUST arrive as a new migration.
- **FR-024**: SPEC.md MUST be changed in the same work: principle 2.10, §5.0, §9.2, §9.3, §10.2, §14 and the version in §18, so that one rule stands and the old wording is gone.
- **FR-025**: The conversations list, and every list a conversation is chosen from, on the terminal and the web console, MUST show a conversation with a parent as `[its name] - spinoff from [parent's name]`. This MUST be how the list displays it and MUST NOT change the conversation's name, which agents, links and exports go on using. The two names MUST be told apart on screen, so a name that happens to contain those words cannot pass for a spin-off.

### Key Entities

- **Conversation link**: One conversation's parent, and how it came about (a spin-off or a move). Outlives either side being destroyed, holding names only.
- **Move record**: One act by the person: the exchanges that moved, the source, the destination, when and who. No message text.
- **Move notice**: What one agent is told about one open exchange that moved. Waits for its next collection, and is then gone.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: From reading a message to a sent spin-off takes the person under 30 seconds, with no copying and pasting.
- **SC-002**: After any move, every moved message is identical to before in words, author, recipient, time and relative order, in all cases tested.
- **SC-003**: No message is ever in neither or both conversations, including when a move is stopped part-way or an agent sends during one.
- **SC-004**: A move starts no agent turn, in all cases tested, and every agent holding a moved open exchange is told at its very next collection.
- **SC-005**: Every open exchange that was moved can be finished normally, with no agent or person action beyond answering it.
- **SC-006**: Before confirming, the person can see 100% of what will move. Nothing moves that the list did not name.
- **SC-007**: From either side of a move or spin-off, the person reaches the other in one step, and can always tell what left a conversation and where it went.
- **SC-008**: No agent-facing operation can perform a move, record a link or read another conversation through one.
- **SC-009**: In the conversations list, the person can tell every spin-off and its parent without opening anything.

## Assumptions

- Files do not move in this feature. Moving a file between conversations can be added later, and nothing here prevents it.
- Roadmap item and `owns-` conversations are refused both ways for now, rather than designed for. The owner can lift this later from use.
- The person names a new conversation under the same rules as any other, and a surface may suggest a name but MUST NOT apply one without them seeing it.
- Quoting an agent's words into a Request sends them under the person's name, by the person's own act with the words in front of them. That is the existing rule for anything a person sends, and they stay inert data (§13).
- The review gate applies as usual, and this is a protocol version change.
- Depends on nothing unmerged. Feature 027 also claims a migration, so this one takes the next free number when it is built. The pull request also carries the conversation page's panel, which overlaps the same page and has no requirement here of its own: the owner's request for it was separate.
