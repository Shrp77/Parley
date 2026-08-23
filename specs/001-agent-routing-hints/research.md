# Phase 0: what the existing code forces

Three findings changed the design. Each was in the code before this feature and none was
visible from the specification.

## R1. Registration writes nothing when the agent is already known

`BrokerSession` registers through `AgentRepository.RegisterIfAbsentAsync`, whose `INSERT`
ends in `ON CONFLICT(name) DO NOTHING`. The reasoning is written on the method and is sound:
a second launch must not revive a retirement by the side door, and must not rewrite an agent
an operator described in a manifest as Headless — with the endpoint the broker needs to invoke
it — into a configuration-free Pull agent because somebody opened that repository once.

**Consequence for this feature, if left alone:** `PARLEY_OWNS` would take effect on an agent's
first launch ever and never again. A statement that cannot be corrected is worse than none,
because every peer routes by it and nothing marks it stale.

**Resolved by** a narrow update alongside registration, touching the one column the launch
owns. It writes no status, so it cannot resurrect a retirement, and skips retired rows
outright; it writes no tier and no adapter configuration, so it cannot flatten a manifest.

**Rejected:** extending the same treatment to `workspace`. It has the same staleness — move a
repository, restart, and the registry still names the old path — and an existing test,
`Launching_over_an_operators_registration_leaves_it_exactly_alone`, states the contract that
forbids the fix: a launch that says nothing about a workspace must not erase what a manifest
said. A launch supplies `owns` or withdraws it; it cannot be silent about it in a way that
means "keep what you have". The two fields are not symmetric, and pretending they were is what
broke that test on the first attempt. The workspace staleness stays a separate problem.

## R2. `AgentSummary` is a projection, not a filter, and that is load-bearing

The stored record carries a workspace path, a session handle and the adapter's configuration
blob. None may cross to a peer. The type exists so that **a new column does not reach a peer
until somebody adds it here**, which is the moment to ask whether a peer should see it.

This feature is that moment, taken once, for one field. `MixedWorkloadTests` enumerates the
projection's members and fails when the list changes, which is what made the decision visible
rather than incidental — the test had to be edited, with a reason, for the field to ship.

## R3. The specification's own schema is executed, so it is not documentation

`SpecSchemaTests` runs every DDL fragment in `SPEC.md` against SQLite, and
`SchemaConformanceTests` compares the columns and the CHECK-constraint literals in the document
against the migration. Adding a column to the code and not to `SPEC.md` fails the build.

That is the mechanism behind "specification first" being a rule rather than an aspiration
here, and it caught the first attempt: the column was added to the migration and the document
was a commit behind.

## R4. Section references in `SPEC.md` resolve to headings only

`Every_section_reference_resolves` checks `§n.m` against the document's headings. The numbered
items of §17 and the numbered principles of §2 are not headings, so `§17.10` and `§2.2` fail
even though both exist. The document's own convention is `principle 2.2` and `rule 8.5` — no
section sign. Followed here.

## What was measured rather than assumed

The cap. It was written as 160 on the reasoning that a boundary statement is prose and prose
wants room. `The_roster_for_a_project_costs_less_than_a_message` then measured a ten-agent
roster at **2,352 characters** against a 2,000-character message cap — so under the original
number, finding out who to ask cost more than asking. A roster entry is about 75 characters of
structure before the statement, which puts the limit at 100 and the ten-agent roster
comfortably under one message.

The number in the schema is therefore an output of the design rather than an input to it,
which is worth saying because 160 looked obviously fine.
