# Feature Specification: An agent is an identity, not a name

**Feature Branch**: `015-file-store` (this branch, as 016 and 017 were — no new branch)
**Created**: 2026-09-22
**Status**: Draft
**Input**: The owner's model, stated on 2026-09-22 after a message reached one agent and not another.

## The defect this starts from

One checkout is enrolled in two projects and answers to a different name in each — `infra` in one,
`billing-infra` in the other. The node's catalogue is keyed by **the name**, and there is **one catalogue per
project**, so the same checkout is startable in the project whose catalogue names it and unstartable in the
other.

A request to the unstartable name is accepted, queued and flagged, and nothing starts. `RequestStart` finds
the role is not in that node's announced set and the wake is `Unreachable`; the Request sits open until
somebody happens to open that repository. The broker knows, and prints it at every startup into a log nobody
reads:

> *Registered here but not in the catalogue: … A request to one of those waits, flagged, until somebody opens
> that repository.*

The owner met it as a message that reached one agent and not another, with nothing on the screen they were
looking at saying why.

**The defect is not the catalogue's contents. It is that the arrangement can express the state at all.** A
per-project catalogue keyed by a per-project name makes "one checkout, startable here and not there" a
representable thing, and 009's FR-013 then has to forbid the second catalogue line by hand, because two nodes
must not launch one working directory.

## The model

**An agent is an identity.** It is identified by a stable identifier that does not collide across any
boundary. That identifier *is* the agent.

**A name is what a project calls it.** The name is what a person interacts with, it is project-dependent, and
it translates to the identifier. The node cares about the identifier and knows nothing about projects.

Everything below follows from those two sentences.

## User Scenarios & Testing *(mandatory)*

### User Story 1 — A message reaches an agent whatever project it is asked in (Priority: P1)

A person sends a message in a conversation to an agent whose checkout is enrolled in two projects. The agent
is not running. It starts, collects the message, and answers — and it does so identically whichever project
the conversation belongs to.

**Why this priority**: This is the defect. Everything else in this feature exists to make this true by
construction rather than by the catalogue happening to be right.

**Independent Test**: Enrol one checkout in two projects under two names; ask it from the project whose
catalogue did not name it; the agent starts.

**Acceptance Scenarios**:

1. **Given** a checkout enrolled in projects A and B, known as `infra` in A and `billing-infra` in B, **When** a
   person in a conversation in B sends it a Request and it is not running, **Then** it starts and collects.
2. **Given** the same checkout, **When** a person in a conversation in A sends it a Request, **Then** the same
   checkout starts, by the same route, with no difference in behaviour between the two projects.
3. **Given** the agent is already running, **When** a Request arrives from either project, **Then** it is given
   a turn and no second process is started.

---

### User Story 2 — One checkout is one process (Priority: P1)

Two projects ask for the same checkout at the same moment. One process starts.

**Why this priority**: It is the hazard that forced the per-project partition in the first place. If this is
not true by construction, the feature has moved the problem rather than removed it.

**Independent Test**: Drive two simultaneous start requests for one identifier from two projects; count
processes.

**Acceptance Scenarios**:

1. **Given** a checkout catalogued once and named in two projects, **When** both projects ask for a start at
   once, **Then** exactly one process is started and both projects' adapters attach to it.
2. **Given** that process is alive, **When** either project asks again, **Then** it is given a turn.
3. **Given** that process has died, **When** either project asks, **Then** one new process is started.

---

### User Story 3 — Identity survives a move or a rename (Priority: P2)

A person moves a checkout to a new path, or renames what a project calls it. Nothing needs repairing.

**Why this priority**: It is what makes the identifier worth having rather than a second name. It is also the
cheapest thing to get wrong: derive the identifier from a path and a move silently mints a second agent.

**Independent Test**: Move a catalogued checkout; the identifier is unchanged and the agent still starts.

**Acceptance Scenarios**:

1. **Given** a catalogued checkout, **When** its folder is moved and the catalogue entry repointed, **Then**
   the identifier is unchanged and every project's name for it still resolves.
2. **Given** a catalogued checkout, **When** a project's name for it is changed, **Then** the identifier is
   unchanged and no other project is affected.

---

### User Story 4 — A person is told when this machine cannot start an agent (Priority: P2)

Somebody addresses an agent this machine has no way to start. They are told where they are looking, before
they press Enter — not in a log at startup.

**Why this priority**: It is the half of the defect that made it invisible. The arrangement is being fixed so
the state is rarer; when it does happen it must not be silent.

**Independent Test**: Address an agent registered on this machine with no catalogue entry; read the screen.

**Acceptance Scenarios**:

1. **Given** an agent registered on this machine with no catalogue entry, **When** it is among the recipients
   of what a person is writing, **Then** the composer says the message will wait until that repository is
   opened.
2. **Given** the same agent, **When** the message is sent, **Then** it is still accepted and queued, and the
   report says what will happen to it.

### Edge Cases

- **Two checkouts claim the same name in one project.** The one thing Parley makes unforgeable is an agent's
  name; two workspaces answering to it in one project is the copied-repository case. The node must refuse to
  announce that project and name both paths.
- **A catalogued workspace has gone.** The entry is reported and inert; nothing is retired automatically,
  because a missing folder is as likely to be an unmounted drive as a deletion.
- **A catalogued workspace declares no membership at all** — no `parley-<project>` entry. It is catalogued and
  startable by nothing, which is reported rather than silently dropped.
- **A repository declares a name for a project this machine does not serve.** Ignored for announcement, and
  not an error: a checkout may be enrolled on two machines in different projects.
- **Two catalogue entries carry the same identifier.** Cannot be expressed: the catalogue is keyed by it.
- **A repository carries an identifier that no catalogue entry has.** It is not startable here, which is the
  ordinary state of a repository on a machine that has not enrolled it.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: An agent MUST have a stable identifier that does not collide across machines, projects or
  repositories.
- **FR-002**: The identifier MUST NOT be derived from a path, a name, a project or a machine, so that moving
  or renaming changes nothing.
- **FR-003**: There MUST be exactly one catalogue per machine, keyed by identifier.
- **FR-004**: A catalogue entry MUST carry what is needed to start the agent — its command and its workspace —
  and MUST NOT carry project membership or any project's name for it.
- **FR-005**: A repository MUST carry its own identifier, beside the per-project entries that already give the
  name it answers to in each project.
- **FR-006**: The node MUST announce, for each project it serves, the names that catalogued identifiers claim
  for that project.
- **FR-007**: The node MUST resolve a requested name to an identifier, and the identifier to a command and a
  workspace, when starting.
- **FR-008**: Running agents MUST be tracked by identifier, shared across every project one process serves, so
  that one checkout is one process.
- **FR-009**: The node MUST refuse to announce a project in which two catalogued workspaces claim the same
  name, and MUST name both paths.
- **FR-010**: Enrolment MUST mint the identifier and write it to both the catalogue and the repository.
- **FR-011**: Enrolling a checkout into a further project MUST NOT create a second catalogue entry, and MUST
  NOT be refused for the reason that one already exists.
- **FR-012**: A person composing a message MUST be told, before sending, when a recipient cannot be started by
  this machine.
- **FR-013**: A message to an agent that cannot be started MUST still be accepted and queued, and the report
  MUST say what will happen to it.
- **FR-014**: Nothing MAY write a registry row on a repository's behalf; registration stays the adapter's own
  act on attach.
- **FR-015**: A machine MUST still hold one node per broker it joins.

### Key Entities

- **Agent identity**: the stable identifier. One per startable thing. Minted once, never derived, never reused.
- **Catalogue entry**: identifier → command, workspace. One per identifier, one catalogue per machine.
- **Membership**: declared by the repository — which projects it answers in, and what it is called in each.
- **Name**: a project's label for an identity. Unique within a project; several may exist for one identity
  across projects.
- **Resident**: a running process for one identity, shared by every project that names it.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A checkout enrolled in two projects starts on demand from either, with no difference in
  behaviour between them.
- **SC-002**: Two projects asking for one checkout at the same moment produce exactly one process, measured by
  counting processes rather than by reading code.
- **SC-003**: "Startable in one project and not another" cannot be represented: there is no arrangement of
  catalogue and membership that produces it.
- **SC-004**: Moving a checkout or renaming it in one project leaves every other project's use of it
  unchanged, with no repair step.
- **SC-005**: Enrolling a checkout into a second project requires no exception, no gate and no cross-project
  scan, and the machine-wide enrolment lock is gone.
- **SC-006**: A person addressing an agent this machine cannot start reads that fact on the screen they are
  looking at, before sending.

## Assumptions

- **The identifier stays machine-side.** The registry, the catalogue and the node use it; messages are still
  addressed by name, because that is what people and agents read and what §13's envelope is written about.
  There is no change to how a message is addressed on the wire.
- **No migration.** Existing catalogues and existing data are discarded; the owner was explicit. Enrolment is
  re-run.
- **One identifier is one checkout.** A repository hosting two agents is not a case this feature serves; if it
  arrives, the workspace becomes a field on an identity rather than the thing identified.
- **Membership is already recorded** in each repository's per-project launch entries, carrying the name it
  answers to in each. This feature reads what 009 already writes.
- **Out of scope, left where 012 put it**: one node object holding several broker connections, consent moving
  into the store, enrolment becoming an operator operation, and purge's sentinel. This takes 012's D2 and D3
  and leaves D4 to D9.

## What this deletes

Counted, because the change earns its cost here rather than in new capability:

- the per-project catalogue, and the binding for it in the manifest
- the machine-wide enrolment gate, and 009's documented deviation from Principle V
- the cross-project catalogue scan
- the rule that a second project gets no catalogue line, and the "start it through the project that holds the
  consent" workaround that stands in for it
- one of the two registers of what a machine can start
