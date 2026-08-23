# Parley Constitution

Parley is a cross-repository agent-to-agent messaging protocol. `SPEC.md` is the primary
artifact of the repository; the code is a reference implementation of it. These are the
premises a change is measured against. A change that contradicts one is a design change, not
an implementation detail, and must be argued rather than assumed.

## Core Principles

### I. The specification is the product

`SPEC.md` is what other implementations build against; this repository's code is one
implementation of it. When code and specification disagree, that is a defect in one of them,
and **which one is a decision, not an assumption** — neither may be silently changed to match
the other. A protocol change is a specification change first and code second, and the
specification must be able to stand alone for somebody who will never read the code.

### II. No agent has authority over another

There is no orchestrator, no lead agent, and no plan that one agent imposes on another. An
agent may refuse, may decline to conclude, and may not be overruled by a peer. The one
asymmetry is the operator, who is a person and not an agent: operator operations exist on a
separate type, in a separate binary, so an adapter has nothing to bind by accident. Any
design that introduces authority between agents contradicts the premise.

### III. Identity is fixed by the launch, never by a call

An agent's name comes from the environment its server was started with and from nowhere
else. No operation takes a sender parameter, so no call can change who the caller is. This
is what makes "a sender cannot forge the agent it claims to be" a structural guarantee
rather than a model's good behaviour, and no feature may reintroduce a caller-supplied
identity by another name.

### IV. Terseness is a security control

The message cap is a context-poisoning defence, not a style preference, and it lives in the
schema so no code path can route around it. Anything that crosses the boundary between
agents — a message, a roster entry, a field returned by an operation — is charged against
the working memory of every agent that receives it. A new field that can hold a paragraph
will eventually hold one, so **a bound is part of the design of any new text that crosses**,
not a refinement added later.

### V. Invariants live in storage

Application code can route around a check it makes itself; a database constraint refuses
every caller, including the one written next year and the one written by somebody else. A
protocol invariant belongs in the schema as a constraint, a trigger, or an index, and the
DDL carrying it is worth exactly what its test is worth. Hand-verifying a constraint and
moving on is not enough — the case is added so the next edit is checked too.

## Boundaries That Do Not Move

**Only question and answer text crosses.** Never conversation history, never files, never
another agent's context. An operation that returns more about a peer than an asker needs to
address it is a disclosure, and must be argued as one. The agent-facing roster withholds
workspace paths for exactly this reason.

**Agent operations and operator operations are different surfaces.** Not merely differently
named: they live on different types, and the operator's are not compiled into the server an
MCP client launches. A new operation is a decision about which side it belongs on.

**Credentials are references, never values** — `env:`, `keychain:`, `file:`. The broker
resolves them at invocation and never persists or logs them. This is what makes a manifest
safe to commit.

**The protocol is the product; the transport is disposable.** If a platform ships native
cross-session messaging, Parley should run over it rather than compete with it.

**`Parley.Core` has no I/O and no package dependencies.** A `using` that pulls I/O into the
domain is a review finding, and its tests do not reference the storage assembly.

## Quality Gates

**A green build is the whole lint gate.** Warnings are errors, documentation is enforced,
culture-sensitive comparison and unparameterised SQL are errors, and package versions are
centralised. There is no separate lint step, and a rule the compiler cannot enforce is a
rule that decays.

**Every claim the documents make is checked.** `tests/Parley.Spec.Tests` executes every
schema fragment in `SPEC.md` in the order written, checks that section references resolve
and that tables are well formed. The README and the plugin are held to the same standard:
documentation that tells someone what to type is documentation the code can be measured
against.

**Probe; do not reason.** The highest-value findings in this repository were found by
writing the malicious case and watching it be admitted, not by reading a guard and judging
it sound. A new invariant is verified by reconstructing the defect it exists for and
watching the check fail.

**Review runs to convergence, not to a fixed count.** Three lenses — correctness and
concurrency; security and data integrity; API surface and maintainability — rotated after
the last edit rather than alongside it, repeated until a full rotation finds nothing above
low severity. A pass that finds something means the branch was not green.

**The repository is public and its history goes with it.** No trace of any originating
private project may appear in any commit. Placeholder agents are `orders`, `billing` and
`inventory` throughout.

## Governance

This constitution restates the standing constraints recorded in `CLAUDE.md` in the form
Spec Kit's checks read. **`CLAUDE.md` and `SPEC.md` remain the authority when they and this
document disagree**; this file is a projection of them, not a second source of truth, and an
amendment here that is not also made there is a defect.

A feature specification, plan, or task list that contradicts a principle above must say so
explicitly and carry the argument for the change, in the specification rather than in a
commit message. Design rationale belongs in the pull request body, which is the durable
record: merges are squash-only, so the pull request title becomes the commit message.

**Version**: 1.0.0 | **Ratified**: 2026-08-22 | **Last Amended**: 2026-08-22
