# Feature Specification: Machine-level project manifest

**Feature Branch**: `002-project-manifest`
**Created**: 2026-08-28
**Status**: Draft
**Input**: User description: "A machine-level Parley project manifest at a well-known per-OS location, so one machine can host several independent Parley projects (separate agent pools) without any command having to infer paths from its working directory."

## Context

SPEC.md §4.1 already establishes projects as a deployment concept: each project has its own
database file and its own transport endpoint, one process may host several brokers, and **the
protocol has no concept of a project and must not acquire one**. Agent names collide freely
across projects, deliberately, because a broker's connection cannot see another's file.

What §4.1 does not settle is where that configuration lives. Today each path is supplied
separately and independently — the store by `--db` or an environment variable, the endpoint by
`--broker` or an environment variable, the node catalogue by an environment variable **or by
falling back to the working directory**. Nothing ties the three together, so nothing can notice
when they disagree.

That last fallback caused a defect found in use rather than in review. With the catalogue
variable unset, enrolling ten repositories from one directory would have created a *new*
catalogue there listing those ten, while the catalogue the node actually reads sat elsewhere
listing seven. Ten agents would have been recorded as consenting to run unattended, none of them
startable, and the run would have reported success. No file was corrupted and nothing was
overwritten; the two files simply described different worlds, and nothing was in a position to
say so.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Name a deployment once, and stop repeating its paths (Priority: P1)

An operator runs several unrelated projects on one workstation. Each has its own store, its own
endpoint and its own set of repositories. Today every command that touches one of them must be
given the right paths, from the right directory, and a mistake in either is silent. The operator
wants to name each deployment once and then refer to it by name.

**Why this priority**: This is the whole feature. Every other story depends on a project being a
nameable thing. It also removes the class of defect described above, because a command that is
told which project it is acting on never has to infer one.

**Independent Test**: Configure two projects with different stores and endpoints, run the same
command against each by name, and confirm each acts only on its own files. Fully testable without
any other story, and delivers the value on its own.

**Acceptance Scenarios**:

1. **Given** a manifest describing two projects, **When** an operator runs a command naming one,
   **Then** the command uses that project's store, endpoint and catalogue, and touches no file
   belonging to the other.
2. **Given** a manifest describing two projects, **When** an operator runs a command naming
   neither, **Then** the command refuses and lists the names it knows, rather than guessing.
3. **Given** a manifest describing exactly one project, **When** an operator runs a command
   naming none, **Then** that project is used, because there is nothing to disambiguate.
4. **Given** a manifest naming a project that does not exist in it, **When** an operator names
   it, **Then** the command refuses, names the missing project, and lists what is configured.

---

### User Story 2 - An existing deployment keeps working untouched (Priority: P1)

An operator already has Parley running, configured entirely by flags and environment variables,
with no manifest anywhere. They upgrade. Nothing about their deployment changes and nothing asks
them to migrate.

**Why this priority**: Equal first, because a feature that breaks working deployments is worse
than the problem it solves. It is listed second only because it is defined by what does *not*
happen.

**Independent Test**: With no manifest present, run every command that previously worked and
confirm identical behaviour. Testable in isolation on a machine that has never had a manifest.

**Acceptance Scenarios**:

1. **Given** no manifest exists, **When** any command runs with the flags and variables that
   worked before, **Then** it behaves exactly as before and reports no absence.
2. **Given** a manifest exists and names a project, **When** an operator also passes an explicit
   path or endpoint, **Then** the explicit value wins and the command says which value it used
   and where it came from.
3. **Given** a manifest exists, **When** a command needs a value the manifest does not supply,
   **Then** it falls back to the previous resolution rather than failing.

---

### User Story 3 - Consent to run unattended lands where it is read (Priority: P2)

An operator enrolls a set of repositories and marks some of them as allowed to start when a peer
asks them something. The consent is recorded in the catalogue that the supervisor for that
project actually reads, and the operator can see which file that is before anything is written.

**Why this priority**: This is the defect that prompted the feature, and it is the one with a
consequence an operator cannot see afterwards. It is P2 only because it depends on Story 1
existing first.

**Independent Test**: Enroll repositories against a named project whose catalogue is not in the
working directory, and confirm the entries appear in that project's catalogue and nowhere else.

**Acceptance Scenarios**:

1. **Given** a named project whose catalogue is elsewhere, **When** an operator enrolls
   repositories with consent, **Then** the entries are written to that project's catalogue.
2. **Given** any command that would modify a catalogue, **When** it runs in preview mode,
   **Then** it names the exact file it would write before writing anything.
3. **Given** two projects, **When** the same repository name is enrolled in each, **Then** each
   project's catalogue records it independently and neither is disturbed by the other.

---

### User Story 4 - See what this machine is configured to run (Priority: P3)

An operator returning to a machine after some weeks wants to know what projects exist on it,
where each one's files are, and which are currently reachable — without opening a file or
remembering a path.

**Why this priority**: Genuinely useful and not required by anything else. A manifest that can
only be read by opening it in an editor is a smaller improvement than one the tool can report on.

**Independent Test**: Configure projects, run the listing, and confirm it names each project with
its paths and its reachability.

**Acceptance Scenarios**:

1. **Given** a manifest with several projects, **When** an operator asks what is configured,
   **Then** each project is listed with its store, endpoint and catalogue.
2. **Given** a project whose broker is not running, **When** the listing runs, **Then** that
   project is shown as not reachable rather than omitted or reported as an error.
3. **Given** a manifest that cannot be read, **When** any command runs, **Then** it refuses and
   says why, rather than behaving as though no projects are configured.

---

### User Story 5 - Add a project without looking up where the manifest lives (Priority: P2)

An operator setting up a second project on a machine adds it by name, with its store, endpoint
and catalogue, and the tool writes the manifest wherever the manifest belongs. They never type
the well-known path, and a mistake — a port already claimed by another project, a store already
in use — is refused at that moment rather than discovered when a broker will not start.

**Why this priority**: The feature exists to stop operators maintaining paths by hand. A manifest
that must itself be hand-created, in a location they have to look up, keeps most of the problem.
P2 rather than P1 because reading a manifest is what the other stories need; writing one is how
it comes to exist.

**Independent Test**: On a machine with no manifest, add two projects by command, then confirm
the file exists at the expected location, describes both, and that adding a third with a
duplicate endpoint is refused.

**Acceptance Scenarios**:

1. **Given** no manifest exists, **When** an operator adds a project, **Then** the manifest and
   any missing directory are created and the project is described in it.
2. **Given** a manifest with one project, **When** an operator adds a second reusing the first's
   endpoint or store, **Then** the addition is refused, names the conflict, and the manifest is
   left unchanged.
3. **Given** a manifest with two projects, **When** an operator removes one, **Then** only its
   entry is removed, and the command states that the store and catalogue remain and where.
4. **Given** a project whose broker is running, **When** an operator removes its entry, **Then**
   the removal reports that a broker appears to be reachable for it.

---

### Edge Cases

- **The manifest exists but is unreadable or malformed.** It must be refused, not treated as
  empty. Treating an unreadable configuration file as an absent one is how a deployment silently
  loses everything it described — the same failure class as the defect that prompted this
  feature, and one already found and fixed once in the enrollment tool.
- **Two projects claim the same endpoint.** Only one broker can hold a port. This must be
  reported when the configuration is read, not discovered when the second broker fails to start.
- **Two projects name the same store.** Two brokers sharing one database file breaks the
  isolation §4.1 relies on, and must be refused.
- **A project names paths that do not exist yet.** A store is created on first serve, so a
  missing store file is normal and must not be an error. A missing *directory* for it is a
  different matter and should be reported.
- **The manifest is edited while a broker is running.** A running broker keeps the configuration
  it started with; nothing is reloaded underneath it.
- **One project of several fails to start.** Its store is corrupt, its migration fails, or its
  port is already held. The others must start, the failure must be named, and the process must
  not report success as though everything came up — an operator reading an exit code should not
  have to also read a log to learn that two of three brokers are running.
- **Every project fails to start.** Distinguishable from starting none because none were
  configured; the first is a failure and the second is an empty machine.
- **A launch configuration names a project on a machine whose manifest does not describe it.**
  This is the ordinary case for a repository cloned by a teammate, and it must fail loudly at
  launch rather than resolve to whatever the local default endpoint happens to be. Connecting an
  agent to the wrong deployment is worse than an agent that does not start.
- **A launch configuration names a project *and* carries an endpoint that disagrees with it.**
  The endpoint wins, and the disagreement is worth reporting rather than silently resolving.
- **A project is removed from the manifest while its agents are configured against it.** Those
  agents keep their own endpoint and continue to work; the manifest is not the source of truth
  for a running agent.
- **The well-known location is not writable**, or the environment variable naming the user's
  configuration directory is unset or points somewhere absent.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The system MUST support a single machine-level manifest describing zero or more
  named projects.
- **FR-002**: The manifest MUST live at one predictable per-user location on each supported
  platform, discoverable without configuration.
- **FR-003**: An operator MUST be able to override the manifest location explicitly for a single
  invocation.
- **FR-004**: Each project MUST carry, at minimum: a name, a store location, a transport
  endpoint, and a node catalogue location.
- **FR-005**: Project names MUST be unique within the manifest, and the system MUST refuse a
  manifest that repeats one.
- **FR-006**: Commands that act on a deployment MUST accept a project by name and derive every
  path from it.
- **FR-007**: When exactly one project is configured and none is named, the system MUST use that
  project.
- **FR-008**: When more than one project is configured and none is named, the system MUST refuse
  and list the configured names, rather than choosing.
- **FR-009**: Explicitly supplied values MUST take precedence over a project's values, and the
  system MUST report which source it used.
- **FR-010**: With no manifest present, every command MUST behave exactly as it did before this
  feature existed.
- **FR-011**: A manifest that exists but cannot be read or understood MUST be refused with a
  message naming the file, and MUST NOT be treated as describing no projects.
- **FR-012**: The system MUST refuse a manifest in which two projects share a store or an
  endpoint.
- **FR-013**: Any command that would write to a project's node catalogue MUST be able to report
  the exact file it would write before writing.
- **FR-014**: An operator MUST be able to list the configured projects with their locations and
  whether each is currently reachable.
- **FR-015**: The protocol MUST NOT gain any notion of a project: nothing crossing the wire, and
  nothing in storage, may carry a project identity. [Constraint, restated as a requirement
  because it is testable: no message field, no schema column.]
- **FR-016**: Agent names MUST remain free to collide across projects.
- **FR-017**: Consent to run unattended MUST remain per project, because a node catalogue belongs
  to one project.
- **FR-018**: The system MUST NOT require an existing deployment to adopt a manifest, at any
  point, to keep working.
- **FR-019**: When the broker is started with a manifest and no project named, it MUST start one
  broker per configured project in a single process, as SPEC.md §4.1 permits. Naming a project
  MUST start only that one.
- **FR-020**: A project that fails to start MUST NOT prevent the others from starting. Its
  failure MUST be reported and named, the remaining projects MUST continue, and the process MUST
  report that it is running a subset rather than exiting successfully as though all were up.
- **FR-021**: Stopping a multi-project process MUST stop every broker it started, and MUST NOT
  leave one holding a port or a store handle.
- **FR-022**: An agent's launch configuration MAY name a project in place of an endpoint, and MAY
  carry an explicit endpoint. Where both are present the explicit endpoint MUST win, consistent
  with the precedence used everywhere else.
- **FR-023**: When a launch configuration names a project that the machine's manifest does not
  describe, and carries no explicit endpoint, the agent MUST refuse to start with a message
  naming the project and the manifest consulted. It MUST NOT fall back to a default endpoint,
  because connecting to the wrong deployment is worse than not starting.
- **FR-024**: Naming a project in a launch configuration MUST NOT change how an agent's identity
  is established: the name and what it owns still come from the launch environment, and no
  operation may edit either.
- **FR-025**: The system MUST provide commands to add a project to the manifest, remove one, and
  list what is configured.
- **FR-026**: Adding a project MUST validate the whole manifest before writing, and MUST refuse
  an addition that duplicates an existing project's name, store or endpoint.
- **FR-027**: Adding a project MUST create the manifest, and the directory holding it, if absent.
- **FR-028**: Removing a project MUST remove only its entry from the manifest. It MUST NOT delete
  or modify that project's store, node catalogue, or any repository's launch configuration, and
  MUST say what it has left behind and where.
- **FR-029**: Removing a project that is currently reachable MUST report that a broker appears to
  be running for it, since the entry is the operator's record of how to reach it.

### Key Entities

- **Project manifest** — one per machine, at a known location. Holds the set of projects. Belongs
  to the operator, not to any repository, and is never read by the protocol.
- **Project** — a named, independent deployment: one store, one endpoint, one node catalogue, and
  the set of agents that connect to that endpoint. Isolation is a property of the store, not of
  the name.
- **Node catalogue** — an existing entity, now owned by a project rather than found beside the
  caller. Records which repositories this machine may start unattended.
- **Store** — an existing entity. One per project, opened only by that project's broker.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: An operator can run any deployment command against the correct project by naming
  it, from any working directory, with no other arguments.
- **SC-002**: No command writes to a node catalogue that the corresponding supervisor does not
  read. Verified by enrolling against a project whose catalogue is outside the working directory
  and confirming no second catalogue is created.
- **SC-003**: A machine hosting three independent projects requires zero path arguments in
  day-to-day use, down from at least one per command today.
- **SC-004**: An operator can determine what projects a machine is configured for, and where each
  one's files are, in a single command and under ten seconds.
- **SC-005**: 100% of existing deployments continue to work unchanged after upgrading, with no
  manifest present and no migration performed.
- **SC-006**: Every configuration error that would previously have surfaced as wrong behaviour —
  a duplicate endpoint, a duplicate store, an unreadable manifest — instead surfaces as a refusal
  naming the file and the problem, before anything is written or started.
- **SC-007**: Two projects can each contain an agent of the same name, indefinitely, with neither
  able to observe the other.
- **SC-008**: One command and one supervisor entry are enough to run every project on a machine,
  down from one of each per project.
- **SC-009**: When one project of several cannot start, the others are serving and the operator
  can tell which failed and why from the command's own output, without consulting a log.
- **SC-010**: An operator can add a project to a machine that has never had a manifest without
  knowing where the manifest lives, and a conflicting addition is refused before the file is
  modified.
- **SC-011**: An agent configured against a project that a machine does not know about fails to
  start with a message naming the project, and never connects to a different deployment.

## Assumptions

- **The manifest is JSON.** SPEC.md §4.1 illustrates the idea with `parley.yaml`, but every other
  file in this system is JSON — the node catalogue, the launch configuration, the enrollment
  manifest — and YAML would add a package dependency for one file. Recorded here because it means
  §4.1's illustration should be amended rather than implemented as written.
- **The well-known location follows the platform's convention for per-user application
  configuration**, resolved by the runtime rather than by hard-coded paths, so that it is correct
  on all three supported platforms without a per-platform branch.
- **Precedence runs**: explicit argument, then environment variable, then the named project, then
  the previous default. This keeps existing automation working and makes a manifest additive.
- **The manifest is not secret.** It holds paths and endpoints, not credentials; credentials
  remain references resolved at invocation, as the standing constraints require. It therefore
  needs no protection beyond ordinary file permissions.
- **A missing store file is normal** — brokers create one — while a missing directory to put it
  in is a configuration error worth reporting.
- **Reachability in a listing is observed, not recorded.** Whether a project's broker is running
  is discovered by looking, never stored in the manifest, for the same reason liveness is read
  from a heartbeat rather than written down.
- **Removal is withdrawal, not destruction.** Taking a project out of the manifest forgets how to
  reach it and leaves every file intact, mirroring the distinction the operator commands already
  draw between retiring an agent and purging one. Deleting a store is not something a
  configuration command should be able to do as a side effect.
- **Hosting several brokers in one process is a convenience, not an isolation boundary.** SPEC.md
  §4.1 is explicit that isolation lives in the storage layer: separate files, separate
  connections. Sharing a process changes only what a process-fatal fault takes with it, which is
  the risk any single service carries.
- **A project reference in a launch configuration is resolved before connecting**, on the machine
  the agent runs on. Nothing about a project crosses the wire, so FR-015 holds unchanged: the
  broker still cannot tell how an agent was configured to find it.

## Out of Scope

- Any change to the wire protocol or the storage schema.
- Remote or shared manifests: this describes one machine, not a fleet.
- Migrating an existing deployment into a manifest automatically.
- Reloading configuration into a running broker.
- Cross-project messaging. Two projects are separate by construction, and making them reachable
  to one another would contradict SPEC.md §4.1.
