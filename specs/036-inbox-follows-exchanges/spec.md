# Feature Specification: The Inbox follows an exchange after it is answered

**Feature Branch**: `036-inbox-follows-exchanges` (not created yet: the working tree holds uncommitted work from 035)
**Created**: 2026-11-06
**Status**: Draft
**Input**: User description: "The Inbox follows an exchange after the person answers it. Today the Inbox lists only
exchanges the person is waited on for; once answered, the row disappears and the person loses track of it. Add three
views: Waiting on you, In flight (with a status chip for where the agent stands), and Closed (within a window set in
Parley's settings, default 7 days, with a way to see older). Every row opens its conversation and offers a Follow up.
No hiding of closed rows for now. The terminal gets the same views and setting. SPEC.md §5.6 says what the Inbox shows."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - See what is in flight after answering (Priority: P1)

The person replies to an agent's question, or sends an agent a request. Today the row vanishes, and the person has to
hunt through conversations to see whether anything happened. Instead the row moves to an **In flight** view. A chip says
where the agent stands: not running, working, idle with no reply yet, cannot be started, open outside Parley, or
stalled. When the agent asks something back, the row comes back to **Waiting on you**. When the agent replies, it moves
to **Closed**.

**Why this priority**: losing track of an answered question is the problem the owner raised. Everything else builds on
it.

**Independent Test**: reply to a question in the Inbox. The row appears under In flight with a chip. When the agent
replies, it moves to Closed; when it asks back instead, it returns to Waiting on you.

**Acceptance Scenarios**:

1. **Given** an agent's question waits on the person, **When** the person replies to it, **Then** the row leaves Waiting
   on you and appears under In flight, naming the agent and the conversation, with a status chip.
2. **Given** the person sent an agent a request (from any page, or the "which are still open" button), **When** the
   person opens In flight, **Then** that request is listed until the agent replies.
3. **Given** a row is in flight and the agent's turn is running, **When** the page is shown, **Then** its chip says the
   agent is working.
4. **Given** a row is in flight and the agent asks the person something back, **When** the page next refreshes, **Then**
   the row is under Waiting on you, not In flight.
5. **Given** a row is in flight, **When** the agent sends its Result, **Then** the row moves to Closed with the outcome.
6. **Given** the agent cannot be started, or is open outside Parley, **When** the page is shown, **Then** the chip says so
   in words that tell the person nothing will happen until somebody starts or prompts it.

---

### User Story 2 - Look back at what was closed (Priority: P2)

The person wants to see what came of questions and requests they took part in, after the fact. A **Closed** view lists
exchanges the person was part of that ended within a window, newest first, with how each ended: Done, Answered,
Received, Blocked (and why), settled by the agent, or closed by the person. A Result the person sent appears here at once.

**Why this priority**: it is the "archive" the owner asked for, and it gives the person a way back to an answered
question without keeping anything new.

**Independent Test**: send a Result from the Inbox. It appears under Closed with the outcome chosen. A question the agent
settled appears there as settled.

**Acceptance Scenarios**:

1. **Given** the person sent a Result, **When** they open Closed, **Then** the exchange is listed with the outcome they
   chose, and it is not listed under In flight.
2. **Given** an agent settled one of its questions to the person, **When** the person opens Closed, **Then** it is listed
   as settled by that agent.
3. **Given** an exchange closed longer ago than the window, **When** the person opens Closed, **Then** it is not listed,
   and a "show older" control lists it.
4. **Given** nothing closed within the window, **When** the person opens Closed, **Then** the page says so plainly and
   still offers "show older".

---

### User Story 3 - Follow up from the Inbox (Priority: P2)

From any row in any view, the person can open the conversation where it happened, or write a follow-up to that agent
without leaving the Inbox. The follow-up is a new request to that agent in the same conversation, and it then shows up
under In flight.

**Why this priority**: the owner's complaint was that the Inbox separates the person from the conversation. A link and a
follow-up box close that gap.

**Independent Test**: on a Closed row, write a follow-up and send it. A new request reaches that agent in the same
conversation, and the new exchange is listed under In flight.

**Acceptance Scenarios**:

1. **Given** any row, **When** the person chooses to open its conversation, **Then** the conversation opens at that
   exchange.
2. **Given** a Closed row, **When** the person sends a follow-up, **Then** the agent receives a new request in the same
   conversation, and the row's original exchange stays closed.
3. **Given** a follow-up the broker refuses, **When** the page comes back, **Then** it says why and the words are still
   in the box.

---

### User Story 4 - Choose how far back Closed goes (Priority: P3)

The person sets how many days the Closed view covers in Parley's settings. The default is 7 days.

**Why this priority**: a sensible default makes the feature usable without it. The owner wants it configurable.

**Independent Test**: change the setting to 2 days. An exchange closed 3 days ago leaves the Closed view and is still
found under "show older".

**Acceptance Scenarios**:

1. **Given** no setting was ever made, **When** the person opens Closed, **Then** it covers 7 days and says so.
2. **Given** the person sets 30 days on the web console's settings page, **When** they open Closed in the terminal,
   **Then** it also covers 30 days.
3. **Given** the person enters a value that is not a whole number of days in the allowed range, **When** they save,
   **Then** it is refused, with the range named, and the old value stands.

---

### User Story 5 - The same in the terminal (Priority: P3)

The terminal's view of what waits on the person offers the same three views, chips, links to the conversation and
follow-up, and reads the same setting.

**Why this priority**: the console and the terminal must not drift. It can follow once the web console has it.

**Independent Test**: the same exchanges appear in the same views with the same chips on both surfaces.

**Acceptance Scenarios**:

1. **Given** a reply sent from the web console, **When** the person opens the terminal, **Then** it is in flight there
   too, with the same chip.

---

### User Story 6 - Open a referenced exchange where it is mentioned (Priority: P2)

Agents refer to exchanges by number ("Thanks for the answers on exchange #351"). Today that number is plain text, and the
person has to find exchange 351 by hand. Instead, every reference written the agreed way is a link. Clicking it opens a
panel right under that line showing the exchange: who asked whom, what each side said, and how it ended or who it waits
on, with a link to open it in its conversation. Clicking again closes it.

Agents are told the form to use. When they mention a numbered exchange, they write `exchange #351`, or for several
`exchanges #349, #350 and #351` (or "or"). A number written any other way stays plain text, so ordinary numbers in a
message ("16,000 characters", "2026") never become links.

**Why this priority**: it removes the hunt for a referenced exchange, the same gap as the rest of this feature, at the
point where the person is reading.

**Independent Test**: show a message containing "exchange #351" where exchange 351 exists in the project. Clicking the
reference opens a panel under that line with exchange 351's messages; "351" written without the prefix is not a link.

**Acceptance Scenarios**:

1. **Given** a message says "exchange #351" and exchange 351 is in the same project, **When** the person clicks the
   reference, **Then** a panel opens directly below that line showing exchange 351, and a second click closes it.
2. **Given** a message says "exchanges #349, #350 and #351", **When** it is shown, **Then** each of the three numbers is
   its own link.
3. **Given** a message says "on 351", "#351" with no "exchange" before it, or "exchange 351" with no "#", **When** it is
   shown, **Then** nothing is a link.
4. **Given** a reference to an exchange number that does not exist in the project, **When** the person clicks it,
   **Then** the panel says there is no such exchange in this project. Nothing else changes.
5. **Given** a reference inside code or inside a link the agent wrote, **When** it is shown, **Then** it stays as written.
6. **Given** a referenced exchange whose words were removed by retention, **When** the panel opens, **Then** it shows what
   the store keeps (when it opened and closed, and how it ended) and says the words are gone.
7. **Given** an agent connects to Parley, **When** it reads how to use Parley, **Then** the instructions tell it to write
   numbered exchanges in this form.

---

### Edge Cases

- **A Result the person sent**: the exchange is closed, so nothing is in flight for the person. It goes straight to
  Closed. It may show the agent's present state as a courtesy, worded so it never claims the agent is working on the
  answer.
- **An agent asks back on an exchange in flight**: it is waiting on the person again, and it is listed under Waiting on
  you only, never in two views at once.
- **One request sent to several agents**: each exchange is its own row, in flight until its own agent replies.
- **A request to a role** that one member took: the row names whoever holds it.
- **Messages removed by retention**: a closed exchange is listed only while a message on it still shows the person was
  on it; once every message is pruned, nothing the store keeps says whose it was, so it is not claimed as theirs. A
  reference to such an exchange still opens, showing when it opened and closed and how it ended, with no quotation.
- **An archived conversation**: its exchanges still appear in every view.
- **A moved exchange**: it is listed under the conversation it is in now, and the link goes there.
- **A project the broker cannot be reached for**: its rows cannot be read. The page names the project and says so,
  rather than showing an empty view as if nothing were in flight.
- **An older node that does not report turns**: the chip says the state is unknown, never "idle" or "stopped".
- **The facilitator**: its questions are listed like any other agent's. Follow-up is not offered to the facilitator,
  because a person speaks to an agent only with a Request and the facilitator is not given work.
- **Many closed exchanges**: the Closed view stays usable with hundreds in the window (paged or limited, with a count).

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The Inbox MUST offer three views: *Waiting on you*, *In flight* and *Closed*, each with a count, and MUST
  open on Waiting on you.
- **FR-002**: *Waiting on you* MUST list exactly what it lists today: every open exchange on which an agent waits on the
  person.
- **FR-003**: *In flight* MUST list every open exchange on which the person waits on an agent, whether the person opened
  it with a request or handed the turn back with a reply.
- **FR-004**: An exchange MUST appear in at most one view at any moment, decided by who the broker says is waiting on
  whom, or by its being closed.
- **FR-005**: Each in-flight row MUST carry a status chip, taken from the agent's present state as the broker reports
  it: *Working* (a turn is running), *Idle* (running, no turn running and no reply yet), *Running* (its turns are not
  reported), *Not running* (a node can start it), *Cannot be started now*, *Open outside Parley* or *Unknown*. A flag the
  broker raised on the exchange (*Stalled*, *Not collected yet*, *Stopped: a permission was refused*, *Retired*) MUST be
  shown alongside the agent's state rather than replacing it. (The broker keeps no record that a start is under way, so
  there is no *Being started*; see research R2.)
- **FR-006**: A chip MUST never claim more than the broker reported. In particular, nothing may say an agent is working on
  the person's message: only that a turn is running.
- **FR-007**: *Closed* MUST list every exchange the person was part of (as the one who asked, or the one asked) that closed
  within the window, newest first, with its outcome: Done, Answered, Received, Blocked with its kind, settled by the agent,
  or closed by the person (with the reason, when one was given).
- **FR-008**: *Closed* MUST be derived from what the broker already keeps about each exchange. The feature MUST NOT add
  an archive flag, a per-person record of what was seen, or any other stored state about the Inbox.
- **FR-009**: *Closed* MUST offer a way to see exchanges closed before the window, and MUST say which window it covers.
- **FR-010**: Every row in every view MUST link to its conversation, at that exchange.
- **FR-011**: Every row MUST offer a follow-up box (the shared text box, with the same keys as every other) that sends the
  row's agent a new Request in the row's conversation. It MUST NOT add to or reopen the row's own exchange. It is not
  offered for the facilitator.
- **FR-012**: The window MUST be a setting on Parley's settings page, a whole number of days from 1 to 365, defaulting to
  7. It MUST be the same setting for the web console and the terminal on this machine.
- **FR-013**: Views MUST follow changes without a manual reload, as the Inbox does today, and MUST NOT reload over a box
  the person is typing in.
- **FR-014**: There MUST be no way to hide or dismiss a closed row (owner decision, 2026-11-06; to be revisited).
- **FR-015**: Reading any view MUST NOT deliver, close, settle or otherwise change anything. Only sending changes state.
- **FR-016**: The terminal MUST offer the same three views, chips, conversation links and follow-up, and read the same
  window.
- **FR-017**: SPEC.md §5.6 MUST say what a surface's list of what waits on the person, what is in flight and what closed
  shows, and that none of it is stored state.
- **FR-018**: The badge that counts what waits on the person MUST keep counting only *Waiting on you*.
- **FR-019**: A reference to a numbered exchange has exactly one form: the word "exchange" then `#` then the number
  (`exchange #351`), or for several, the word "exchanges" then a list of `#` numbers separated by commas, "and" or "or"
  (`exchanges #349, #350 and #351`). Matching ignores letter case. Nothing else is a reference.
- **FR-020**: Wherever a message is shown, on every surface, each reference in that form MUST be a control that opens,
  directly below the line it is on, a panel showing that exchange in the message's project: the agents on it, each
  message drawn as messages are drawn everywhere else, its state (open and who it waits on, or closed with its outcome),
  and a link to it in its conversation. A second activation closes it. References inside code or inside a link stay as
  written.
- **FR-021**: Opening a reference panel MUST NOT deliver, close, settle or otherwise change anything (as FR-015).
- **FR-022**: A reference to a number that is not an exchange in the project MUST open a panel that says so, and nothing
  more.
- **FR-023**: Agents MUST be told the form in the instructions Parley gives every agent that connects, and the plugin's
  guidance MUST say the same. Words Parley itself writes into a message that name an exchange (such as the "which are
  still open" request) MUST use the form.
- **FR-024**: SPEC.md MUST state the form as the way a message names an exchange, as a convention for display only: the
  broker never reads a reference out of a message, and a message without the form is not refused.
- **FR-025**: In the terminal, a reference in the form MUST be marked so it can be told from plain text, and the person
  MUST be able to open the referenced exchange from it without leaving the conversation they are reading.

### Key Entities

- **Exchange row**: one exchange the person is part of, with the other agent, the conversation, when it opened, the last
  thing said on it, which view it belongs in, and (for In flight) a status chip or (for Closed) an outcome and when it
  closed.
- **Agent state**: what the broker reports about an agent now: whether a turn is running, whether it is being started,
  whether it can be started, whether it is open outside Parley, whether its turns are reported at all.
- **Closed window**: a whole number of days, one per machine, shared by both surfaces.
- **Exchange reference**: the words `exchange #N` (or `exchanges #N, #M and #K`) in a message, naming an exchange in the
  message's own project. It is display only: it grants nothing and is never read by the broker.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: After answering or sending, the person can find the exchange again from the Inbox in one click, in every
  case, with no searching through conversations.
- **SC-002**: Every exchange the person is part of that is open, or closed within the window, appears in exactly one of
  the three views.
- **SC-003**: When an agent replies or asks back, the row is in its new view within 10 seconds, without the person
  reloading.
- **SC-004**: For the same deployment at the same moment, the web console and the terminal show the same rows in the same
  views with the same chips.
- **SC-005**: Opening any view, any number of times, changes nothing the broker holds.
- **SC-006**: Changing the window on one surface changes it on the other with no further step.
- **SC-007**: A referenced exchange opens in one click from where it is mentioned, and a number not written as a
  reference never becomes a link.

## Assumptions

- The window is stored once per machine, beside the deployment's other machine-wide files, so both surfaces read it.
  The web console's existing appearance settings stay in the browser; this one cannot, because the terminal must read it.
- The agent's present state (turn running, being started, cannot be started, open outside Parley, turns not reported) and
  stalled flags are already reported to operator surfaces; this feature reads them and adds no new report. If one of them
  turns out not to be available to a surface, the plan says so and the chip leaves it out rather than guessing.
- What was said when a request was sent ("being started", "running") is not kept by the broker, so the chip reflects the
  agent's state now, not what the wake said at the moment of sending.
- "Show older" lists older closed exchanges a page at a time; it does not change the setting.
- The person's own Requests are included in In flight whether they were sent from a conversation page, a roadmap, the
  spin-off page or the Inbox.
- The terminal part may ship after the web console part, in the same feature.
- Exchange numbers are unique within a project's store, so a reference is read in the message's own project. A
  reference to another project's exchange cannot be told apart and is not supported.
- Agents already running keep the old instructions until they reconnect; messages written before this feature mostly
  lack the form, so their numbers stay plain text. Nothing rewrites old messages.
- Agents may not always follow the form. A reference they write another way is simply not a link; nothing guesses.
