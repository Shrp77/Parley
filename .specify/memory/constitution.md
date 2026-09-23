<!--
Sync Impact Report — 2026-09-14
Version change: 1.0.0 → 2.0.0 (MAJOR)
  Two backward-incompatible redefinitions, one of which had shipped without a bump:
  - Principle II was redefined by 0.26.0 (protocol 0.5, specs/014) — "an agent may refuse … and may not be overruled"
    became "declined only for a concrete blocker" — and the version stayed 1.0.0. Recorded here rather than left silent.
  - "Only question and answer text crosses. Never … files" is redefined for specs/015 (protocol 0.6): files placed in a
    project's file location cross between agents; their contents never enter a model's context through Parley.
    Its first clause also described protocol 0.4's question-and-answer channel, stale since 0.5.
Modified principles:
  - II. No agent has authority over another — text unchanged here; the 0.26.0 redefinition is now versioned.
  - IV. Terseness is a security control — expanded: what an operation returns about a file is bounded text, and a
    file's bytes go to disk, never into a context.
Modified sections:
  - Boundaries That Do Not Move — "Only question and answer text crosses" → "What crosses is messages and placed files,
    never contexts".
Added sections: none. Removed sections: none.
Templates and dependent documents:
  ✅ .specify/templates/plan-template.md — generic "Constitution Check"; no change needed
  ✅ .specify/templates/spec-template.md — no constitution-mandated sections affected
  ✅ .specify/templates/tasks-template.md — no principle-driven task types affected
  ✅ .specify/templates/commands/ — does not exist in this repository
  ✅ specs/015-file-store/plan.md, research.md, tasks.md — 1.1.0 references updated to 2.0.0; the boundary is no longer
     a violation; T009 is now a verification
  ✅ CLAUDE.md — the 015 plan entry records the amendment
  ✅ SPEC.md §1 and principle 2.2 — rewritten the same day: messages and, from protocol 0.6, placed files cross; a
     file's contents never enter a context; what crosses about a file is bounded
  ✅ README.md "What Parley does" — rewritten the same day for protocol 0.5, with file sharing marked as arriving in 0.27.0
Deferred TODOs: none.
-->

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

There is no orchestrator, no lead agent, and no plan that one agent imposes on another. **That is
not the same as agents declining each other's work, and Parley exists so that they do not.** Any
agent or person may request work of another repository's agent; each owner changes only its own
repository; and a request is declined only for a concrete blocker the receiver pursued and could
not resolve — missing information, a conflict, or a permission it does not have. No agent may
conclude a conversation over another's objection, and nothing ends work because a participant
was silent. The one asymmetry is the operator, who is a person and not an agent: operator
operations exist on a separate type, in a separate binary, so an adapter has nothing to bind by
accident, and a person may close a conversation whatever its participants have voted. Any design
that introduces authority between agents contradicts the premise.

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

**A file is not text that crosses.** Its bytes are written to disk in the receiving agent's own
repository and are never returned into a model's context by Parley. What an operation returns
*about* files — names, sizes, hashes, lists, change notices — is text that crosses, and is
bounded like any other: a name has a maximum length, and a list or a batch of notices has a
maximum count per call.

### V. Invariants live in storage

Application code can route around a check it makes itself; a database constraint refuses
every caller, including the one written next year and the one written by somebody else. A
protocol invariant belongs in the schema as a constraint, a trigger, or an index, and the
DDL carrying it is worth exactly what its test is worth. Hand-verifying a constraint and
moving on is not enough — the case is added so the next edit is checked too.

## Boundaries That Do Not Move

**What crosses between agents is messages and placed files, never contexts.** Requests,
Clarifies and Results cross as messages. Files cross only by being placed in a project's file
location and fetched from it onto disk, each carrying a content hash the broker computed. Never
conversation history, never another agent's context, and never a file's contents delivered into
a model's context by Parley. An operation that returns more about a peer than a participant needs
to address it is a disclosure, and must be argued as one. The agent-facing roster withholds
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

**Review is three consecutive clean runs per lens, not a rotation to convergence.** Three
lenses — correctness and concurrency; security and data integrity; API surface and
maintainability — run after the last edit rather than alongside it. A finding above low
severity resets that lens to zero; the lenses are independent and all three must reach three.
Each run must probe something the previous runs did not, or it is one run repeated.

Convergence-by-rotation was the earlier rule and was measured to be weaker: on
`004-terminal-surface` five rotation rounds reported convergence, and the counter's first six
runs then found four more defects. The running count belongs in `REVIEW-LEDGER.md`, which is
untracked because it is the state of one checkout's review rather than a fact about the
protocol, and which logs clean runs as well as findings — a ledger of findings alone cannot
distinguish an area that held from one nobody probed.

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

**A principle changes in its own amendment, never inside a feature's implementation.** A
feature that needs one records the argument in its specification, the constitution is amended
first with a Sync Impact Report at the top of this file, and the feature's plan then checks
against the amended text. Versions follow semantic versioning: MAJOR for a principle or boundary
removed or redefined, MINOR for one added or materially expanded, PATCH for wording. **An
amendment that changes what a principle permits is MAJOR even when its title is unchanged** —
0.26.0's redefinition of Principle II shipped without a version change, and 2.0.0 records it.

**Version**: 2.0.0 | **Ratified**: 2026-08-22 | **Last Amended**: 2026-09-14
