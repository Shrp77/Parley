# Feature Specification: What each agent owns, in the registry

**Feature Branch**: `001-agent-routing-hints`
**Created**: 2026-08-22
**Status**: Draft
**Input**: User description: "Let an agent learn what each peer owns from the registry rather than from hand-written standing instructions"

## Context

`SPEC.md` §17 question 10 records the open question this answers. The agent operation `Agents` returns
a name, an adapter, a tier and a status. Nothing in the protocol says what any of those agents
owns, so the only routing signal an asker has inside the protocol is a name. Everything else —
that `billing` owns tax, that this repository does not — lives in whatever file the deployment
uses for standing instructions, written once per repository and maintained by hand.

That has been survivable because a wrong guess is cheap: `out_of_scope` carries `redirect_to`,
referral is a first-class outcome, and *refer, do not relay* keeps the correction to one hop.
It is still N-1 copies of the same ownership map, in N repositories, drifting independently
and able to disagree with each other about the same boundary.

## Clarifications

### Session 2026-08-23

- **Q: does the Claude Code plugin's `/parley:peers` generate its block from the roster in this
  feature?** No — a follow-up. The command needs its hand-authored path regardless, because no
  installed server carries the field until somebody rebuilds, so doing it now means writing the
  fallback twice. The files also belong to an open pull request. Nothing is lost by waiting
  except that the feature ships one release ahead of its most visible consumer.
- **Q: should a server with no ownership statement say so at start-up?** No. It starts silently,
  exactly as it does for an unset workspace, and the operator's registry listing shows the gap
  explicitly instead. A note on standard error would reach whichever log the client keeps for a
  server that is misbehaving, which is the place least likely to be read while nothing is wrong,
  and it would fire on every launch of every deployment that left the variable unset on purpose.
  The listing costs nothing and is read by the one person who can act on it.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - An agent chooses a recipient from the roster alone (Priority: P1)

An agent working in the `orders` repository reaches a question that turns on VAT. It lists the
agents it can address and sees, beside each name, one line saying what that agent owns. It
addresses `billing` because billing's line says invoicing and tax are its, not because
somebody wrote a paragraph about billing into this repository months ago.

**Why this priority**: This is the whole feature. Without it the roster remains a list of
names and the routing knowledge stays in per-repository prose. Every other story is a
refinement of this one.

**Independent Test**: Register two agents with ownership statements, connect as a third, call
the roster operation, and confirm the response carries each peer's statement. Then confirm an
agent given no repository-local prose about its peers can still name the right recipient.

**Acceptance Scenarios**:

1. **Given** `billing` has registered with an ownership statement, **When** `orders` lists the
   agents it can address, **Then** the response carries billing's statement beside its name.
2. **Given** `inventory` registered without one, **When** `orders` lists agents, **Then**
   inventory appears with no statement rather than with an invented or defaulted one.
3. **Given** an agent lists the roster, **When** it reads its own row, **Then** there is none —
   the caller is excluded from the listing as it is today.

---

### User Story 2 - An operator declares a boundary once (Priority: P1)

The person configuring the `billing` repository writes one line saying what billing owns, in
the same launch configuration that fixes its name. Every agent in the project sees that line.
When billing's scope changes, they change it in one place and every peer sees the new one the
next time billing's server starts.

**Why this priority**: Equal to the first, because it is what makes the statement worth
trusting. The objection to putting ownership in the registry is that it is self-declared and
therefore no better than the prose it replaces. It is only self-declared if a *model* declares
it. Sourced from the launch environment, it is declared by the same person, in the same file,
by the same mechanism that already fixes identity — and no tool call can change it.

**Independent Test**: Start a server with an ownership statement in its environment, confirm
the registry holds it; restart with a different one, confirm the registry holds the new one;
confirm no operation exposed to an agent can write it.

**Acceptance Scenarios**:

1. **Given** a launch configuration carrying an ownership statement, **When** the server
   starts, **Then** the registry records it against that agent.
2. **Given** an agent already registered with a statement, **When** its server restarts with a
   different one, **Then** the registry holds the new statement and no other field changes.
3. **Given** any agent operation, **When** an agent attempts to set an ownership statement —
   its own or another's — **Then** no operation accepts one, because none takes it as a
   parameter.
4. **Given** an ownership statement was set and is then removed from the launch configuration,
   **When** the server restarts, **Then** the registry records its absence rather than keeping
   the stale value.

---

### User Story 3 - A misrouted sub-question is referred to the right owner (Priority: P2)

`orders` asks `billing` a question of which one sub-question turns out to be inventory's.
Billing answers what it owns and refers the rest — and it can name the right owner because the
roster told it who that is, rather than guessing from a name.

**Why this priority**: Referral already works and already carries a list of names. This makes
the referral better informed rather than making it possible, so it is a refinement of an
existing path.

**Independent Test**: With three agents registered and ownership statements set, confirm an
answerer can resolve a sub-question `out_of_scope` naming a peer whose statement covers it,
and that the existing constraint refusing a referral to an unregistered agent still holds.

**Acceptance Scenarios**:

1. **Given** three registered agents with statements, **When** an answerer refers a
   sub-question, **Then** the referral names a registered agent and is accepted.
2. **Given** a referral naming an agent that is not registered, **When** it is stored, **Then**
   it is refused as it is today.

---

### Edge Cases

- **Two agents claim the same ground.** The registry records descriptions; it does not
  adjudicate them and MUST NOT. There is no uniqueness constraint over ownership statements
  and no arbitration, because no agent has authority over another and the broker is not a
  registry of record for what a team has agreed. Overlap surfaces as two plausible recipients
  and is resolved by asking one, which refers if it is wrong.
- **An agent claims everything.** Nothing prevents a statement of "all backend concerns". The
  cap makes it costly to be expansive and specific at once, and the operator authored it, so
  this is a deployment problem rather than a protocol one.
- **The statement is stale.** It is as fresh as the last time that agent's server started,
  which is worse than live and better than a file in a repository nobody has opened this
  quarter. A restart is the update mechanism, and agents restart constantly.
- **The statement carries an instruction rather than a description.** This text lands in the
  working memory of every agent that lists the roster, which makes it an injection surface.
  A statement spanning several lines could imitate the structure of the listing around it.
  Single line, bounded, control characters rejected.
- **A retired or unreachable agent.** Its statement is still shown beside its status. An asker
  needs to know that the thing it was going to ask is that agent's *and* that the agent cannot
  be reached, or it will keep looking for someone else who owns it.
- **The project has many agents.** The listing grows by one bounded line per agent. See
  SC-004.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The registry MUST be able to record, for each agent, one statement of what that
  agent owns.
- **FR-002**: The statement MUST come from the environment the agent's server was launched
  with, and from nowhere else. No agent operation may accept it as a parameter.
- **FR-003**: The statement MUST be optional. An agent registered without one is recorded as
  having none, and the absence MUST be reported as absence rather than as an empty or
  defaulted value.
- **FR-004**: Registration MUST overwrite the recorded statement with the one the current
  launch supplies, including replacing a previous statement with none.
- **FR-005**: The statement MUST be bounded in length, and the bound MUST be enforced in
  storage so that no code path can write a longer one.
- **FR-006**: The statement MUST be a single line and MUST NOT contain control characters. A
  value that is not MUST be refused at start-up, in the same way and for the same reason that
  a malformed agent name is.
- **FR-007**: The agent-facing roster operation MUST return each listed agent's statement
  beside its name, adapter, tier and status.
- **FR-008**: The agent-facing roster MUST continue to withhold everything it withholds today.
  Adding this field MUST NOT widen the projection in any other respect.
- **FR-009**: The operator's registry listing MUST show the statement alongside the workspace
  it already shows.
- **FR-011**: The specification document MUST describe the field, its bound, and its source,
  and MUST resolve open question §17 question 10 by recording the decision and the argument for it.
- **FR-012**: Guidance an agent receives on connecting MUST tell it that the roster says what
  each peer owns, so that listing peers is the first move rather than a fallback.

### Key Entities

- **Agent registration**: what the registry holds about one agent — its name, how to reach it,
  what it is doing, and now one line about what it owns. The name is the identity; the
  ownership statement is a hint for choosing between identities, and carries no authority.
- **Ownership statement**: operator-authored prose, bounded, single-line, optional, replaced
  on every registration. It is a description, not a claim the broker enforces: nothing is
  routed differently because of it, and nothing is refused because of it.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: An agent given no repository-local prose about its peers, connected to a project
  where the peers have ownership statements, addresses the right peer for a question that
  crosses a boundary.
- **SC-002**: The ownership statement for an agent exists in exactly one place per deployment —
  that agent's own launch configuration — rather than once in each of the other repositories.
- **SC-003**: No agent operation accepts an ownership statement as a parameter, verified the
  way the absence of a sender parameter is verified today: by enumerating the surface.
- **SC-004**: A roster response for a project of ten agents, every one of them at the limit,
  remains smaller than a single message is allowed to be, so finding out who to ask costs less
  than asking. Measured, not asserted: this is what set the limit.
- **SC-005**: A statement containing a line break, a control character, or more characters
  than the bound never reaches storage, demonstrated by attempting each.

## Assumptions

- The bound is **100 characters**. It began at 160, which is comfortable prose length, and the
  measurement in SC-004 refused it: ten peers at 160 cost 2,352 characters against a
  2,000-character message, so listing peers cost more than asking one of them. A roster entry
  costs about 75 characters of structure before the statement, which puts the limit at 100.
  *"Invoicing, tax and payment capture. Does not price orders or reserve stock."* is 74.
- The launch variable is `PARLEY_OWNS`, following the existing `PARLEY_` naming and set in the
  same place as `PARLEY_AGENT`.
- Nothing is deployed anywhere yet, so the column goes into the initial schema and there is no
  migration. That is a fact about the moment rather than a policy: the first release is where
  migrations start mattering.
- The statement is advisory. The broker does not route on it, refuse on it, or compare it
  against anything. Making it enforceable would make the registry an authority over what an
  agent may be asked, which no agent has over another.
- Ownership statements are visible to every agent that can list the roster, which is every
  registered agent. A deployment that cannot tolerate that leaves the variable unset; the
  feature degrades to today's behaviour.
- The existing `Peers` section in a repository's standing instructions is not replaced by
  this. It says what *this* repository does not own, which is a first-person statement no peer
  can make on its behalf. What moves to the registry is the other half — what each peer owns.
