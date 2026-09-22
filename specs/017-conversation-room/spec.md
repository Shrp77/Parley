# Feature Specification: A conversation is a room

**Feature Branch**: `015-file-store` (continued; this repository does not branch per spec — 016 was written here too)
**Created**: 2026-09-21
**Status**: Draft
**Input**: The owner's call on 2026-09-21, after using the terminal against a live deployment: the conversation view
should read like Claude Code with a layout — two columns, a transcript, and one line to speak in.

## Why this exists

The terminal's conversation view today is a transcript and four participation actions on four keys: take delivery,
send a result, clarify, send a request. Three things follow from that shape, and the owner met all three within a
day of installing 0.29.0.

1. **A person had to go and look.** Nothing said work was waiting; finding out meant opening a conversation and
   pressing a key inside it. 0.29.2 put a count on the status line, which tells somebody *that* there is something
   without telling them *what*.
2. **The act came before the words.** Answering meant choosing "send a result" and then writing; asking meant
   choosing "send a request" and then writing. A person thinks in the other order — they know what they want to
   say, and the protocol's four acts are bookkeeping about who owes whom.
3. **What it costs was invisible until afterwards.** A plural ask is N exchanges (§5.5), each owing a Result. The
   bill arrived in `parley flags` the next day.

This replaces the conversation view with a room: who is here down one side, what was said in the middle, and one
line at the bottom to say something. The act is derived from who owes whom, and the cost is shown before Enter.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Say something without choosing a verb first (Priority: P1)

A person opens a conversation, types what they want to say, and presses Enter. The surface works out, for each
recipient, whether that is a Result, a Clarify or a Request, and says so above the line before they commit.

**Why this priority**: It is the feature. Everything else here is furniture around the input line, and without it
the room is a nicer-looking version of the same four-key surface.

**Independent Test**: Seat a person in a conversation where one agent has asked them something and another has
not. Type one line, send it, and read the store: one Result closing the first exchange, one Request opening a new
one against the second.

**Acceptance Scenarios**:

1. **Given** an agent has asked this person something and they hold it, **When** they type an answer and send,
   **Then** it is recorded as a Result, the exchange closes, and the preview said so before Enter.
2. **Given** this person asked an agent and is waiting, **When** they type and send, **Then** it is recorded as a
   Clarify on that exchange and the turn goes back to the agent — never as a Result, which nobody may send on
   their own request.
3. **Given** no exchange is open between them and an agent, **When** they type and send, **Then** a Request opens
   and the preview said "new request, they will owe a result" beforehand.
4. **Given** one line addressed to three agents in three different states, **When** they send, **Then** each
   recipient gets the act its own state calls for, and the preview listed all three.

---

### User Story 2 - Answer or ask back, never guessed (Priority: P1)

Where a person owes a Result, what they type may be the answer — which closes the exchange — or a question they
need settled first, which hands the turn back. The surface offers both and never decides.

**Why this priority**: The one case the derivation cannot reach, and the only one where a wrong guess destroys
something: a Result closes an exchange, and an exchange closed on work that was not done is a record that says the
work was done. Ships with US1 because US1 is unsafe without it.

**Independent Test**: Hold a Request, type a line, and confirm both outcomes are offered with the default named on
screen; take each and read the store for a closed exchange in one case and an open one with the turn moved in the
other.

**Acceptance Scenarios**:

1. **Given** this person owes a Result, **When** they have typed something, **Then** the surface shows which of
   answer-and-close or ask-back it will do, and how to choose the other, before Enter.
2. **Given** they choose to ask back, **When** it sends, **Then** the exchange is still open and the turn is with
   the agent that asked.

---

### User Story 3 - See what is waiting, and read it where it was said (Priority: P2)

The thin column lists what this person must address. Selecting one moves the transcript to that message so the
question is read in its context rather than as a line in a list.

**Why this priority**: It is what the owner asked for after 0.29.2 told them *that* something was waiting. Real
value on its own, and it does not block US1.

**Independent Test**: Seat a person with two outstanding items, select the second, and assert the transcript is
showing that message.

**Acceptance Scenarios**:

1. **Given** outstanding items exist, **When** the conversation opens, **Then** they are listed with who is
   waiting and what kind of thing is owed.
2. **Given** an item is selected, **When** it is activated, **Then** the transcript moves to that message with the
   messages around it visible.
3. **Given** nothing is outstanding, **When** the conversation opens, **Then** the list says so rather than
   standing empty.

---

### User Story 4 - Address some of the room (Priority: P2)

Typing reaches every participant of the conversation. `@` opens a picker and resolves what is chosen into a
recipient, not into text. `@all` is every participant. Commands are reached as `/` and an identifier, each
declaring what it takes; `/add <agent>` is the first of them and widens the recipients to an agent that is not a
participant yet.

**Why this priority**: Without it the room only speaks in chorus, which is the expensive default. It is separable
from US1: the derivation works the same whether the recipient set came from the default or from a picker.

**Independent Test**: Type an ask, narrow it to one agent through the picker, send, and assert exactly one
exchange was opened and that no payload contains the picker's syntax.

**Acceptance Scenarios**:

1. **Given** a conversation with three participants, **When** a person types and sends without addressing anyone,
   **Then** all three are recipients and the preview said three.
2. **Given** the picker has resolved two agents, **When** the message sends, **Then** only those two are
   recipients and the stored payloads contain no `@`, no name list and no routing of any kind.
3. **Given** an agent that is not a participant, **When** it is added through `/add` and a message is sent,
   **Then** it becomes a participant by being party to the new exchange.
4. **Given** `/add` with nothing else written, **When** the person presses Enter, **Then** nothing is sent and the
   surface says why: joining is being asked something, and there is nothing to ask.

---

### Edge Cases

- **The transcript grows while somebody is typing.** Messages arrive every few seconds from agents. Neither the
  reader's position nor the half-written line may move.
- **An exchange closes underneath a draft.** A person types an answer; the conversation is force-concluded, or the
  other party's Result lands first. The act the preview promised is no longer available, and the person must be
  told rather than have something else sent in its place.
- **A recipient leaves the roster mid-draft** — retired, or purged — between the picker resolving and Enter.
- **The seat is released while a draft is open**, by the idle sweep or a broker restart. Nothing may be sent under
  a name the book no longer holds.
- **A message is over the size cap.** Refused before anything is sent, with what was written kept.
- **`@` matches nothing**, or matches an agent that is not a participant and was not added.
- **An ask that begins with `/`** — `/etc/hosts`, a route, a flag somebody is asking about. Decided by FR-020: a
  second `/` makes it text, whatever any command is called.
- **A mistyped command**, `/ad` for `/add`. It is text by FR-021, so it sends — which means a typo becomes an ask
  that every participant must answer. FR-022 is what makes that survivable: the line says it is sending as text
  before Enter, and the preview already says how many exchanges that opens.
- **A command given the wrong parameters** — none where one is required, an agent that does not exist, more than
  it takes.
- **The list is open with something selected when Enter is pressed.** Tab completes, so Enter has to mean
  something else, and what it means is worth choosing rather than inheriting.
- **Deleting back into a match**: `/added` matches nothing, and removing two characters makes `/add` offer itself
  again. The list must come back, which it does only if it is derived rather than dismissed.
- **A single `/` alone**, which is every command and no identifier.
- **A draft command pressed alone**, which cannot act, and an acting command pressed with text beside it, which
  has to say which of the two it did.
- **A conversation with one participant**, where "everyone" and "that agent" are the same set.
- **A conversation this person is not a party to at all**, read as an operator: there is no seat, so there is no
  act to derive and the line cannot send.
- **Sending fails for one recipient and not the others**, which §5.5 already makes N independent sends.

## Requirements *(mandatory)*

### Functional Requirements

**The room**

- **FR-001**: The conversation view MUST present two columns: a narrow one listing the conversation's
  participants, the things this person must address, and the status of the agents; and a wide one holding the
  transcript above a single input line.
- **FR-002**: Each participant MUST be shown in the colour already derived from its name, so that an agent is the
  same colour here, in every other view, and in the web console.
- **FR-003**: Colour MUST NOT be the only thing carrying any fact. Every message keeps a header naming its sender,
  and the view MUST remain fully readable with every colour attribute stripped.
- **FR-004**: The things to address MUST be the ones the protocol defines — a Request this person holds and owes a
  Result on, a Clarify awaiting their reply, and a closure they have been polled on and not answered.
- **FR-005**: Activating an outstanding item MUST move the transcript to the message it refers to, with
  surrounding messages visible.
- **FR-006**: The transcript MUST continue to follow the conversation while the view is open, and MUST NOT move
  the reader's position or disturb unsent text when it grows.

**The line**

- **FR-007**: For each recipient, the act MUST be derived from what is open between them and this person: a Result
  where the recipient asked and this person holds it; a Clarify where this person asked and is waiting; a Request
  where nothing is open.
- **FR-008**: Where this person owes a Result, the surface MUST offer both answering — which closes the exchange —
  and asking back, and MUST show which it will do by default. It MUST NOT choose between them silently.
- **FR-009**: Before sending, the surface MUST show the resolved recipients and what Enter will do to each,
  including how many new exchanges will be opened and that each will owe a Result.
- **FR-010**: One line addressed to several recipients MUST be allowed to resolve to different acts per recipient,
  each correct for that pair.
- **FR-011**: A send that is refused MUST keep what the person wrote.

**Addressing**

- **FR-012**: With nothing addressed, the recipients MUST be every participant of the conversation.
- **FR-013**: `@` MUST open a picker that resolves a chosen agent into a recipient rather than leaving text to be
  parsed later, so that punctuation in prose is never mistaken for addressing.
- **FR-014**: `@all` MUST mean every participant of this conversation, not every agent in the project.
- **FR-015**: Resolved recipients MUST NOT appear in any stored payload. No `@`, no name list, and no routing
  instruction of any kind reaches an agent's context.
- **FR-016**: A message MUST NOT be sent under a seat that is no longer held.

**Commands**

- **FR-017**: The input line MUST support commands of one uniform form: `/` followed by a command identifier,
  followed by that command's parameters. Every command declares its identifier and what parameters it takes, and
  the line parses, completes, refuses and describes all of them by that declaration rather than one at a time.
- **FR-018**: A command's parameters MUST be typed rather than free text. A parameter naming an agent resolves
  through the same picker `@` uses, so what the command receives is a resolved agent and not a string somebody
  spelled.
- **FR-019**: `/` alone MUST list the available commands with what each takes, so that no command exists only as
  something a person has to already know.
- **FR-020**: A command is `/` at the start of the line followed by an identifier containing no further `/`.
  Anything else beginning with `/` is text: `/etc/hosts` is a path somebody is asking about, not a mistyped
  command, and the rule decides that without knowing what any command is called.
- **FR-021**: An identifier that is not a command MUST be treated as text and sent as written. Nothing is refused
  for not being a command, because the identifiers a person might type are not a closed set they can be expected
  to know.
- **FR-022**: Where a draft begins with `/` and is being treated as text, the line MUST say so before Enter. The
  completion list closing is the first and continuous signal of this (FR-029 to FR-031); this is the backstop for
  a draft that stopped matching long ago — `/ad hoc fix needed` has no list open by the time Enter is pressed, and
  the leading slash is still there.
- **FR-023**: A recognised command missing a parameter it requires, or given one it does not take, MUST be refused
  before anything is sent, and MUST say what it expected. This is not FR-021's case: the identifier resolved, so
  the person meant a command and the line owes them the reason it could not run.
- **FR-024**: Commands MUST be told apart by whether they change the draft or perform an act, and a command that
  changes the draft MUST NOT be sendable on its own.
- **FR-025**: `/add <agent>` MUST be a draft command: it widens the recipients to an agent that is not yet a
  participant. It is not sendable alone, because membership is derived (§5.0) — an agent joins by being party to an
  exchange, so adding with nothing to say would have to invent a Request, and every message is answered.
- **FR-026**: A command MAY perform a destructive operation, and the intention is that more operations become
  commands over time. What it MUST NOT do is reach one without the door that operation already has: a description
  of what will go, and the subject's own name typed back. The command is a way to arrive at the door, never a way
  past it.
- **FR-027**: A destructive command MUST declare its door alongside its identifier and parameters, so that the
  door is part of what the command *is* rather than something its handler remembers to open.
- **FR-028**: The guarantee that every destructive operation is reachable only through its door MUST remain
  mechanically checkable as operations move into commands. It is checked today by holding each operation's name to
  one file containing its prompt; a command table is a second route, so the check MUST read the declarations too,
  or it will keep passing while meaning less.

**Completing a command**

The list is not a convenience. Because an unrecognised identifier is sent as text (FR-021), a typed command that
does not resolve costs somebody an ask they must answer — so the remedy is to make typing one blind the unusual
path. Completing from a list is how a command is normally entered, which is what makes the sent-as-text case rare
enough to be survivable rather than a trap standing open.

- **FR-029**: Typing `/` at the start of the line MUST open a list of every command, which narrows as more is
  typed to those whose identifiers still begin with what is there.
- **FR-030**: While the list is open, up and down MUST move the selection and Tab MUST complete the selected
  identifier. Neither may reach past the list to the transcript or to the surface's own navigation while it is
  open, and both MUST return to what they did before once it closes.
- **FR-031**: The list MUST be derived from what is written rather than remembered: it closes when nothing matches
  any longer, and opens again if characters are deleted until something matches once more. No state says "the
  person was completing a command" — there is only the text and what it matches.
- **FR-032**: What decides command-or-text at send MUST be an exact match of the identifier — the text between `/`
  and the first space — and not what the list happens to be showing. `/ad` is text even while the list offers
  `add`, because nothing completed it; `/add @billing` is a command because the identifier resolved.
- **FR-033**: Completing from the list MUST be the ordinary way to enter a command, and the surface MUST NOT
  require anything to be typed in full that it could have completed.
- **FR-034**: One completion mechanism MUST serve both sigils: `/` over the commands and `@` over the agents, with
  the same narrowing, the same keys and the same behaviour when nothing matches. Two completion lists that were
  built separately would eventually behave differently in the same input line.

**Scope**

- **FR-035**: The views above a conversation — the projects list and the project view — MUST be unchanged.
- **FR-036**: Every operation reachable from the conversation view before this change MUST remain reachable after
  it, so that the surface loses nothing by being rearranged.
- **FR-037**: Where an operation is reachable both as a command and as a region's key, the two MUST perform the
  same act behind the same door. Two routes that drifted would be the disagreement `DriftTests` exists to catch
  between surfaces, arriving inside one.

### Key Entities

- **Participant**: an agent or a seated person party to an exchange in the conversation, or that placed a file in
  it. Derived, never a list somebody edits (§5.0).
- **Outstanding item**: something this person owes or is owed an answer to — a held Request, an unanswered
  Clarify, an unvoted closure — with the message it refers to.
- **Draft**: what is written but not sent, with its resolved recipients and, per recipient, the act that will be
  performed and its cost.
- **Command**: an identifier reached as `/` plus that identifier, what parameters it takes and of what kind, and
  whether it changes the draft or performs an act. One declaration per command, which is what the line parses,
  completes, refuses and lists from — so a command added later is a declaration rather than another branch in the
  input line.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A person who has been sent something can see what it is and answer it without leaving the
  conversation view and without choosing an act before writing.
- **SC-002**: Every message sent from the room is recorded as the act the preview named, for every recipient, with
  no case where a Result was written when a Clarify was intended or the reverse.
- **SC-003**: No payload stored by this surface contains addressing syntax, a recipient list, or any other
  routing instruction.
- **SC-004**: A person can tell, before pressing Enter, how many new exchanges they are about to open and that
  each will owe a Result.
- **SC-005**: Messages arriving while somebody is typing never move the reader's position and never alter or lose
  unsent text.
- **SC-006**: The view is fully usable and loses no information when every colour attribute is stripped.
- **SC-007**: Every operation the conversation view offered before this change is still reachable.
- **SC-008**: A person is never able to send a message under a seat they no longer hold.
- **SC-009**: Every command is discoverable from the line itself, takes its parameters the same way, and is
  refused the same way when given the wrong ones — with no command that behaves unlike the others because it was
  added separately.
- **SC-010**: Before pressing Enter, a person can always tell whether a line beginning with `/` will run as a
  command or be sent as text.
- **SC-011**: No command reaches a destructive operation without the door that operation already has, and that
  remains true by a check rather than by review as more operations become commands.

## Assumptions

- **The terminal first; the web console follows.** The console will be brought to match afterwards, with graphics
  it can do and the terminal cannot. The two surfaces are held to parity on *operations* rather than on layout, so
  they may look different in between.
- **The seat is unchanged.** Taking one, losing one to the idle sweep, and the guard on names the registry holds
  all behave exactly as they do now. This changes what a seated person sees and types, not how they become one.
- **No protocol change, no schema change, no wire change.** Every act used here exists: Request, Clarify, Result,
  take delivery. The conversation-assignment rule of §5.0 is untouched — this surface names the conversation it is
  showing, which a person may always do.
- **The status shown in the thin column is operator information** — flags and health read across pairs, which is
  what §10.1 withholds from an agent and §10.2 grants an operator. The terminal is an operator surface and may
  show it; that the same screen also holds a seat is a thing to be deliberate about rather than a contradiction.
- **"Everyone" means the room.** A conversation's participants, not the project's roster. Reaching an agent
  outside the room is `/add`, which is a deliberate act.
- **Outstanding items are this conversation's.** What is waiting elsewhere is already reported on the status line
  by 0.29.2 and in the web console's frame by 0.29.1.

## Out of scope

- The web console's version of this view, which follows afterwards.
- Any change to what an agent's adapter sends or receives.
- Any change to the projects list or the project view.
- Threading, editing a sent message, or deleting one: the log is a record of what was said (§9.4 prunes text, it
  does not rewrite it).
