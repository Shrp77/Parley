# Feature Specification: A repository may answer in more than one project

**Feature Branch**: `009-multi-project-enrolment`
**Created**: 2026-09-09
**Status**: Draft
**Input**: A repository may be an agent in more than one project at once. The launch configuration's
server key becomes `parley-<project>` instead of the fixed `parley`, so one repository can hold one
entry per project it belongs to.

## Why this exists

**The protocol already permits this and the tooling forbids it.** SPEC.md §4.1:

> **The protocol has no concept of a project, and must not acquire one.** Membership is which
> endpoint an agent connects to. There is no group column, no scoped query, and no `WHERE` clause
> anyone can forget: a broker's connection cannot see another's file.

> It also means agent names collide freely across projects. Two projects may each have an `orders`
> agent, because they are different files.

`ProjectManifest.Validate` agrees: it refuses a repeated project name, store file, endpoint or node
catalogue path, and says nothing about a workspace. Two registries on one machine may each hold a row
for `inventory@this-machine` pointing at the same directory, and neither broker can see the other.

What forbids it is one line of deployment tooling. A repository holds a single `.mcp.json` entry under
the fixed key `parley`, carrying one `PARLEY_AGENT` and one `PARLEY_PROJECT`, and enrolling it
somewhere else is refused:

> **A repository that already belongs somewhere is refused rather than re-pointed.** Naming a second
> project would move an agent out of one deployment's roster and into another's, silently, from a
> surface whose person may not know the first exists.

**That reasoning is right about the thing it examined and answers a different question.** It is about
*re-pointing* — one identity moving between deployments, which does lose an agent. It was never an
argument against a repository having *two* identities, and the refusal covers both cases only because
one key can hold one entry.

**The cost is paid by exactly the repositories worth asking.** Brokers do not federate: an agent in
one project cannot reach an agent in another at all. So a repository that several product lines
depend on — the infrastructure one, the schema one, the design system — must pick one set of peers and
be unreachable to the rest. The teams that most need to ask it are the ones that cannot.

## What is being changed, and what is not

**Not the protocol, and this was checked rather than assumed.** SPEC.md never mentions `.mcp.json`,
`mcpServers` or a server key; the launch configuration appears twice, both times as "the launch
configuration" with no shape given. The document is untouched by this feature, which is what makes it
deployment tooling rather than a protocol change. *(§13 and §11.5 are the sections that would have
had to move, and neither describes a file.)*

**Not the refusal's reasoning.** Re-pointing is still refused. What is admitted is multiplicity: a
second entry beside the first, never a first entry rewritten to mean somewhere else.

**Not the legacy spelling.** Every repository enrolled to date has the key `parley`. The rule is 007's,
in 007's words — *stop writing, stop accepting, keep reading* — with the middle third dropped, because
`parley` is still what an unnamed deployment writes. **A legacy entry is never renamed in place**: the
key becomes part of every tool name the agent sees (`mcp__parley__parley_ask`), so renaming it changes
the vocabulary of a working agent to fix nothing.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A shared repository answers two product lines (Priority: P1)

An operator runs two projects on one machine, `alpha` and `beta`. One repository — the infrastructure
one — holds facts both need. Today it is enrolled in `alpha` and `beta`'s agents cannot ask it
anything. The operator enrols it in `beta` as well, under a name of `beta`'s choosing, and both
rosters now list it.

**Why this priority**: it is the whole feature. Every other story is a consequence of admitting it.

**Independent Test**: enrol one repository in two projects, start both brokers, and ask it a question
from each. Both answers come back, and neither roster lists the other project's agents.

**Acceptance Scenarios**:

1. **Given** a repository already the agent of `alpha`, **When** it is enrolled in `beta`, **Then**
   the enrolment succeeds, the file holds two entries, and the `alpha` entry is byte-for-byte
   unchanged.
2. **Given** a repository enrolled in `alpha` and `beta`, **When** an agent of `alpha` asks it a
   question, **Then** the answer is attributed to the name `alpha` knows it by, and no message
   crosses to `beta`.
3. **Given** the same repository, **When** somebody lists either project's registry, **Then** it
   appears in both, under each project's own name for it, with each project's own `owns` line.

---

### User Story 2 - A repository enrolled before this keeps working (Priority: P1)

Every repository in an existing deployment has a `parley` entry. Nobody edits any of them, and nothing
about those deployments changes.

**Why this priority**: the same priority as the feature, because a migration that quietly breaks a
live deployment is not a feature. The 18-repository deployment this was written against is the test.

**Independent Test**: take a repository with a legacy entry, run every read path over it, and confirm
each reports it enrolled and names the right project. Then re-enrol it into the project it is already
in and confirm nothing is renamed.

**Acceptance Scenarios**:

1. **Given** a `parley` entry pinned to `alpha`, **When** anything asks what the repository is,
   **Then** it answers "an agent of `alpha`" and names the key it found.
2. **Given** a `parley` entry pinned to `alpha`, **When** it is enrolled in `alpha` again, **Then**
   the existing entry is updated in place and **no `parley-alpha` key is created**.
3. **Given** a `parley` entry pinned to `alpha`, **When** it is enrolled in `beta`, **Then** a
   `parley-beta` entry is added and the `parley` entry is left exactly as it was.
4. **Given** a `parley` entry naming no project at all — the shape every repository enrolled before
   projects existed has — **When** it is enrolled in `alpha`, **Then** it is re-pointed in place as it
   is today, and the run says that the repository has become an agent of `alpha`.

---

### User Story 3 - Withdrawal takes one project's entry and no other (Priority: P1)

An operator retires the repository from `beta` and it goes on answering in `alpha`.

**Why this priority**: withdrawal is the only path that deletes somebody's configuration, and this
repository has already shipped one withdrawal that removed a different agent's entry and reported
success. Multiplying the entries multiplies that failure.

**Independent Test**: enrol in two projects, withdraw from one, and read the file: one entry gone, one
untouched, and the agent still answering in the project not named.

**Acceptance Scenarios**:

1. **Given** entries for `alpha` and `beta`, **When** the agent is withdrawn from `beta`, **Then**
   only `parley-beta` is removed and the run says how many parley entries remain.
2. **Given** entries for `alpha` and `beta`, **When** a withdrawal names an agent whose name does not
   match the entry for that project, **Then** nothing is removed and the run says whose entry it
   found — the existing guard, now matching on project as well as name.
3. **Given** a repository with several parley entries, **When** a withdrawal names no project at all,
   **Then** nothing is removed and the run names every project the repository belongs to.
4. **Given** a repository with exactly one parley entry under any spelling, **When** a withdrawal
   names no project, **Then** that entry is removed, because there is nothing to be ambiguous about.

---

### User Story 4 - A person can see everywhere a repository answers (Priority: P2)

Somebody scanning a directory of repositories is told, for each one, every project it already answers
in — not the first one found.

**Why this priority**: it is how the operator avoids enrolling a repository twice in the same project
by accident, and how they discover the ones that are already multiply enrolled. It is not P1 because
the feature works without it; it is only harder to see.

**Independent Test**: scan a directory holding one unenrolled repository, one in a single project and
one in three. Each row says what is true of it.

**Acceptance Scenarios**:

1. **Given** a repository enrolled in three projects, **When** it is scanned, **Then** all three are
   reported, each with the name and `owns` line that project knows it by.
2. **Given** a repository with no parley entry, **When** it is scanned, **Then** it is reported as
   enrolled nowhere, which is distinct from unreadable.

---

### Edge Cases

- **The key is already taken by something else.** `parley-alpha` exists and is not this tooling's
  entry — a server somebody wrote by hand, or another tool's. It is refused and named, never
  overwritten. Three separate instances of silently replacing a person's MCP configuration are
  recorded in this codebase's comments; this is the fourth opportunity and it is closed in advance.
- **An entry pinned to a raw broker address.** The legacy shape `PARLEY_BROKER: 127.0.0.1:51707` names
  no project, so nothing can be derived from it by inspection alone — but the manifest on the machine
  knows which project holds that endpoint, and resolving it there gives a decidable answer rather than
  a guess. See FR-008.
- **A project named `parley`.** The key is `parley-parley`, which collides with nothing: a project name
  is at least one character, so `parley-<project>` can never be the legacy key.
- **The same workspace catalogued for unattended start in two projects.** Each project's node would
  start it, in the same working tree, because a resident agent runs with its workspace as the working
  directory. Two models editing one checkout. See FR-013.
- **A deployment that names no projects.** SPEC.md §4.1 keeps this supported — "a deployment that names
  no projects is configured entirely by explicit paths, exactly as before". There is no project name to
  build a key from, so the key is `parley`, which is both the legacy spelling and the correct one.
- **`.mcp.json` unreadable, or `mcpServers` the wrong shape.** Unchanged: refused and reported, never
  tidied.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: A repository's launch configuration MUST be able to hold more than one parley entry, one
  per project it answers in, each with its own agent name, project and `owns` line.
- **FR-002**: An entry written for a named project MUST use the key `parley-<project>`.
- **FR-003**: An entry written when no project is named MUST use the key `parley`.
- **FR-004**: Every path that reads a repository's enrolment MUST find entries under both spellings,
  and MUST decide whether an entry is this tooling's from the key alone.
- **FR-005**: An existing entry for the project being enrolled into MUST be updated in place under
  whatever key it already occupies. Nothing is ever renamed.
- **FR-006**: Enrolling into a project the repository does not already answer in MUST add an entry and
  leave every existing entry unchanged.
- **FR-007**: An entry naming no project, when a project is named, MUST be re-pointed in place and the
  change of allegiance said out loud — the behaviour that exists today, unchanged.
- **FR-008**: An entry pinned to a broker address MUST be resolved through the machine's project
  manifest: if the address is the endpoint of the project being enrolled into, the enrolment is a
  duplicate and is refused; if it is another project's endpoint, the enrolment proceeds as an addition;
  if it matches no project the manifest knows, it is refused and the address is named, because
  admitting it in the permissive direction is what loses an agent.
- **FR-009**: A key this tooling would write that is already held by an entry it did not write MUST be
  refused, naming the key, and MUST NOT be overwritten or merged into.
- **FR-010**: Withdrawal MUST remove only the entry for the project named, MUST match the agent's name
  back before removing anything, and MUST report how many parley entries remain.
- **FR-011**: Withdrawal naming no project MUST remove the single entry when there is exactly one, and
  MUST refuse and name every project the repository belongs to when there is more than one.
- **FR-012**: A scan MUST report every project a repository already answers in, with the name and
  `owns` line each project knows it by, and MUST distinguish "enrolled nowhere" from "could not be
  read".
- **FR-013**: A workspace MUST NOT be catalogued for unattended start in more than one project on one
  machine. The second is refused, naming both projects and the workspace. *(A resident agent runs with
  its workspace as its working directory, so two nodes starting one workspace is two models writing one
  checkout. A launch entry is inert until a person opens the repository; a catalogue entry is standing
  consent to start without one, which is the half that must stay singular.)*
- **FR-014**: Every illustration of a launch configuration a person is shown — the README, the plugin's
  setup instructions, the console's registration page, and **the adapter's own header comment** — MUST
  show the project-keyed shape and say what the bare `parley` key means. *(The fourth was found by the
  one-key check rather than by the survey that produced the first three, which is the argument for the
  check: an illustration that disagrees with the tool is read by exactly the person who has not used
  the tool yet.)*
- **FR-015**: A repository already answering in a project MUST be told so plainly when somebody enrols
  it there again, rather than the run reporting a change it did not make.

### Key Entities

- **Launch entry**: one server in a repository's `.mcp.json`, keyed `parley` or `parley-<project>`,
  carrying the agent's name, the project, what it owns, and its workspace. One per project.
- **Project**: a name, a store, an endpoint and a node catalogue on one machine. Unchanged by this
  feature except that its name now appears in a key.
- **Node catalogue entry**: standing consent for a machine to start a repository as an agent with
  nobody watching. Per project today, and now at most one per workspace across all projects.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: One repository, enrolled in two projects on a live deployment, answers a question from
  each project's peers, and neither project's roster lists the other's agents.
- **SC-002**: Every repository in an existing deployment keeps answering with no edit to any file, and
  the tool names its agent sees are unchanged. Verified by reading a live deployment's files before and
  after, not by reasoning about the reader.
- **SC-003**: Withdrawing a repository from one project leaves it answering in every other, verified by
  asking it a question afterwards.
- **SC-004**: No path in the feature removes, replaces or merges into an MCP server entry this tooling
  did not write, under any input, including a hand-written entry occupying the key it wants.
- **SC-005**: Every surface that says what a repository is — the scan, the enrolment plan, the
  withdrawal, the console and the terminal — reports the same set of projects for the same repository.
- **SC-006**: An attempt to catalogue one workspace for unattended start in two projects is refused
  with both project names in the message.

## Assumptions

- **Project names are safe as key components.** `ProjectName` is `^[a-z][a-z0-9-]{0,31}$` — lowercase
  letters, digits and hyphens, starting with a letter — so `parley-<project>` is at most 39 characters,
  needs no escaping in JSON or in a tool name, and is injective: two projects cannot produce one key.
- **A person opens one editor on one checkout.** Two launch entries in one repository mean two servers
  in whichever client opens it, which is two attachments for one process and is already an ordinary
  shape (a person seated in a browser and a terminal at once was settled in 004). The unattended case
  is the one that needs a rule, and FR-013 is it.
- **The manifest is available where enrolment runs.** FR-008 resolves a broker address through it. Where
  there is no manifest, an address-pinned entry is refused as it is today.
- **`owns` differs per project, and that is the point.** The same repository is worth asking about
  different things by different rosters, so nothing forces the entries to agree.
- **No registry change.** Two projects are two stores; a repository in both is two ordinary
  registrations that no broker can see the other half of.
