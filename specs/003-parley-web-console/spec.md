# Feature Specification: Parley Web Console

**Feature Branch**: `003-parley-web-console`
**Created**: 2026-08-30
**Status**: Draft
**Input**: User description: "We need a web UI for Parley that shows all the projects, registered agents per project, provides instructions on how to register a new agent and allows the user to see the conversations taking place in the scope of the projects. The conversations show up as group chats with each agent posting a message to the chain and each series of conversations happening on a timeline."

## Framing

Two facts from `SPEC.md` decide the shape of this feature before any requirement is written.
Both are quoted rather than paraphrased, because getting either wrong produces a console that
looks right and misinforms the person reading it.

**This is an operator surface, and the specification already says so.** §5.0:

> So a conversation is visible in aggregate to the **operator** (§10.2) and to nobody inside
> it. That asymmetry is deliberate: the human coordinating several repositories has a
> legitimate need to see the whole shape of a discussion, and no agent does.

The console is therefore not a new disclosure. It renders what §10.2 already grants a person,
in a form a person can actually read. Nothing it shows may become reachable by an agent.

**A conversation is not a group chat, and rendering one as a room would misrepresent it.**
Also §5.0:

> The distinction is between *membership* and *a room*. A conversation is a set of pairwise
> exchanges that its participants say belong together; it is not a channel everyone reads.

**The group chat is a presentation construct, and it is the right one.** A conversation genuinely
does grow to several participants over time, and its exchanges genuinely do sit on a timeline, so
a chat transcript is the form a person reads fastest. That is the whole reason to build a console
rather than another command, and the reader is the operator — who §5.0 says is entitled to the
whole shape of the discussion.

What the presentation must not do is quietly erase who saw what. In an ordinary group chat every
member has read every message; in a Parley conversation no agent has seen anything but its own
exchanges. An unannotated thread would tell the operator that `inventory` had read what `billing`
said, when `inventory` never saw it, and the operator would then reason about a disagreement that
neither agent could have had.

So: the chat, annotated. Each message names the pair it belongs to, and participants who were not
party to it are visibly not party to it. The annotation withholds nothing from the operator — it
is there so that what they read is what actually happened.

**The console's capabilities are a declared interface, not a list in this document.** Two of them,
in `Parley.Core`: one for what a conforming broker exposes to an operator console, and one for what
an agent must be able to receive from the operator. Together they are broker V1, and an
implementation either satisfies them or does not compile against them.

They are two rather than one because the split is load-bearing. An agent must never acquire the
operator's commands — retiring and purging are a person's judgements about a deployment, and the
codebase already keeps them off the type an agent's software is handed. One interface carrying both
would undo that in the first refactor.

This document says what each interface is for, and what must never appear on either. It does not
restate their members. A second copy of a declaration is a copy that drifts, and the compiler
cannot check prose.

## Clarifications

### Session 2026-08-30

- Q: When the underlying data changes — presence flips, a new answer lands in a conversation being read — what should the console do? → A: List views refresh themselves; an open transcript stays as loaded and says when it has fallen behind.
- Q: A project's broker speaks a protocol version the console does not — what happens? → A: That project is shown as unusable with both versions named; every other project renders normally.
- Q: Can the operator take a conversation out of the console, and in what form? → A: Both — the rendered transcript with its annotations intact, and the raw messages as the operator surface returns them.
- Q: What must the console guarantee about how it can be read? → A: Keyboard navigable and screen-reader legible, with pair and visibility conveyed as text rather than by colour or position alone.
- Q: What may the console record in its own logs about what it served? → A: Requests and errors only — never message text, never payloads.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Read a conversation end to end (Priority: P1)

An operator coordinating several repositories knows a discussion happened — a boundary was
contested, an answer looked wrong, a decision was made — and needs to see the whole of it.
They open the console, choose the project, find the conversation, and read it as a time-ordered
sequence of exchanges, each showing who asked whom, what was asked, what came back, and what
each participant could see.

**Why this priority**: This is the capability nothing else provides. The roster is already
available from the command line; the shape of a discussion is not, and §5.0 names it as the
operator's legitimate need. A console that only did this would be worth building.

**Independent Test**: Seed a project with a conversation spanning three participants and
several exchanges, open the console, and confirm the operator can reconstruct who asked what,
in what order, and who saw which message — without reading a database or running a command.

**Acceptance Scenarios**:

1. **Given** a conversation in which `orders` asked `billing`, then `orders` asked `inventory`
   under the same identifier, then `billing` and `inventory` exchanged one, **When** the
   operator opens that conversation, **Then** all three exchanges appear in time order, each
   labelled with its two participants.
2. **Given** the same conversation, **When** the operator views the message `billing` sent to
   `orders`, **Then** the view states that `inventory` did not see it.
3. **Given** an exchange that is still open, **When** the operator views the conversation,
   **Then** the exchange is shown as awaiting an answer rather than omitted or shown as
   concluded.
4. **Given** an exchange that was ghosted, **When** the operator views the conversation,
   **Then** it is shown as ghosted with the reason, rather than as an ordinary close.
5. **Given** a conversation containing a message at the length cap, **When** it is rendered,
   **Then** the full text is shown without truncation by the console.
6. **Given** a message whose text contains markup and text imitating the console's own labels,
   **When** it is rendered, **Then** it appears as the message's content and does not alter the
   page or appear to be the console speaking.
7. **Given** any conversation, **When** the operator views it, **Then** no control is offered
   that would change the state of an exchange, an agent, or the store.

---

### User Story 2 - See every project and who is registered in it (Priority: P2)

An operator wants to know the state of their deployment: which projects exist on this machine,
which agents are registered in each, what each says it owns, and whether each is running,
startable, stranded or retired.

**Why this priority**: It is the navigation for User Story 1 and is independently useful — it
answers "is my deployment healthy" at a glance, which today requires a command per project.
It is second because the information is already obtainable; the conversation view is not.

**Independent Test**: With two projects configured and one broker stopped, open the console and
confirm it lists both projects, shows the agents of the running one with their presence, and
reports the stopped one as unreachable without blocking the page.

**Acceptance Scenarios**:

1. **Given** a machine whose configuration names three projects, **When** the operator opens
   the console, **Then** all three are listed.
2. **Given** a project whose broker is running, **When** the operator selects it, **Then** every
   registered agent is listed with its name, what it says it owns, and its presence.
3. **Given** a project whose broker is not running, **When** the operator opens the console,
   **Then** that project is shown as unreachable and the others remain usable.
4. **Given** two projects that each contain an agent named `orders`, **When** both are listed,
   **Then** the two are shown as distinct and neither project's data appears under the other.
5. **Given** an agent an operator has retired, **When** the project is listed, **Then** it is
   shown as retired rather than omitted.

---

### User Story 3 - Learn how to register a new agent (Priority: P3)

Somebody adding a repository to an existing deployment needs to know exactly what to do: what
identifies an agent, what the launch configuration must contain, which project endpoint it
belongs to, and how to confirm it worked.

**Why this priority**: Valuable and self-contained, but it is guidance rather than live state,
and the rollout it serves happens rarely. It is last because the first two must be right for it
to be worth reading.

**Independent Test**: Give the console's instructions to somebody who has not registered an
agent before, and confirm they can register one without consulting any other document.

**Acceptance Scenarios**:

1. **Given** a selected project, **When** the operator opens the registration instructions,
   **Then** the instructions name that project's endpoint rather than a generic placeholder.
2. **Given** the instructions, **When** they are followed for a new repository, **Then** the new
   agent appears in that project's roster.
3. **Given** the instructions, **When** they are read, **Then** they state that the agent's name
   comes from the launch environment and cannot be supplied by a call.
4. **Given** any project, **When** the instructions are displayed, **Then** no resolved
   credential value appears anywhere in them.

---

### User Story 4 - Act on what you find (Priority: P2)

An operator reading the console finds something wrong — an agent describing a boundary it no longer
owns, a discussion neither side will close, a repository that has been decommissioned — and fixes
it from where they found it rather than from somewhere else.

**Why this priority**: Seeing without acting sends the operator to a command line with what they
just learnt held in their head, which is where mistakes happen. It is P2 rather than P1 because
reading is what nothing else provides, and because acting is the half that can destroy something.

**Independent Test**: Retire an agent from the console and confirm it reads as retired; reinstate
it and confirm it does not; instruct an agent to change what it says it owns and confirm the
change appears after that agent next starts.

**Acceptance Scenarios**:

1. **Given** an agent that is running, **When** the operator retires it, **Then** it is shown as
   retired and its history is intact.
2. **Given** a purge, **When** the operator asks for it, **Then** what will be destroyed is stated
   and confirmation is required before anything is.
3. **Given** a discussion neither participant will close, **When** the operator force-closes it,
   **Then** it closes and the log records that a person closed it.
4. **Given** an instruction sent to an agent, **When** the conversation log is read afterwards,
   **Then** the instruction is attributed to the operator and is not presented as peer traffic.
5. **Given** an instruction telling an agent to change what it says it owns, **When** that agent
   next starts, **Then** the roster shows the new statement — because the agent changed its own
   launch configuration rather than the console writing behind it.

---

### Edge Cases

- **No projects configured.** A deployment configured entirely by explicit paths names no
  projects. The console must say so and explain how to name one, rather than showing an empty
  page indistinguishable from a broken one.
- **A broker is unreachable.** One project's broker being down must not prevent the others from
  being read, and must be reported as unreachable rather than as a project with no agents.
- **Brokers at different versions.** A fleet is upgraded one broker at a time, so a machine
  may hold projects at two protocol versions at once. This is a normal transient state, not
  a fault, and is contained the same way an unreachable project is.
- **Duplicate agent names across projects.** Names collide freely across projects because each
  is a separate store. Nothing may key on a name alone.
- **A conversation identifier claimed by a non-participant.** An identifier is a claim by the
  sender, not a verified fact, and an agent may join a discussion by naming it. The console
  must show participation as derived from exchanges, not assert that membership was agreed.
- **A single-exchange conversation, and an exchange with no conversation at all.** Most
  exchanges stand alone. The view must handle one exchange without pretending it is a thread.
- **Clock differences between machines.** Agents run on different machines, so a timeline
  ordered on wall-clock timestamps can misorder events. Ordering must be stated so the operator
  knows what they are reading.
- **An instruction to an agent that is not running.** The machine's node starts it, exactly as it
  would for a question from a peer. An instruction to an agent on a machine that is switched off
  has nowhere to go, and must say so rather than appearing to have been delivered.
- **An agent that refuses an instruction.** It may. The operator is a person and not a superior
  process, and an agent that declines is behaving correctly rather than failing.
- **A very long conversation.** A discussion of many exchanges must remain readable and must not
  require the operator to load all of it at once.
- **An agent with no `owns` line.** Some agents have not said what they own. The view must show
  that it is absent rather than inventing or omitting the agent.
- **A message whose text contains markup or envelope-like framing.** Message text is untrusted
  input written by an agent. Rendering must not allow it to alter the surrounding page or
  imitate the console's own labelling.

## Requirements *(mandatory)*

### Functional Requirements

**Project first, always — this rule governs every requirement below.**

- **FR-032**: A project MUST be selected before anything else is shown, and every view
  after that point MUST be scoped to that one project and MUST name it. There is no view of
  agents, conversations, messages or search results that is not the view of exactly one
  project. The only page that is not project-scoped is the list of projects itself.

  Two projects may each hold an agent called `orders`, and their conversations are held in
  different stores by different brokers. A view that merged them would show one list of
  colliding names with nothing to say which environment each belonged to — and the operator
  would draw conclusions about a deployment that does not exist. Selecting the project first
  is what makes that impossible rather than merely discouraged.

**Projects**

- **FR-001**: The console MUST list every project named in the configuration of the machine it
  runs on, and MUST NOT require the operator to supply paths or endpoints for them.
- **FR-002**: The console MUST treat each project as a separate deployment: no view may combine
  agents, exchanges or conversations from two projects into one list.
- **FR-003**: The console MUST NOT introduce a project identifier into any message, stored
  record, or exchange between broker and agent. Project membership remains which endpoint an
  agent connects to.
- **FR-004**: The console MUST show a project whose broker cannot be reached as unreachable, and
  MUST keep every other project usable while it is.

- **FR-026**: Where a project's broker speaks a protocol version the console does not, the
  console MUST show that project as unusable and name both versions, and MUST leave every
  other project readable.

**Agents**

- **FR-005**: For a selected project, the console MUST list every registered agent with its
  name, the machine it is registered on, what it says it owns, and its presence.
- **FR-006**: The console MUST distinguish retired agents from unreachable ones, and MUST show a
  retired agent rather than hiding it.
- **FR-007**: The console MUST show when an agent has no stated `owns` line, and MUST NOT
  substitute a description of its own.

**Conversations**

- **FR-008**: The console MUST present a conversation as a time-ordered sequence of the pairwise
  exchanges that carry its identifier.
- **FR-009**: Every message the console renders MUST identify the two agents party to its
  exchange.
- **FR-010**: The console MUST make visible, for each message, which participants in the
  conversation could see it and which could not.
- **FR-011**: A chat-style transcript is the expected presentation of a conversation. It MUST
  carry the attribution of FR-009 and the visibility of FR-010 inline, within the thread itself,
  rather than in a separate view the operator has to go and open.
- **FR-012**: The console MUST show the state of each exchange — open, answered, concluded,
  orphaned or ghosted — and MUST NOT render an unresolved exchange as a resolved one.
- **FR-013**: The console MUST show the responder identity recorded for each message, so that an
  answer produced by a script, a person, or a weak model is distinguishable from one produced by
  a capable model.
- **FR-014**: The console MUST state the basis on which it orders events, and MUST NOT present an
  ordering across machines as more precise than the underlying timestamps support.
- **FR-015**: Within the selected project, and never across projects, the console MUST allow
  an operator to locate a conversation without knowing its identifier in advance — at
  minimum by participant and by time. There is no cross-project search (FR-032).
- **FR-016**: The console MUST render message text as data. Text originating from an agent MUST
  NOT be able to alter the structure of the page or imitate the console's own labels.

**Legibility**

- **FR-029**: Every view MUST be navigable by keyboard and legible to a screen reader.
- **FR-030**: The pair attribution of FR-009 and the visibility of FR-010 MUST be carried
  as text. Colour, position or iconography MAY reinforce them and MUST NOT be the only
  thing carrying them — a reader who cannot see the colour would lose exactly the fact
  that stops the transcript misleading them.

**Taking a conversation away**

- **FR-027**: The console MUST let an operator take a conversation away in readable form:
  the rendered transcript, with the pair attribution of FR-009 and the visibility of
  FR-010 intact. An export that drops the annotations is the misleading artefact those
  requirements exist to prevent, one step further from anyone who could catch it.
- **FR-028**: The console MUST also offer the messages as the operator surface returns
  them, unrendered, for machine use.

**Registration guidance**

- **FR-017**: The console MUST provide instructions for registering a new agent that are specific
  to the selected project, naming that project's endpoint.
- **FR-018**: The instructions MUST state that an agent's identity comes from its launch
  configuration and cannot be supplied by any call.
- **FR-019**: The instructions MUST describe how to confirm a registration succeeded.
- **FR-020**: The console MUST NOT display a resolved credential value anywhere, in instructions
  or elsewhere. Credentials appear only as the references they are configured as.

**Freshness**

- **FR-024**: List views — projects, agents, and the conversation list — MUST refresh without
  operator action, and MUST show how current what they display is.
- **FR-025**: An open transcript MUST NOT change under the reader. When messages have arrived in
  the conversation being read, the console MUST say so and offer to reload, rather than appending
  to the view silently.

**Logging**

- **FR-031**: The console MUST NOT write message text or message payloads to its own logs.
  Requests and errors may be logged. §9.4's retention governs the store, and a copy of a
  message in a web log is governed by nothing — it would outlive the prune meant to
  reclaim it.

**Boundary**

- **FR-021**: The console MUST be an operator surface only. It MUST NOT expose any of its data
  through a path an agent can reach, and MUST NOT be bound into the server an agent's client
  launches.
- **FR-022**: The console MUST NOT require the broker to accept connections from anything beyond
  the trusted path the deployment already permits.
- **FR-023**: The console acts on the deployment as well as reading it, through the broker
  interface and no other route. Whatever that interface declares, the console may do; whatever it
  does not, the console cannot. There is no second path to a broker and no direct path to a store.
- **FR-033**: An action that destroys something MUST state what will be destroyed before it
  happens, and MUST require the operator to confirm. Purge removes an agent and everything that
  referred to it; an operator who meant retire and clicked purge cannot be given it back.
- **FR-034**: An instruction sent to an agent MUST be recorded as coming from the operator, and
  MUST NOT appear in the message log as though a peer agent sent it. The log's value is that it
  says who said what; an instruction disguised as peer traffic makes a discussion unreadable
  afterwards and puts words in an agent's mouth that it never chose.
- **FR-035**: The agent-facing interface MUST NOT carry any operator command. An agent that could
  retire or purge another agent would hold authority no agent has.

### Key Entities

- **Project**: A named deployment on this machine — one store, one endpoint, one set of agents.
  Known only to the person and the tools they run; never to the protocol.
- **Agent**: A named participant registered with one project's broker, with a machine, a stated
  ownership line, a tier, and an observed presence.
- **Conversation**: An identifier chosen by an agent, grouping exchanges that belong to one line
  of discussion. Membership is derived from the exchanges that carry it, never declared.
- **Exchange**: One question and its resolution between exactly two agents, including any
  clarifications. The unit a conversation is made of, and the unit visibility is scoped to.
- **Message**: One question, clarification, answer or notification within an exchange, carrying
  its author, its responder identity, and its text.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: An operator who knows two participants and roughly when a discussion happened can
  find it and read it in full in under two minutes, without running a command or opening a file.
- **SC-002**: Given a rendered conversation, an operator can correctly state which participants
  saw a given message, in 100% of cases, without consulting the specification.
- **SC-003**: No message is ever shown to imply readership by an agent that was not party to its
  exchange — zero occurrences across the whole view.
- **SC-004**: A project whose broker is stopped is reported as unreachable within five seconds,
  and every other project remains readable.
- **SC-005**: A deployment of at least twenty agents with twelve months of accumulated exchanges
  remains navigable: any conversation is reachable within three interactions of opening the
  console.
- **SC-006**: Somebody who has never registered an agent can register one successfully on the
  first attempt using only the console's instructions.
- **SC-007**: The console exposes nothing an agent can read: an agent attempting to reach any of
  its data through its own client obtains nothing.
- **SC-009**: No destructive action is ever completed without the operator having been shown what
  it would destroy — zero occurrences of a purge or force-close taking effect on one click.
- **SC-008**: A transcript taken out of the console and read somewhere else still states, for
  every message, which participants saw it — in 100% of exports.

## Assumptions

These are the reasonable defaults chosen where the description did not specify. Each is a
decision that can be reversed, not a discovery.

- **The console acts, it does not only read.** An earlier draft scoped this version to reading
  alone. That was reversed deliberately: an operator who can see that an agent is wrong and must
  then go elsewhere to do anything about it is being shown a problem and denied the fix. What the
  console may do is whatever the broker interface declares.
- **The projects are this machine's.** The list of projects is a per-machine configuration file
  by design, so the console shows the projects configured on the machine it runs on. Reading
  another machine's deployment is out of scope.
- **The trusted path is not widened.** Anything able to reach a broker can claim to be any agent,
  so the transport is confined to a trusted path. The console is assumed to run where the
  operator already has that access, and is not an authentication boundary for the broker. It is
  not exposed to a network the deployment does not already trust.
- **The group chat is the presentation.** A chat transcript on a timeline is how a conversation
  is rendered, because it is the form an operator reads fastest. The single constraint on it is
  that attribution and visibility are annotated inline, so the transcript reports pairwise
  exchanges rather than implying a room everyone read. Argued in the Framing section above.
- **Registration guidance is instructions, not automation.** The console explains how to
  register; it does not perform a registration, because an agent's identity must come from the
  launch configuration of the process that will act as it.
- **Existing operator capability is sufficient.** The console is assumed to be able to read
  projects, agents, conversations and messages through the operator surface that already exists.
  If any view here requires something that surface does not offer, that is a protocol change and
  belongs in `SPEC.md` before it belongs here.
