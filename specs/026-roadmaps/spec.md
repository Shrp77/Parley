# Feature Specification: Roadmaps, and every agent in the Room

**Feature Branch**: `019-layout` (stays on the branch the last seven features share; no new branch was cut)
**Created**: 2026-10-06
**Status**: Draft
**Input**: User description: "Every agent in the project is listed in a conversation's Room, participants marked apart. When agents coordinate to get something done, the list that results must not be lost in a transcript's history: an agent proposes it, the person confirms it, and it persists as a roadmap, a first-level entity of the project. The person opens a roadmap to see each item's state and what is next, to instruct agents to do an item, and to move to the item's own conversation and back."

## Clarifications

### Session 2026-10-06

- Q: Can the person set an item's state by hand? → A: No. State is only ever derived. An item that is dead or finished elsewhere is removed or reworded by editing the roadmap.
- Q: What happens to an item's conversation when the item is removed or the roadmap is revised? → A: The conversation is kept as an ordinary conversation, marked as formerly belonging to that item. Nothing in flight is destroyed.
- Q: When is an item's conversation created? → A: The first time the item is started, or opened and written into. Until then the item only reserves its identity, so a long roadmap does not fill the conversation list.
- Q: How long can a roadmap be? → A: No cap on a roadmap, its items or their text. The person's review is the control. The message cap still applies wherever an item's text is sent to an agent.
- Q: How does an agent put a proposal forward? → A: A new, dedicated agent operation that takes the title and items. The proposal is stored apart from messages and listed as waiting on the person; it is not a transcript line.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A coordinated list becomes a roadmap (Priority: P1)

Several agents work out how to get something done and end up with a list of steps. Today that list is a message in a transcript, and it scrolls away. An agent that has arrived at such a list *proposes* it as a roadmap. The proposal appears to the person as something waiting on them. The person reads it, may reword, remove, reorder or add items, and confirms it. Only then does a roadmap exist.

**Why this priority**: This is the whole point of the feature. Nothing else has anything to show without it.

**Independent Test**: An agent proposes a three-item list; the person sees it waiting, confirms it unchanged, and finds a roadmap of three items listed under the project. Before confirmation no roadmap exists anywhere, and an agent's attempt to create or change one directly is refused.

**Acceptance Scenarios**:

1. **Given** an agent has proposed a list, **When** the person opens the project, **Then** the proposal is shown as waiting on them, with who proposed it, where it was proposed, and every item in full.
2. **Given** a waiting proposal, **When** the person edits items and confirms, **Then** the roadmap holds the person's version, records that it began as that agent's proposal, and the proposal is no longer waiting.
3. **Given** a waiting proposal, **When** the person declines it, **Then** nothing is persisted, the proposing agent is told it was declined, and the conversation it came from is unchanged.
4. **Given** a confirmed roadmap, **When** an agent tries to add, remove, reorder or retitle items or mark one done, **Then** the attempt is refused and the roadmap is unchanged.
5. **Given** a confirmed roadmap, **When** an agent believes it should change, **Then** it can propose a revision, which reaches the person the same way and changes nothing until confirmed.

---

### User Story 2 - See the state of a roadmap and what is next (Priority: P1)

The person opens a roadmap and sees each item with its state: not started, in progress, waiting on an agent's answer, waiting on the person, blocked (and why), or done. The roadmap says which item is next to work on.

**Why this priority**: A list that cannot say where it stands is only a note. This is the use the owner named as key.

**Independent Test**: With a roadmap of four items in four different situations, the page shows four different states with no one typing them in, and names exactly one item as next.

**Acceptance Scenarios**:

1. **Given** an item whose conversation has an open request an agent has collected, **When** the person opens the roadmap, **Then** the item reads in progress and names the agent holding it.
2. **Given** an item whose conversation ended with a blocked result, **When** the roadmap is opened, **Then** it reads blocked and says the blocker the agent gave.
3. **Given** an item whose agent has asked the person something, **When** the roadmap is opened, **Then** it reads waiting on the person, and the roadmap also counts it as waiting on them at project level.
4. **Given** items ahead of a not-started item are all done, **When** the roadmap is opened, **Then** that item is named next. **Given** an earlier item is not done and a later item depends on it, **Then** the later item is not named next.
5. **Given** every item is done, **When** the roadmap is opened, **Then** it reads complete and names no next item.
6. **Given** an item's conversation cannot be read (for example the broker does not answer), **When** the roadmap is opened, **Then** the item says its state could not be read, never that it is not started.

---

### User Story 3 - Instruct agents to do an item, from the roadmap (Priority: P1)

From a roadmap item the person tells the relevant agent or agents to do it. What goes out is an ordinary request from the person, in the item's own conversation, to the agents the item names (or whom the person ticks), carrying the item's text. The person can add their own words.

**Why this priority**: It turns the roadmap from a record into the place work is driven from.

**Independent Test**: From a not-started item the person chooses an agent and confirms; the item's conversation is created, the agent receives one request in it, and the item moves to in progress.

**Acceptance Scenarios**:

1. **Given** a not-started item with an intended agent, **When** the person chooses to start it, **Then** a request goes to that agent in the item's conversation and the person's words are the record of who directed it.
2. **Given** an item with no intended agent, **When** the person chooses to start it, **Then** they are asked which agents, from every agent in the project, and nothing is sent until they choose.
3. **Given** an item already in progress, **When** the person chooses to start it again, **Then** they are told it is already with that agent and nothing is sent unless they choose to send a further line.
4. **Given** an item and several agents, **When** the person addresses more than one, **Then** each receives its own request, as in any conversation.

---

### User Story 4 - Every item is its own conversation, reachable both ways (Priority: P2)

Each item has a conversation of its own, which comes into being the first time the item is started or written into. From an item the person goes to that conversation; from a conversation that belongs to an item the person goes to the roadmap, positioned on the item. A conversation says it belongs to an item and shows the item's text.

**Why this priority**: It keeps the conversation as the unit of work, with the roadmap as the map over it. Useful only once stories 1 to 3 exist.

**Independent Test**: Open a roadmap, go to item 2's conversation, see the item named there, go back, and land on item 2.

**Acceptance Scenarios**:

1. **Given** a roadmap item that has been started, **When** the person opens it, **Then** they land in that item's conversation, with the transcript, Room and input line as in any conversation. **Given** an item not yet started, **Then** opening it shows the item's text and offers to start it, and no conversation exists until it is started.
2. **Given** a conversation belonging to an item, **When** the person asks for its roadmap, **Then** the roadmap opens on that item.
3. **Given** a conversation that belongs to no item, **When** the person asks for its roadmap, **Then** they are told it has none.
4. **Given** the conversation list of a project, **When** it is read, **Then** conversations that belong to roadmap items are recognisable as such and name their roadmap.

---

### User Story 5 - Every agent in the project is in the Room (Priority: P2)

The Room of any conversation lists every agent in the project, not only those who have spoken. Agents who have taken part are marked apart from those who have not, by an icon that is not colour alone, and a legend says what the marks mean. The ticks decide who a typed line goes to, as they do today. Ticking an agent who has not taken part is allowed and brings them in, as addressing them already does.

**Why this priority**: It lets the person bring in someone new without leaving the conversation, and it makes the roadmap's agent choice (story 3) use the same list. It is independent of roadmaps.

**Independent Test**: In a conversation between two agents of a four-agent project, the Room shows four agents with two marked as participants; ticking a third and sending a line delivers it to the third.

**Acceptance Scenarios**:

1. **Given** a project of four agents and a conversation of two, **When** the Room is drawn, **Then** all four are listed, the two participants carry the participant mark, the other two do not.
2. **Given** an agent that has joined the project since the conversation began, **When** the Room next refreshes, **Then** that agent is listed as a non-participant.
3. **Given** an agent that was retired, **When** the Room is drawn, **Then** it is not offered as a recipient and is shown as retired if it took part.
4. **Given** the person's own seat and Parley, **When** the Room is drawn, **Then** they are shown apart from the agents as they are today.
5. **Given** a narrow terminal, **When** the Room is drawn, **Then** no row is cut off; the participant mark and the name survive before any status text does.

---

### Edge Cases

- A proposal arrives for a project that already has a roadmap of the same title: the person is told (FR-009b), and may confirm it as a revision of that roadmap or as a new one.
- An agent proposes an empty list: refused. There is no length limit on a proposal, a roadmap or an item.
- An item's text, plus any words the person adds, would exceed the message cap when started: the start is refused with the cap named and nothing is sent, and the person shortens the item. The cap is not moved for roadmaps.
- An item is confirmed naming an agent that is not in the project: refused when confirming, not when starting.
- The item's conversation is force-closed, archived or destroyed by the person: the item says so; it is not shown as done unless a Result said done.
- Two proposals for one roadmap at once: the person sees both, and confirming one marks the other out of date rather than silently overwriting.
- An agent that proposed a roadmap is later retired or purged: the roadmap stays; its provenance names the agent as it was.
- A roadmap item's text edited after work started: the conversation keeps the words originally sent, and the item says the text changed since.
- An item finished or made irrelevant outside Parley: the person edits the roadmap to remove or reword it; there is no mark that sets state by hand, so a roadmap never shows a state no conversation supports.
- A very long roadmap: the list scrolls and the next item is reachable by one key.
- A broker older than this feature is reached: the surface says roadmaps are not available there, and acts as no one.

## Requirements *(mandatory)*

### Functional Requirements

**Room**

- **FR-001**: The Room MUST list every agent in the conversation's project, with the participants marked apart by an icon and a legend.
- **FR-002**: The ticks in the Room MUST remain the recipients of a typed line, with the existing rules unchanged, including that the last recipient cannot be unticked.
- **FR-003**: Ticking and sending to a non-participant MUST bring them into the conversation exactly as addressing them does today.
- **FR-004**: The Room MUST NOT offer retired agents as recipients.
- **FR-005**: Both the terminal and the web console MUST show the same list from the same derivation.

**Proposing and confirming**

- **FR-006**: An agent MUST be able to propose a roadmap, through one dedicated agent operation that is not a message and opens no exchange: a title and an ordered list of items, each with text and optionally the agent it is meant for and the items it depends on.
- **FR-006a**: The proposing tool MUST guide the agent with a template that keeps a proposal factual, point-oriented and short: a short title; one line per item, led by a verb and naming the thing to be done; the intended agent and the items it needs only where known; no background, reasoning or narrative. Detail belongs in the item's conversation once it is started. The template is guidance, not a limit (see the Assumptions), and what an agent writes outside it is still accepted.
- **FR-006b**: Titles and items are single-line text. A line break in either is refused with a sentence saying so, as an ownership line's is.
- **FR-007**: A proposal MUST NOT be a roadmap. Nothing is persisted as a roadmap until a person confirms it.
- **FR-008**: A proposal MUST be shown to the person as work waiting on them on every surface, with its proposer and its source conversation.
- **FR-009**: The person MUST be able to edit, reorder, add and remove items, and confirm or decline, on every surface.
- **FR-009a**: Proposing, confirming and editing MUST each refuse, with a sentence, an intended agent that is not in the project, and an item that needs one that is not live and earlier. Editing a confirmed roadmap MUST refuse the same, so a reorder or removal that leaves an item needing a later or removed item is not accepted. A removed item is therefore never a dependency of a live one.
- **FR-009b**: A proposal that names a roadmap to revise names it by title only (an agent never reads roadmaps). The person chooses at confirmation whether it revises an existing roadmap or becomes a new one; the title is a hint and never resolves anything by itself. A proposal whose title matches an existing roadmap is shown with that match.
- **FR-010**: Declining MUST tell the proposer and change nothing else.
- **FR-011**: An agent MUST be able to propose a revision to an existing roadmap, which is treated as a proposal under FR-007.
- **FR-012**: No operation available to an agent MAY create, change, reorder, delete or mark done a roadmap or one of its items. This MUST be held structurally, by the operation not existing on the agents' side, and checked by a test that fails when it does.
- **FR-013**: Parley, the facilitator, MUST NOT propose or confirm roadmaps. Its permitted acts remain those the constitution lists.

**State and next**

- **FR-014**: Each item's state MUST be derived from its conversation's exchanges and the registry, never typed in by anyone, and no operation sets or overrides it, with at least: not started, in progress, waiting on an agent, waiting on the person, blocked, done, closed by the person, and could not be read.
- **FR-015**: A blocked item MUST show the blocker the agent gave.
- **FR-014a**: An item whose text was edited after its conversation began MUST say so, from the item's own edit record and never by comparing words, because the person may change the item's words before sending them.
- ~~**FR-014b**~~: **Dropped, and recorded as a decision rather than an omission.** `install.ps1 -Force` ends the broker and every adapter and installs one build of every tool, so on one machine a surface never meets a broker older than itself. A surface reaching a broker on another machine, through the SSH tunnel SPEC.md §16 describes, can; the project health warning that names a broker of another release says so. Only the store outlives an install, and it is upgraded by migration. Owner's call, 2026-10-24.
- **FR-016**: A roadmap MUST name its next item: the first not-started item, in order, whose dependencies are all done. When none qualifies it MUST say why (complete, or everything remaining is waiting or blocked).
- **FR-017**: A roadmap waiting on the person MUST be counted with the project's other work waiting on them.
- **FR-018**: A state that cannot be read MUST be shown as such and never as not started.

**Instructing**

- **FR-019**: The person MUST be able to start an item from the roadmap, which opens the item's conversation with the item's words in the box and only its intended agent ticked. Nothing is sent until the person sends; what they send goes as a request from the person to the agents ticked.
- **FR-020**: Starting MUST use the person's seat and the existing rules of a person's line; it MUST NOT be an operator instruction and MUST appear in the transcript as the person's words.
- **FR-021**: Starting an item already in progress MUST say so and send nothing without a further deliberate choice.
- **FR-022**: Choosing agents MUST use the whole project's agents, the same list as the Room.

**Items as conversations**

- **FR-023**: Every item MUST have at most one conversation of its own, whose identity is derived, reserved when the roadmap is confirmed, and unique within the project. No agent may start that conversation, by request or by placing a file. The conversation MUST be created the first time the item is started or written into, and not before; an unstarted item has none and shows as not started.
- **FR-023a**: Removing an item MUST NOT delete, close or archive its conversation. The conversation stays, belongs to no item, and says it formerly belonged to that item.
- **FR-023b**: An edit, or a confirmation as a revision, that removes an item whose work is still open MUST be described before it is saved, and saved only on a second, deliberate act, because the agent holding the work is not told. A roadmap that could not be read is never taken to hold no open work.
- **FR-024**: A conversation MUST be recognised as belonging to an item by the record of that relationship and never by a marker in any message payload.
- **FR-025**: The person MUST be able to go from an item to its conversation and from that conversation to its roadmap and item, on every surface.
- **FR-026**: Conversation lists MUST show which conversations belong to roadmap items.

**Persistence and operations**

- **FR-027**: Roadmaps, items, proposals and their provenance MUST be persisted by the broker's store, and the rules that matter (an item belongs to one roadmap, one conversation per item, no change without a person's confirmation) MUST be enforced in storage as well as in code.
- **FR-028**: Every operator operation added MUST be available on the terminal and the web console, and every agent operation added MUST be on the agents' service only. The command line adds none: SPEC.md §10.2 keeps it to what a script or an installer reads, and the owner's rule since 019 is that what a person does to a deployment is the terminal's. A decision recorded after the plan assumed otherwise.
- **FR-029**: Roadmaps MUST survive restart of the broker and of every agent, and MUST be included in a project's export, destruction and retention rules.
- **FR-030**: The protocol document MUST be written first and the code MUST match it before review; any place they disagree is recorded as a decision, not an assumption.

### Key Entities

- **Roadmap**: A first-level entity of one project. Has a title, an ordered list of items, a record of where it began (proposer and conversation, or the person), and a history of confirmed revisions.
- **Roadmap item**: A step in a roadmap. Has text, an order, optional intended agents, optional dependencies on other items, and at most one conversation, created when work on it begins. Its state is derived, not stored.
- **Roadmap proposal**: An agent's suggested roadmap or revision awaiting the person. Holds the proposed content as written. Becomes a roadmap or revision on confirmation, or is discarded on decline or when superseded.
- **Item conversation**: The existing conversation, with a recorded relationship to one item.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A list an agent proposes is available as a roadmap within two actions by the person after they open the project, and is findable again at any later time without searching a transcript.
- **SC-002**: For a roadmap in any mix of states, every item's displayed state agrees with its conversation in 100% of cases checked, with no state entered by hand.
- **SC-003**: A person can go from the roadmap to any item's conversation and back in two key presses or clicks each way, on every surface.
- **SC-004**: A person can start an item and have the agent receive its request in one deliberate action after choosing agents.
- **SC-005**: No test finds a route by which an agent changes a roadmap without a person's confirmation.
- **SC-006**: In every conversation, every agent of the project is listed in the Room, and a non-participant can be brought in without leaving the conversation.
- **SC-007**: Roadmaps are intact after the broker is restarted.

## Assumptions

- Agents propose; Parley does not. The description said "an agent (or Parley)". Parley proposing would be a fourth act beyond the three the constitution (6.0.0) limits it to, so it is left out. Adding it is a constitution amendment for the owner to decide, not an implementation detail.
- A proposal reaches the person through the same route as other work waiting on them (the waiting-on-you list), rather than as a message in a transcript.
- Item order is the intended order; an item depends on those listed, and by default on nothing. "Next" respects dependencies when given and order otherwise.
- Items are worked as requests like any other: the latest result decides: `done` makes an item done and `blocked` makes it blocked, and a person's close makes it closed by the person.
- One roadmap item has one conversation; an item meant for several agents sends one request each within it.
- Nothing is released, but live stores are kept: schema 1 is frozen and any schema change is a new migration that upgrades a store in place. The protocol and the wire change freely, since every tool on a machine is installed together; protocol and version numbers move per this repository's rules.
- Roadmaps are per project and never cross projects.
- The template (FR-006a) is what keeps roadmaps brief, since no cap does. It lives in the proposing tool's description and in the standing instructions every agent receives, so it reaches agents that never read the plugin.
- Having no cap on a roadmap is a reduction in a control that the constitution's terseness principle names, and is recorded as one, as the ownership statement's was. It is tolerable here because a proposal is read by the person and not by an agent; the text that reaches an agent travels as a request and keeps the message cap. If proposals prove large in practice, a cap should be set from that evidence.
- The roadmap is a record and a map, not an orchestrator: it never sends anything by itself, and nothing in it assigns work without the person's act.
- Surfaces reach the store only through the broker, as they do today.
