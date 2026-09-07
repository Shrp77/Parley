# Feature Specification: Three-view operator surfaces, and conversations that can be archived and destroyed

**Feature Branch**: `006-three-view-operator`
**Created**: 2026-09-05
**Status**: Draft
**Input**: Owner's report on first use of the built 005 terminal, plus four decisions taken in
that conversation.

## Why this exists

005 put four regions on one screen — projects, agents, conversations, transcript — and moved
between them with Tab. The owner ran it and said: *"having all the 4 windows in one is confusing
and too busy."*

That is a verdict on 005's central design decision, delivered by the only test that could have
produced it. It does not invalidate what 005 built underneath: the seating guards, the enrolment
writer, the withdrawal doors with a name typed back, the containment of a broker that does not
answer. **All of that is kept.** What changes is the arrangement — one thing at a time, drilled
into — and how a transcript reads.

Two further things the owner asked for are not surface work at all. Archiving a conversation and
destroying one do not exist in the protocol: `SPEC.md` has no archived state and no per-conversation
destroy, and the only thing that removes conversation records today is purging an agent (§14). Both
were decided to be **real operator operations in §10.2**, backed by the store, rather than a
preference one surface keeps to itself. That work comes before the views that use it.

And one thing 005 wrote down as impossible has been reopened. 005's User Story 3 was amended
during planning to promise less: a newly added agent would read as *catalogued, not yet
registered*, becoming startable only once somebody opened that repository in an editor. The owner's
instruction is that **adding an agent must produce a startable agent with no further step**. §12.3
still forbids what planning rejected then — a surface registering on a repository's behalf is a
caller-supplied identity, and §16 makes that the whole trust model — so the requirement here is the
outcome, not that mechanism. See FR-011.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - See the deployment without reading four things at once (Priority: P1)

A person opens the operator surface and sees one list: the projects this machine names. Moving the
highlight down that list shows, beside it and without leaving the view, what is in the highlighted
project — which agents are registered to it and what conversations have taken place — so the
question *"which project do I want?"* is answered before anything is opened. Choosing a project
opens that project's own view. Projects are added and removed from here, through the same
description-then-typed-name doors 004 and 005 built.

**Why this priority**: It is the whole complaint. Everything else in this feature is reached
through this view, and a person who cannot find their way to a project cannot use any of it.

**Independent Test**: With two projects named, open the surface; the projects are listed and
nothing else competes for attention. Move the highlight from the first to the second and the
summary beside it changes to the second project's agents and conversations. Choose one and the
project view opens on it. Go back and the same project is still highlighted.

**Acceptance Scenarios**:

1. **Given** a machine naming two projects, **When** the surface opens, **Then** the projects are
   listed, one is highlighted, and a summary of that project's agents and conversations is shown
   beside it.
2. **Given** the projects list, **When** the highlight moves to another project, **Then** the
   summary follows it within the same view, with no navigation and no lost place.
3. **Given** a highlighted project, **When** it is chosen, **Then** the project view opens on that
   project.
4. **Given** the project view, **When** the person goes back, **Then** the projects list is shown
   again with the same project still highlighted.
5. **Given** a machine naming no projects, **When** the surface opens, **Then** it says so in plain
   words and names the one action that changes it.
6. **Given** a project whose broker is not answering, **When** it is highlighted, **Then** the
   summary says the broker is not answering rather than showing an empty project, and the other
   project is still reachable.

---

### User Story 2 - Conversations that can be put away and destroyed (Priority: P1)

An operator marks a conversation archived and it stops appearing in the ordinary list; the fact is
durable, held by the broker, and true for every operator on every surface and after a restart. An
operator destroys a conversation and it is gone, along with every peer's half of it — behind a
description of exactly that consequence and the conversation's own name typed back.

**Why this priority**: It is protocol work, and everything in User Story 4 that shows or hides an
archived conversation depends on it existing. Building the view first would mean building it twice.

**Independent Test**: Archive a conversation through one surface; a second surface, and a fresh
process, both show it as archived. Restart the broker and it is still archived. Destroy a
conversation and no surface can find it, and the store holds no message of it.

**Acceptance Scenarios**:

1. **Given** an open conversation, **When** an operator archives it, **Then** every surface reports
   it archived, and it is still archived after the broker restarts.
2. **Given** an archived conversation, **When** an operator un-archives it, **Then** it returns to
   the ordinary list everywhere.
3. **Given** an agent's tool set, **When** that agent attempts to archive or destroy a
   conversation, **Then** there is no operation there for it to call — the boundary of §10.1 and
   §10.2 is structural, not advisory.
4. **Given** a conversation with messages from three agents, **When** an operator destroys it,
   **Then** they are first shown what will be lost and for whom, must type the conversation's own
   name, and afterwards are told what was destroyed per table.
5. **Given** a conversation somebody is waiting on an answer in, **When** an operator asks to
   destroy it, **Then** they are told what the waiting asker will see before the name is asked for,
   as a purge does (§14).
6. **Given** an archived conversation, **When** an agent in it sends a message, **Then** the
   behaviour is defined and documented rather than incidental (see FR-016).

---

### User Story 3 - Manage the agents of one project (Priority: P2)

Inside a project, a person sees its agents: whether each is running right now, and what its
standing is in plain words. They add an agent, remove one — retire, reinstate or purge, each
through the doors 005 built and none of them weakened — and change whether an agent may be started
without anybody watching. Adding an agent produces an agent that can be started, immediately,
without the person having to go and open that repository themselves.

**Why this priority**: It is the reason the terminal exists rather than a browser: the machine
running a broker is frequently not one somebody has a browser open on. It comes after the
navigation because it lives inside a view that navigation must first reach.

**Independent Test**: In a project with an empty catalogue, add a repository; within seconds it is
listed as an agent that can be started, and starting it works with nobody opening that repository.
Change its startup behaviour and the catalogue reflects it. Retire it, reinstate it, purge it, each
refusing without the exact name typed back.

**Acceptance Scenarios**:

1. **Given** a project's agents, **When** the list is shown, **Then** each agent's row says whether
   it is running now and what its standing is, in words that do not require reading `SPEC.md`.
2. **Given** a repository that is not yet an agent, **When** it is added to the project, **Then**
   the surface first says exactly what it will write and where, and on confirmation writes it.
3. **Given** an agent just added, **When** the agents list next refreshes, **Then** the agent can be
   started, and a question addressed to it starts it, with nobody having opened that repository.
4. **Given** an agent, **When** its startup behaviour is changed, **Then** the project's catalogue
   records the change and the running node honours it without a restart.
5. **Given** an agent that owes an answer, **When** a purge is asked for, **Then** the asker's fate
   is described and accepted before the name is asked for.
6. **Given** the person's own seat, **When** the agents list is shown, **Then** retire and purge are
   not offered for it, or for anybody else's seat.
7. **Given** a repository that is already an agent of another project, **When** it is added,
   **Then** it is refused with the reason and nothing is written.

---

### User Story 4 - Find a conversation and decide what to do with it (Priority: P2)

Inside a project, conversations are listed by name with the time of the last thing said in them,
and the person sorts by either. Beside the list, the highlighted conversation is described: who is
in it and how many messages it holds. From here a person starts a new conversation, archives one,
or destroys one. Archived conversations are not in the list unless the person turns them on.
Choosing a conversation opens it.

**Why this priority**: Depends on User Story 2 for archive and destroy, and on User Story 1 for
being reachable. Ranked with User Story 3 because a project view without either half is not the
view that was asked for.

**Independent Test**: With six conversations of differing names and dates, sort by each and confirm
the order; archive two and confirm the list holds four; turn on *show archived* and confirm it holds
six with the two marked; choose one and the conversation view opens on it.

**Acceptance Scenarios**:

1. **Given** a project's conversations, **When** the list is shown, **Then** each row carries the
   conversation's name and the timestamp of its most recent message.
2. **Given** the list, **When** the person sorts by name and then by date, **Then** the order
   changes accordingly and the highlight stays on the same conversation.
3. **Given** a highlighted conversation, **When** the detail panel is read, **Then** it names the
   agents taking part and the number of messages.
4. **Given** archived conversations exist, **When** *show archived* is off, **Then** they are absent
   from the list; **When** it is on, **Then** they are present and visibly marked as archived.
5. **Given** the list, **When** the person starts a new conversation, **Then** it appears in the
   list and can be opened.
6. **Given** a project with no conversations, **When** the list is shown, **Then** it says so rather
   than showing an empty frame.

---

### User Story 5 - Read a transcript that reads like a transcript (Priority: P2)

A person opens a conversation and can follow it. Each agent has its own colour, the same colour
throughout that conversation. Every message's header — when, from whom to whom, what kind, what
state — stands apart from the words of the message rather than running into them. Times are shown
in the reader's own timezone. The body of a message has the line breaks and paragraphs its author
wrote, and any JSON in it is laid out rather than run together.

**Why this priority**: The transcript is what the whole surface exists to show, and today it is
described by its owner as *"one long ass blob that is not readable by a human."* Ranked below
navigation only because a transcript nobody can reach is worse than one that is hard to read.

**Independent Test**: Open a conversation of at least three agents and twenty messages, including
one carrying a JSON payload and one carrying deliberate paragraph breaks. Every agent's messages
carry a distinct, consistent colour; every header is distinguishable from body text with colour
removed as well as with it; timestamps match the reader's local clock while the stored value stays
UTC; the paragraph breaks survive and the JSON is indented.

**Acceptance Scenarios**:

1. **Given** a conversation with several agents, **When** it is opened, **Then** each agent's
   messages carry a colour distinct from the others', consistent throughout the conversation.
2. **Given** any message, **When** it is rendered, **Then** its header is visually distinct from its
   body by a means that survives colour being stripped.
3. **Given** a message stored with a UTC timestamp, **When** it is shown, **Then** the time
   displayed is the reader's local time, and what is stored is unchanged.
4. **Given** a message whose text contains blank lines and paragraphs, **When** it is shown, **Then**
   those breaks are present.
5. **Given** a message carrying a JSON payload, **When** it is shown, **Then** the JSON is indented
   and line-broken rather than run onto one line.
6. **Given** a conversation with more messages than fit on screen, **When** it is opened, **Then**
   the person can move through it without losing which message they were on.

---

### User Story 6 - Hand the current thing to a browser (Priority: P3)

From any of the three views, one keystroke opens the web console on the same thing the person is
looking at: this project, this conversation, this deployment. Not the console's front page.

**Why this priority**: A convenience over a surface that already works, and the smallest slice
here. It is in scope because the two surfaces are now required to agree, and this is what makes
that agreement useful rather than merely true.

**Independent Test**: On each of the three views, press the key; the address opened names the thing
that was on screen. On a machine with no browser, the surface says so and keeps running.

**Acceptance Scenarios**:

1. **Given** the project view on a project, **When** the key is pressed, **Then** the console opens
   on that project's page.
2. **Given** the conversation view, **When** the key is pressed, **Then** the console opens on that
   conversation.
3. **Given** a machine where no browser can be started, **When** the key is pressed, **Then** the
   surface reports it and continues running.
4. **Given** any view, **When** the surface lists what the view can do, **Then** this key is among
   them rather than being knowledge a person has to have.

---

### Edge Cases

- What happens when a conversation is destroyed while somebody else has it open on another surface?
- What happens when a conversation is archived while an agent in it is mid-exchange?
- What happens when an agent is added to a project whose broker stops answering between the
  description and the confirmation?
- What happens when two operators archive and un-archive the same conversation at once?
- What does *running* mean for an agent attached through an editor window that has been idle for an
  hour — and does the surface distinguish "attached" from "answering"?
- What happens when a conversation holds a message larger than the screen, or one whose text
  contains control characters or terminal escape sequences?
- What happens when a conversation's participants outnumber the distinguishable colours available?
- What happens when the reader's timezone changes while the surface is open?
- What happens to an archived conversation when the retention policy (`prune`) runs?
- What happens when the person goes back from the conversation view to a project that has been
  removed from the manifest underneath them?

## Requirements *(mandatory)*

### Functional Requirements — the protocol

- **FR-001**: The protocol MUST define an archived state for a conversation, held by the broker and
  durable across restarts, such that every operator and every surface reads the same answer.
- **FR-002**: The protocol MUST define an operator operation that archives a conversation and one
  that reverses it, in `SPEC.md` §10.2.
- **FR-003**: The protocol MUST define an operator operation that destroys a single conversation and
  every message in it, in `SPEC.md` §10.2, with its consequence for the conversation's participants
  stated as §14 states a purge's.
- **FR-004**: Neither archiving nor destroying MUST be reachable by an agent. The separation MUST be
  structural — a different service and a different type, as §10.1 and §10.2 are separated today —
  and not merely a naming convention.
- **FR-005**: The names of the new operations MUST NOT collide with any agent operation across the
  §10.1/§10.2 boundary, as the specification's own checks require.
- **FR-006**: Destroying a conversation MUST be recorded in whatever the deployment can still
  answer afterwards, so that an operator asking "what happened to it" is not told nothing.

### Functional Requirements — the surfaces

- **FR-007**: Both surfaces MUST present the same three views over the same operations. Where one
  offers something the other does not, that absence MUST be recorded in the operations contract so
  it is deliberate.
- **FR-008**: The projects view MUST show, for the highlighted project without leaving the view, its
  agents and its conversations, and MUST allow adding and removing a project.
- **FR-009**: The project view MUST show, for each agent, whether it is running and what its
  standing is, in wording that does not use the phrases *catalogued, not yet registered* or *not in
  this catalogue*. The two facts to convey are (a) this agent has never yet announced itself, and
  (b) nothing on this machine is permitted to start this agent.
- **FR-010**: The project view MUST allow adding an agent, retiring, reinstating and purging one,
  and changing whether an agent may be started unattended.
- **FR-011**: Adding an agent MUST leave that agent startable without any further action by the
  person — no opening the repository, no restart, no second command. §12.3 forbids the surface
  registering on the repository's behalf and that prohibition stands; the requirement is on the
  outcome. The evident route is that the machinery which already starts agents on demand starts the
  new one once, so that it registers by its own act, and `plan.md` MUST settle this before it is
  built.
- **FR-012**: Changing an agent's startup behaviour MUST take effect on the running node without
  requiring it to be restarted.
- **FR-013**: The conversations list MUST show each conversation's name and the timestamp of its
  most recent message, and MUST be sortable by either, with the highlight preserved across a sort.
- **FR-014**: A detail panel MUST describe the highlighted conversation: its participants and its
  message count.
- **FR-015**: The conversations list MUST exclude archived conversations unless the view's *show
  archived* option is on, and MUST mark them when shown.
- **FR-016**: The behaviour of an archived conversation that receives a new message MUST be defined
  and documented — whether it un-archives, stays archived, or refuses — rather than left to fall out
  of the implementation.
- **FR-017**: The conversation view MUST assign each participating agent a colour distinct from the
  others' and consistent for the life of that conversation.
- **FR-018**: Every message header MUST be distinguishable from message body text by a means that
  survives colour being removed, since a test that strips colour must lose no information.
- **FR-019**: Timestamps MUST be displayed in the reader's local timezone and MUST remain stored in
  UTC.
- **FR-020**: Message bodies MUST preserve the line and paragraph breaks their author wrote, and
  MUST lay out JSON payloads with indentation and line breaks.
- **FR-021**: Text arriving from an agent MUST NOT be able to alter the screen an operator is
  deciding on — the rendering added here MUST NOT reintroduce the hazard 005 closed.
- **FR-022**: One keystroke, available from every view and listed among what the view can do, MUST
  open the web console on the thing currently being looked at.
- **FR-023**: Every destructive act MUST keep the door 004 and 005 built: a description of what will
  be lost first, an offer to do nothing placed first, and the subject's own name typed back.
- **FR-024**: No region of any view MUST silently truncate. Where something does not fit, the
  surface MUST say so.

### Key Entities

- **Conversation**: gains an archived state. Its **identifier is its name** — `001_initial.sql:342`
  records that the primary key is the name, constrained against `/`, `:` and runs of dots because it
  reaches filenames and headings — so FR-013 asks for no new field, and "sort by name" is an ordinal
  sort on the identifier (`research.md` R5). Participants, message count and last-activity time are
  already returned by §10.2's `conversations()` (`research.md` R6).
- **Agent standing**: the pairing of what the registry knows (has this agent ever announced itself;
  is it attached now) with what the project's catalogue permits (may anything here start it), which
  is what FR-009 must express in one readable phrase.
- **Startup behaviour**: an agent's consent to be started with nobody watching, recorded per project
  in that project's catalogue.
- **View**: one of exactly three — projects, project, conversation — with a defined way back and a
  defined thing it is currently about.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A person who has not seen the surface before can reach a specific conversation's
  transcript from opening the surface in three choices or fewer.
- **SC-002**: At no point does the surface present more than one list requiring the person's
  attention plus one summary of the highlighted item.
- **SC-003**: Adding a repository as an agent results in an agent that can be started, with no
  further human action, within thirty seconds of confirmation.
- **SC-004**: An archived conversation is reported archived by a second surface and by a freshly
  started process, in every case.
- **SC-005**: After a conversation is destroyed, no surface can retrieve it and no message of it
  remains in the store.
- **SC-006**: In a transcript of twenty messages from three agents, a reader can identify the sender
  of any message without reading the header text, and can identify where one message ends and the
  next begins with all colour removed.
- **SC-007**: A message body written with paragraphs is displayed with those paragraphs, and a JSON
  payload of at least five keys is displayed over at least five lines.
- **SC-008**: Every timestamp shown matches the reader's local clock for the same instant.
- **SC-009**: Both surfaces offer the same set of operations, and any difference is named in the
  operations contract.
- **SC-010**: No action is reachable only by a key: everything a key does is also reachable by
  pointer, and every key is listed where the person can find it.
- **SC-011**: The narrowest supported terminal shows every view without any text being cut short.

## Assumptions

- The owner's verdict on the four-region layout is a design decision and not a defect to be tuned:
  the arrangement is replaced rather than adjusted.
- 005's guards are kept unchanged in substance — seating's refusal to take a registered name, the
  withdrawal doors, the containment of an unreachable broker, the neutralising of control characters
  in anything an agent wrote.
- *Running* for an agent means the broker holds an open connection for it, which §4.0 already makes
  the evidence of reachability. No new liveness mechanism is introduced.
- A conversation's "name" is whatever the deployment already identifies it by; this feature does not
  introduce a naming scheme for conversations.
- ~~Colour assignment is per conversation, not global.~~ **Corrected by `research.md` R7.** The
  shared `Palette` already derives colour from a hash of the agent's name, so an agent is one colour
  on every page and in every conversation, and it argues that position-derived colour would repaint a
  transcript whenever somebody joined. That is stronger than FR-017 asked for, so the code is kept and
  this assumption is what was wrong.
- Sorting and the *show archived* toggle are view state, remembered for the session, not stored on
  the broker.
- The keystroke for the browser is chosen from the function keys so it cannot collide with the
  single-letter action keys, and is settled in planning rather than here.
- This supersedes 005's User Story 3 amendment. 005 remains the record of why the easy version was
  rejected; FR-011 is the harder version being required instead.

## Dependencies

- `SPEC.md` changes (FR-001 to FR-006) land before any surface work that reads them, because the
  surfaces are clients of the protocol and building them first means building them twice.
- The store gains what FR-001 needs, under the existing rule that invariants belong in storage
  rather than in application code.
- Both surfaces already share the client library 004 extracted; this feature depends on that split
  holding, and on the contract test that fails the build when the two drift.

## Out of scope

- Any change to how agents talk to each other. This is entirely an operator-facing feature plus the
  two operator operations it needs.
- Retiring the web console or the terminal in favour of the other.
- Search across conversations, or filtering by agent.
- Editing or redacting a message.
- Any change to the message size cap or to what a refusal returns.
