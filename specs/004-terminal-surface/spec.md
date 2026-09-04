# Feature Specification: A terminal surface a person takes part from

**Feature Branch**: `004-terminal-surface`
**Created**: 2026-09-02
**Status**: Draft
**Input**: User description: "A Spectre.Console terminal application, started as `parley top`, that becomes the interaction surface for a person taking part in a Parley deployment... navigate between projects; see the agents; see the conversations; read a conversation as a transcript; send a message into a conversation as themselves; start a new conversation... surface the operator tasks that currently require leaving for a shell... a Logs view... an action that opens the hosted web console."

## Why this exists

SPEC.md §17 question 15 records the gap this closes. A node hands turns to the agents it
started; an agent attached through somebody's own editor window was started by nobody, so
nothing can hand it one. It receives a question, and nothing wakes it to answer.

The consequence is not that the editor agent is inconvenient — it is that **a person cannot
currently take part in a deployment without one**. Everything a person does today either goes
through an editor session that cannot be woken, or through single-shot shell commands that
report and exit. Neither is somewhere to hold a conversation.

The web console (`specs/003-parley-web-console`) already seats a person and renders transcripts,
and it remains the richer reader. What it needs is a browser. This is the same participation on
the machine where the broker already runs, in the terminal an operator already has open — and,
because it is a client rather than a second owner of the store, on a machine where the broker
does not run at all.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Read the deployment without a browser (Priority: P1)

A person opens the terminal application and moves between the projects this machine knows
about. For a project they see the agents registered in it and whether each is present, the
conversations that have happened, and — choosing one — the conversation as a transcript: who
asked, who answered, what each participant could see.

**Why this priority**: It is the slice that stands alone. An operator who can see the state of a
deployment from a terminal has something useful before a single message is sent, and every later
story navigates through this one. Participation is the motivating goal; reading is what ships
first and is worth shipping on its own.

**Independent Test**: Launch against a broker holding a project, agents and at least one
conversation; confirm each is reachable and the transcript matches what the web console renders
for the same conversation.

**Acceptance Scenarios**:

1. **Given** a machine whose manifest names two projects, **When** the person opens the
   application, **Then** both are listed and one can be chosen without typing its name.
2. **Given** a chosen project, **When** the person opens its agents, **Then** every registered
   agent is shown with its presence and the workspace it owns.
3. **Given** a conversation with three participants, **When** the person reads it, **Then** each
   message shows its author, its recipient, and who else could see it — carried as text, never
   by colour alone.
4. **Given** a project whose broker is not answering, **When** the person selects it, **Then**
   the application says so and every other project stays usable.

---

### User Story 2 - Take part in a conversation (Priority: P2)

The person takes a seat under their own name, reads a question addressed to them, and answers it
from the terminal. They can also send into an existing conversation, and start a new one with a
peer they choose from the project's agents.

**Why this priority**: The reason the feature exists, and it depends on Story 1 to navigate. It
is the first point at which a person is a participant rather than an observer.

**Independent Test**: With a peer agent attached, ask a question of the seated person from that
peer; confirm it arrives, that answering it closes the exchange, and that the peer receives the
answer.

**Acceptance Scenarios**:

1. **Given** nobody is seated, **When** the person joins under a name, **Then** they appear in
   that project's registry as present, and the name is refused if the registry holds it for
   anything that is not this surface.
2. **Given** a seated person with a question owed, **When** they open what is waiting, **Then**
   the question, its intent and its consequences are shown, and answering it is possible without
   leaving the transcript.
3. **Given** a seated person, **When** they start a conversation with a chosen agent, **Then**
   the intent, sub-questions and consequences are collected before anything is sent, and a
   refusal from the broker is shown with the remedy the broker gave.
4. **Given** a seated person who quits the application, **When** they do, **Then** their seat is
   released rather than left standing.

---

### User Story 3 - Watch the broker it is talking to (Priority: P3)

The person opens a Logs view showing what the broker is writing, and can open the hosted web
console in a browser from the same screen.

**Why this priority**: Useful and cheap, and it is the half of the original request that needs no
protocol reads at all. It is also the honest answer to a broker whose console output scrolls
past: something that keeps it, filtered.

**Independent Test**: With a broker writing to its log, confirm new lines appear without
restarting the view, and that the launch action opens the console's address.

**Acceptance Scenarios**:

1. **Given** a broker writing its log, **When** the person opens Logs, **Then** new lines appear
   as they are written.
2. **Given** the log carries framework noise alongside Parley's own lines, **When** the person
   filters, **Then** only matching lines are shown and the filter is visible.
3. **Given** a console address, **When** the person chooses to open it, **Then** the default
   browser opens that address and the application keeps running.

---

### User Story 4 - Do the operator jobs without leaving (Priority: P4)

The person names a new project, and onboards an agent into one, from inside the application
rather than from a shell.

**Why this priority**: Convenience over capability — every one of these has a command today, and
each is done once per project or per agent. Last because a person blocked here has a documented
way through, which is not true of anything above.

**Independent Test**: Name a project from inside the application and confirm `parley project
list` reports it identically to one added from the command line.

**Acceptance Scenarios**:

1. **Given** a machine with no manifest, **When** the person names a project, **Then** it is
   written with the same validation the command applies, and a refusal explains which rule it
   broke.
2. **Given** a project, **When** the person onboards an agent, **Then** what is written is what
   the equivalent command writes, and the person is told what remains to be done outside the
   application.

---

### Edge Cases

- **The terminal is not interactive.** Output redirected to a file, or run from a scheduled
  task: refuse with a sentence naming what to run instead, rather than drawing to a stream
  nobody is reading.
- **The terminal is too narrow, or is resized while running.** The transcript reflows; nothing
  that carries meaning is truncated away silently.
- **The same person is seated in the web console and here at once.** Two sessions for one agent
  are legitimate (SPEC.md §14) and must both keep working; neither may release the other's
  in-progress work.
- **The broker restarts underneath a running session.** The seat is gone with it. The surface
  must say the person is no longer seated rather than continue showing them as present — the
  failure a cookie outliving its seat already caused in the web console.
- **A conversation is concluded while it is open on screen.** Sending into it is refused with the
  reason, not with a generic failure.
- **The manifest names no projects.** The broker every other command would reach is offered,
  under a name that says it was not configured.
- **A message exceeds the size cap** (SPEC.md rule 8.5): refused before it is sent, with the
  limit stated, rather than by the store after a round trip.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The surface MUST list the projects this machine's manifest names, and MUST offer
  the broker other commands would reach when the manifest names none.
- **FR-002**: The surface MUST re-read the manifest while running, so a project named elsewhere
  appears without restarting it.
- **FR-003**: The surface MUST show, for a chosen project, every registered agent with its
  presence, tier and owned workspace.
- **FR-004**: The surface MUST list the conversations in a chosen project and open one as a
  transcript showing author, recipient and per-message visibility.
- **FR-005**: Visibility and participation MUST be carried as text and never by colour alone.
- **FR-006**: A person MUST be able to hold a seat under a chosen name, and the surface MUST
  refuse a name the registry holds for anything that is not a person's seat.
- **FR-007**: The surface MUST refuse to seat anybody when the registry could not be read, rather
  than seat them unchecked.
- **FR-008**: A seated person MUST be able to read what is waiting for them, answer a question
  they owe, and send into an open conversation.
- **FR-009**: A seated person MUST be able to open a conversation with an agent chosen from the
  project, supplying intent, sub-questions and consequences before anything is sent.
- **FR-010**: The surface MUST validate what SPEC.md §8 requires of a question before sending,
  and MUST show a broker's refusal with whatever remedy the broker stated.
- **FR-011**: The surface MUST release the person's seat when it exits, including when
  interrupted.
- **FR-012**: The surface MUST show that a person is seated only while a seat is actually held.
- **FR-013**: The surface MUST show the broker's log as it is written, with a visible filter.
- **FR-014**: The surface MUST be able to open the hosted web console's address in the default
  browser, and MUST keep running when it does.
- **FR-015**: The surface MUST be able to name a project and onboard an agent, applying the same
  validation as the equivalent commands and writing what they write.
- **FR-016**: The surface MUST open no database. Every fact it shows MUST come from a broker over
  the transport.
- **FR-017**: The surface MUST contain one project's unreachable broker to that project.
- **FR-018**: The surface MUST refuse to run when the terminal is not interactive, naming what to
  run instead.
- **FR-019**: Operator operations and the person's own agent operations MUST remain separately
  bound, as SPEC.md §10.2 requires and as the web console already does.

### Key Entities

- **Project**: A named deployment on this machine — a store, an endpoint, optionally a node
  catalogue. Read from the manifest; the protocol has no concept of one (SPEC.md §4.1).
- **Agent**: A registered participant, `role@machine`, with a presence, a tier and possibly an
  owned workspace.
- **Conversation**: A thread of exchanges between a pair or a group, carrying state and the
  messages exchanged.
- **Seat**: A person's own attachment, held while the application runs, under a name of the form
  SPEC.md §11.3 gives a human responder.
- **Broker log**: The file the broker writes; the only thing here that is read from disk rather
  than over the transport.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A person can go from launching the application to reading a named conversation
  without typing any identifier — every step is chosen from what is on screen.
- **SC-002**: A person can answer a question addressed to them without leaving the terminal, and
  the asking agent receives that answer.
- **SC-003**: A conversation shown here and the same conversation shown in the web console never
  disagree about who took part or who could see what.
- **SC-004**: A project whose broker is not answering is reported within five seconds and leaves
  every other project usable.
- **SC-005**: The surface works against a broker on another machine, with no store present
  locally.
- **SC-006**: A person's seat is gone from the registry within one minute of them quitting,
  including when they quit by interrupting.
- **SC-007**: Nothing the surface renders conveys meaning by colour alone.

## Open questions for planning

- **Q1 — What is shared with the web console, and does the web console keep its role?**
  Seating, transcript derivation, refusal validation and the operator operations all exist in the
  web console today. Three shapes are possible: extract the shared behaviour into something both
  clients use; keep the web console as the interaction surface and make this one thin; or let the
  two implement independently. The third is how two surfaces come to disagree about who was in a
  conversation, which SC-003 forbids — but the first has a cost the plan must state, because the
  web console deliberately references neither the broker nor the store, and whatever is extracted
  must not drag either onto its graph. **This is for `/speckit-plan` to settle, and it is the
  question this spec most needs answered.**

## Assumptions

- The application is a client only. It reaches a broker exactly as the web console does, over the
  services that already exist, and no new protocol operation is assumed. If any of the above
  cannot be served by an existing operation, that is a SPEC.md change and is out of this scope
  until specified.
- Seating reuses the identity scheme SPEC.md §11.3 already gives a human responder; no new kind
  of participant is introduced.
- Joining is explicit. The application does not seat anybody because it was launched — an
  attachment says a person is present, and only the person can say that.
- The broker's log is found where the broker writes it, beside the store, as the installer
  already reports.
- The manifest is written by `parley` and by nothing else. The operator tasks here are that same
  program, so this does not add a second writer.
- Two sessions for one agent are legitimate, so a person seated here and in a browser at once is
  a supported state rather than an error.
- The web console continues to exist. Nothing here removes it, and FR-014 assumes it is worth
  opening.

## What use added after the plan (2026-09-03)

Recorded here because the plan was built first and the surface was then used, on a real
deployment, by the person it was for. Each item below was asked for or found in use rather
than specified; each is built, tested, and in the review ledger. They amend the requirements
above rather than replace them.

- **FR-015, sharpened.** Naming a project is two forms: *name the deployment this machine already
  runs*, pre-filled from the autostart entry, and *create a new project*, which proposes a store
  beside the existing ones and the default port when free, else a free one. Every answer is
  checked as it is typed, and every prompt says what it is for — the node catalogue's in terms
  of what having one changes.
- **The project list shows the description** it collects.
- **The autostart entry serves the manifest** when projects are named and nothing explicit is
  given, hosting this machine's node in every project whose entry names a catalogue (decided:
  one node per catalogued project, replacing the one-node rule). `autostart status` says what
  the entry serves. `project set` changes an entry in place.
- **The process serving the manifest watches it**: a project named while it runs gets its broker
  within seconds, a removed one is stopped, a changed one is restarted on its new entry, and a
  description change restarts nothing. A project that failed to start is retried on the next
  change.
- **A project can be removed from the terminal**: forgotten, which deletes nothing, or destroyed,
  which stops its broker, deletes its store and catalogue, and names the repositories that were
  agents in it — behind a description and the project's own name typed back. This amends the
  surface rule in CLAUDE.md, by the owner's decision, and `remove --destroy` is the command it
  runs.
- **The log is found where the entry writes it**: beside the manifest for a manifest-serving
  entry, beside the store otherwise. The assumption below that it is "beside the store, as the
  installer already reports" held only for the explicit entry.
