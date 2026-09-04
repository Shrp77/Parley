# Feature Specification: A tabbed terminal, and agents added or removed from it

**Feature Branch**: `005-tabbed-terminal-agents`
**Created**: 2026-09-03
**Status**: Draft
**Input**: User description: "A tabbable terminal for parley top, and adding or removing agents in a project from it. The terminal moves from Spectre.Console prompts to Terminal.Gui v2 (stable 2.4.17, pinned exactly): a windowed interface with tabs, tables, focus that moves with Tab, dialogs and mouse support, replacing the one-question-at-a-time wizard. Everything under src/Parley.Cli/Top and the Top tests are rewritten; Parley.Client (seating, transcript and visibility derivations, the directory, the guards) does not move. Inside a project a person can add an agent — enroll a repository for this project, writing its launch configuration to name the project and putting it in the project's catalogue so the node can start it unattended, reusing what the enroll command and the onboarding screen already write — and remove one, with retire and purge both offered, each described first and confirmed by typing the agent's name back, with the catalogue entry and the launch configuration cleaned up in the same step. Destroying a project stays as it is. No operator operation reaches the surface without a description and a typed name. Decided by the owner on 2026-09-03 after using the Spectre terminal on a real deployment; the surface rule in CLAUDE.md was already amended for destroying a project and this extends it to retire and purge on the same terms."

## Why this exists

`specs/004-terminal-surface` gave a person a terminal to take part from, and it was used on a
real deployment the day it landed. Two things came back from that use.

The first is the shape of the surface. It was built as a sequence of prompts — one question, one
answer, the next question — and that is what it feels like: a wizard. A person looking at a
deployment wants to have the projects, the agents and a conversation in front of them at once,
move between them with the keyboard, and act on what is in front of them, not be walked through
it. The owner's words were that it was "not doing it".

The second is what the surface can and cannot do to agents. It can name a project, create one,
and now destroy one; it can seat a person and let them speak. It cannot make a repository an
agent of a project, and it cannot withdraw one. Both are done today from a shell, with the enroll
commands and the operator's retire and purge. A surface that can create and destroy a project but
cannot change who is in it is half a surface.

The rule that kept retire and purge off the terminal — a purge behind a keystroke is a different
risk from one behind a page that describes it first — has already been amended once, for
destroying a project: what the rule asks for is a description and a deliberate act, and a typed
name is a deliberate act. This extends that amendment to retire and purge, on the same terms and
by the same owner's decision.

## Decisions already made

These are inputs to this specification, not questions for it. They were chosen by the owner on
2026-09-03 from options presented, and are recorded so the plan does not re-open them.

- The terminal is rewritten on a windowing toolkit rather than a prompt library. The specific
  library is a planning matter and is named in `plan.md`; what this specification requires is the
  behaviour a windowing toolkit makes possible, stated below as requirements.
- The client half — seating, transcripts and visibility, the directory of brokers, the guards —
  does not move. It was split into its own library for exactly this.
- Removing an agent offers both retiring, which is reversible, and purging, which is not. Both
  are described first and confirmed by typing the agent's name back.
- 004 landed first. This feature starts from what merged as `c85a271`.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - See the deployment at once and move through it with the keyboard (Priority: P1)

A person runs `parley top` and sees the deployment laid out rather than asked about: the
projects along one edge, and for the chosen project its agents, its conversations and, when one
is opened, the transcript — each in its own region of the screen. Tab moves focus between the
regions; arrow keys move within one; Enter opens; Escape goes back. The mouse does the same for
anybody who has one. Nothing that 004 showed is lost: every fact it displayed is still displayed,
and every state it named in words is still named in words.

**Why this priority**: It is the reason the feature exists. Everything else here is an action
taken from a screen this story provides, and 004's own journeys — read a transcript, take a
seat, answer — have to work on the new surface before anything is added to it.

**Independent Test**: Launch against a manifest naming two projects, one answering and one not.
Without typing an identifier, reach a named conversation's transcript using only Tab, arrows and
Enter; then reach the same transcript using only the mouse. Both journeys end on the same
transcript, showing author, recipient and per-message visibility in words.

**Acceptance Scenarios**:

1. **Given** a machine naming two projects, **When** the surface opens, **Then** both are shown
   with whether each broker is answering, and the first is selected with its agents and
   conversations visible beside it without any further key.
2. **Given** a project selected, **When** the person presses Tab, **Then** focus moves visibly to
   the next region (projects → agents → conversations → transcript, and round again), and the
   region with focus is marked in words and not only by colour.
3. **Given** the conversations region focused, **When** the person moves to a conversation and
   presses Enter, **Then** its transcript opens in the transcript region and the conversations
   region stays where it was, so the next conversation is one arrow away.
4. **Given** a project whose broker is not answering, **When** it is selected, **Then** its
   regions say so in words within five seconds and every other project remains usable.
5. **Given** the surface open, **When** the person resizes the terminal to forty columns,
   **Then** every region still names what it shows and nothing is truncated to a silent ellipsis.
6. **Given** a person seated in a project, **When** they use the surface for twenty minutes
   without touching it, **Then** the same idle rule the console and 004 apply releases the seat,
   and the surface says so.

---

### User Story 2 - Take part from the same screen (Priority: P2)

Everything 004's second, third and fourth stories provided — taking a seat, reading what is
waiting, answering, asking, telling, following the broker's log, opening the web console,
naming, creating, forgetting and destroying a project — is reachable from the new surface with
the same rules, the same guards and the same words. Actions belong to the region that shows the
thing acted on: a seat is taken from the agents region, a question is asked from a transcript, a
project is created or removed from the projects region.

**Why this priority**: The rewrite must not cost a single thing 004 delivered. This story is the
parity contract, and it is what SC-003 in 004 — the console and the terminal never disagree —
continues to hold.

**Independent Test**: Every journey in 004's acceptance scenarios, replayed on the new surface,
ends in the same state in the registry and the store as it did before, verified through a
broker rather than by reading the screen.

**Acceptance Scenarios**:

1. **Given** the agents region focused and nobody seated, **When** the person takes a seat under
   a name, **Then** the registry shows that name present within five seconds and the agents
   region shows it as a seat, and a name the registry holds for a real agent is refused with the
   reason.
2. **Given** a seated person and a question waiting for them, **When** they open it from the
   transcript region and answer, **Then** the answer lands in the transcript under their own
   name and the asker's inbox, exactly as it does from the console.
3. **Given** the projects region focused, **When** the person creates a project, **Then** the
   form is the same pre-filled, validated form 004 ships — every proposal accepted by Enter, every
   answer checked as it is typed — and the project is answering on the list within seconds.
4. **Given** a project selected and nobody seated in it, **When** the person chooses to remove
   it, **Then** keeping is the default, forgetting deletes nothing, and destroying takes the
   project's own name typed back before anything is deleted, exactly as 004 ships it.
5. **Given** the log region opened for a project, **When** the broker writes a line, **Then** it
   appears without the person pressing anything, from the file the machine's supervisor entry
   actually writes.

---

### User Story 3 - Make a repository an agent of this project (Priority: P3)

From a project's agents region a person adds an agent: they name a repository directory on this
machine, and the surface makes it an agent of *this* project — its launch configuration written
to name the project, and an entry added to the project's catalogue so the node may start it
unattended when a question arrives for it. The surface says exactly what it wrote and where. A
repository that is already an agent somewhere is told so and left alone. Within seconds the new
agent appears in the agents region as startable, because the process serving the manifest hosts
this project's node from that catalogue.

**Why this priority**: Adding is the half of "who is in a project" that risks nothing: it writes
two files the person can read and revert, and the same files the enroll commands and the
onboarding screen already write. It goes before removal because removal has to be described in
terms of what adding did.

**Independent Test**: With a project whose catalogue is empty, add a repository directory through
the surface; the directory's launch configuration names the project and the agent's role, the
catalogue names the directory, and the registry lists the agent as startable within ten seconds.
Adding the same directory again is refused with the reason and changes nothing.

**Acceptance Scenarios**:

1. **Given** a project selected, **When** the person chooses to add an agent and names a
   directory that is a repository, **Then** the surface shows what it will write — the launch
   configuration's location and the catalogue entry — and writes both only after the person
   confirms.
2. **Given** a directory that is not a repository, or does not exist, **When** it is named,
   **Then** the surface refuses at the prompt with the reason and writes nothing.
3. **Given** a directory already carrying a launch configuration for another project, **When**
   it is named, **Then** the surface says which project it belongs to and refuses to re-point it
   here.
4. **Given** the agent added, **When** the agents region refreshes, **Then** the new agent is
   listed with the role the surface gave it and shown as startable, within ten seconds.
5. **Given** a project whose entry names no catalogue, **When** the person adds an agent,
   **Then** the surface creates a catalogue for the project, records it on the project's entry,
   and says that the process serving the manifest will host the node from it.

---

### User Story 4 - Withdraw an agent, and be able to take it back (Priority: P4)

From a project's agents region a person removes an agent. They are shown what the agent is —
its role, its workspace, whether it is running, whether it owes anything in an open exchange —
and offered three things in order: keep it; retire it, which withdraws it from every roster
and every route while keeping its record and its history, and can be undone; purge it, which
deletes its record and cannot. Retiring and purging each take the agent's name typed back.
Whichever is chosen, the catalogue entry and the repository's launch configuration are removed in
the same step and the surface says so. A retired agent stays visible as retired, and can be
reinstated from the same region, which restores the roster but not the files.

**Why this priority**: It is the destructive half, and it is last for that reason. Everything it
does can be described in terms of the three earlier stories: the thing being removed is what
story 3 added, from the region story 1 shows, under the rule story 2 already applies to
destroying a project.

**Independent Test**: Add an agent through story 3, then retire it: the registry shows it
retired, the catalogue and launch configuration are gone, and it no longer appears in any
roster a peer sees. Reinstate it: the registry shows it active and the files are still gone.
Purge it: the registry no longer has it. At each step a wrong typed name changes nothing.

**Acceptance Scenarios**:

1. **Given** an agent selected, **When** the person chooses to remove it, **Then** the agent is
   described — role, workspace, presence, and any exchange it currently owes — and keeping is
   the first choice.
2. **Given** retiring chosen, **When** the person types a name that is not the agent's,
   **Then** nothing changes and the prompt says so; **When** they type the agent's name, **Then**
   the registry shows it retired, its catalogue entry and launch configuration are removed, and
   the surface lists exactly what was removed.
3. **Given** purging chosen for an agent that owes an answer in an open exchange, **When** the
   person types the name, **Then** the surface says what the asker will see — the same outcome
   the operator command produces — before the purge is applied, and applies it only after a
   second confirmation of that consequence.
4. **Given** a retired agent, **When** the person chooses to reinstate it, **Then** the registry
   shows it active again, no file is recreated, and the surface says the launch configuration and
   catalogue entry have to be added again for it to be startable.
5. **Given** a seat held by the person themselves, **When** they look for it in the remove
   action, **Then** it is not offered — a person leaves a seat; they are not retired.

---

### Edge Cases

- The terminal is narrower than the four regions can fit side by side: regions stack rather
  than truncate, and Tab still visits every one.
- The broker of the selected project goes away while a transcript is open: the transcript region
  says so in words, the seat book keeps retrying as it does today, and the other projects stay
  usable.
- Two people add the same repository to two different projects on the same machine: the second
  is refused, because a repository is one agent with one launch configuration.
- The catalogue file is edited by hand while the surface is open: the agents region shows what the
  registry says, and adding refreshes its view of the catalogue before writing.
- A purge is confirmed while the agent is mid-exchange: the outcome is whatever the operator
  operation produces today, shown before it is applied — this feature adds a door, not a rule.
- The terminal is not interactive: refused, naming what to run instead, exactly as today.
- The mouse is unavailable, or the terminal reports no mouse: every action remains reachable by
  keyboard alone.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The surface MUST show the projects, the selected project's agents, its conversations
  and an opened transcript at the same time, each in its own region.
- **FR-002**: Focus MUST move between regions with Tab and within a region with the arrow keys,
  and the focused region MUST be named in words as well as marked visually.
- **FR-003**: Every action MUST be reachable by keyboard alone, and every action MUST also be
  reachable by mouse where the terminal reports one.
- **FR-004**: Everything 004 displays and every state it names in words MUST be displayed and
  named on the new surface (FR-001 to FR-019 of `specs/004-terminal-surface/spec.md`, and the
  section "What use added after the plan" there, continue to apply).
- **FR-005**: Every journey 004's acceptance scenarios describe MUST end in the same registry and
  store state on the new surface, verified through a broker.
- **FR-006**: The surface MUST open no database; every fact it shows MUST come from a broker over
  the transport, and the one file it reads is the broker's log.
- **FR-007**: A person MUST be able to add an agent to the selected project by naming a
  repository directory, and the surface MUST show what it will write before writing it.
- **FR-008**: Adding MUST write the repository's launch configuration to name the project and the
  agent's role, and MUST add the repository to the project's catalogue, using what the enroll
  command and the onboarding screen already write and nothing else.
- **FR-009**: Adding MUST refuse a directory that does not exist, is not a repository, or already
  carries a launch configuration for another project, with the reason, and MUST write nothing on
  refusal.
- **FR-010**: A project whose entry names no catalogue MUST have one created and recorded on its
  entry the first time an agent is added, and the surface MUST say so.
- **FR-011**: A person MUST be able to remove an agent from the selected project, and the surface
  MUST describe the agent — role, workspace, presence, and any open exchange it owes — before
  offering keep, retire or purge, with keep first.
- **FR-012**: Retiring and purging MUST each require the agent's name typed back, and anything
  else typed MUST change nothing.
- **FR-013**: Retiring or purging MUST remove the agent's catalogue entry and its launch
  configuration in the same step, and MUST list what was removed.
- **FR-014**: Purging an agent that owes an answer in an open exchange MUST show the asker's
  outcome before applying, and MUST require a second confirmation of that consequence.
- **FR-015**: A retired agent MUST remain visible as retired and MUST be reinstatable from the
  same region; reinstating MUST recreate no file and MUST say so.
- **FR-016**: The person's own seat MUST never be offered for retiring or purging.
- **FR-017**: No operator operation MUST be reachable on the surface without a description first
  and a typed name, and the set of such operations MUST be enumerable by a test from the
  surface's source.
- **FR-018**: The surface MUST refuse to run when the terminal is not interactive, naming what to
  run instead.
- **FR-019**: Operator operations and the person's own agent operations MUST remain separately
  bound, as SPEC.md §10.2 requires and as both existing surfaces do.

### Key Entities

- **Region**: One of the four areas of the screen — projects, agents, conversations, transcript —
  each holding focus in turn and each naming its own state in words.
- **Agent entry**: What makes a repository an agent of a project on this machine: its launch
  configuration, naming the project and the role, and its line in the project's catalogue. Two
  files, both written by adding and removed by retiring or purging.
- **Registry status**: What the broker says about an agent — active or retired — as distinct from
  presence, which is what the connections say. Retiring changes the first; nothing on this
  surface changes the second.
- **Confirmation**: A description of the thing and its consequence, followed by the thing's own
  name typed back. The unit every destructive action on this surface is built from.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A person reaches a named conversation's transcript from launch using Tab, arrows
  and Enter only, with no identifier typed, and separately using the mouse only.
- **SC-002**: Every one of 004's acceptance scenarios passes on the new surface with its
  assertions unchanged where they read registry or store state.
- **SC-003**: A repository added as an agent appears as startable in the agents region within ten
  seconds of confirmation, on a machine whose supervisor entry serves the manifest.
- **SC-004**: Retiring or purging an agent leaves no launch configuration and no catalogue entry
  for it, verified on disk, and a wrong typed name leaves both in place.
- **SC-005**: A conversation shown here and the same conversation shown in the web console never
  differ in who took part, who saw what, or what was said.
- **SC-006**: Every region names its state in words: a test that strips all colour from the
  rendered screen loses no information.
- **SC-007**: Every destructive action on the surface is found by one test reading the source,
  and each is behind a description and a typed name.
- **SC-008**: A project whose broker is not answering is reported within five seconds and leaves
  every other project usable.

## Assumptions

- The windowing toolkit is chosen in `plan.md`. The owner has named one; the plan records the
  choice with the package rules it must live inside, and this specification does not depend on
  which one it is.
- "Repository directory" means a directory the enroll command already recognises as one; this
  feature adds no new notion of what a repository is.
- The role an added agent gets is what the onboarding screen and enroll command already derive
  from the directory, and can be changed at the prompt before confirming.
- Retire, reinstate and purge are the operator operations SPEC.md §10.2 already defines; this
  feature adds a door to them and changes nothing about what they do.
- A person's own seat is distinguishable from an agent by its adapter, as the seat guard already
  distinguishes it.
- The process serving the manifest, which 004 left hosting a node per catalogued project and
  watching the manifest, is what makes an added agent startable without a restart.
- Destroying a project stays exactly as 004 ships it; this feature moves it to the projects region
  and changes nothing about it.
