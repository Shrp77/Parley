<!--
Sync Impact Report — 2026-10-06
Version change: 5.0.0 → 6.0.0 (MAJOR)
  Two changes for specs/026 (roadmaps), amended BEFORE any of its code, as governance requires. This
  file's own rule makes the second MAJOR: it changes what Principle IV permits.
  - Principle II. A roadmap is added as a thing the person owns, so that it is not read as a plan one
    agent imposes on another. An agent may only propose one; a person confirms, edits and declines it;
    no agent operation reads, changes or marks done a roadmap. Nothing about the facilitator changes:
    it may not propose or confirm one.
  - Principle IV. Roadmap text has no bound by default, as the ownership statement has none. The text
    is read by a person, not by an agent, and what crosses to an agent is one item inside a Request,
    which keeps the message cap. Agents are guided to short, point-oriented items by a template in the
    proposing tool, not by a limit. The character class that stops text breaking a surface's lines is
    NOT relaxed. Recorded as a reduction in this principle's protection, with its residual risk
    (any attached agent can write a large proposal into the store) stated rather than left to be found.
Modified principles: II and IV, as above.
Modified sections: none beyond those principles. Added sections: none. Removed sections: none.
Templates and dependent documents:
  ✅ CLAUDE.md — the standing constraints "No agent has authority over another" and "Terseness is a
     security control" carry both changes in the same commit
  ✅ specs/026-roadmaps/plan.md — its Constitution Check is re-run against 6.0.0
  ⚠️ SPEC.md §3, §5.7, §9.2, §9.3, §10.1, §10.2, §17, §18 — NOT yet amended. They are 026's first tasks
     and come before any code (Principle I)
  ✅ .specify/templates/ — no principle-driven sections affected
Deferred TODOs: SPEC.md, as above. It is the feature's own task, not this amendment's.
-->

<!--
Sync Impact Report — 2026-09-30
Version change: 4.0.0 → 5.0.0 (MAJOR)
  One redefinition for specs/023 (the person always has a seat), amended BEFORE any of its code, as
  governance requires. research.md R9 found that 023 changes what Principle III permits.
  - Principle III. The facilitator was "the one identity that no launch fixes". A second is added: the
    person seat, `human-user@<project>`, which the broker provides in every project it serves and
    never releases. Every surface that reaches the broker speaks as it; nothing launches it. Both
    broker-provided identities are named for their project and have no machine. The role is reserved
    in storage, and the limit of that is stated in the principle rather than left to be discovered:
    an attach under the existing row writes no new row, so the guard against a claimant is code that
    trusts the adapter the caller reports, as SPEC.md §16 already concedes of every name.
  MAJOR by this file's own rule: it changes what the principle permits.
Modified principles: III, as above.
Modified sections: none beyond that principle. Added sections: none. Removed sections: none.
Templates and dependent documents:
  ✅ CLAUDE.md — the Parley.Web seat paragraph: "a seat must not outlive the person" becomes "a seat
     outlives the person's presence; the claim that they are present does not", in the same commit
  ⚠️ specs/023-always-a-seat/plan.md — its Constitution Check is re-run against 5.0.0 (T002)
  ⚠️ SPEC.md principle 2.6, §3, §4.0, §4.1.1, §5, §8 rule 10, §9.2, §10.1, §10.2, §12.3, §14, §17 q2,
     §18 — NOT yet amended. They are 023's T003–T007 and come before any code (Principle I)
  ✅ .specify/templates/ — no principle-driven sections affected
Deferred TODOs: SPEC.md, as above. It is the feature's own task, not this amendment's.
-->

<!--
Sync Impact Report — 2026-09-28
Version change: 3.0.0 → 4.0.0 (MAJOR)
  Three redefinitions for specs/022 (Parley as facilitator), amended BEFORE any of its code, as
  governance requires. research.md finding 1 found that 022 changes what Principles II and III
  permit; finding 2 found that SPEC.md says, in four places, that the broker is not a participant.
  - Principle II. The operator was "the one asymmetry". A second is added: the facilitator, a
    participant the broker provides in every project. It is neither an agent nor a person. It may
    ask a question, send a reply a person wrote (attributed to that person), and propose and agree
    that its own conversation is done. It may do nothing else, and in particular it may not assign,
    direct, prioritise, close or override anybody's work, or choose where an agent's work goes.
  - Principle III. "An agent's name comes from the environment its server was started with and
    from nowhere else." The facilitator is not an agent. Its identity is fixed by the broker, for
    the broker's own store, and no launch and no call can claim it. Storage enforces this.
  - Principle IV. Auto Accept is recorded as a further reduction in the ownership control, on top
    of 3.0.0's removed bound: with it on, an agent's own answer becomes its line with no person in
    the loop. What replaces the person is truthful provenance (`automatic`) and a review.
  MAJOR by this file's own rule: each of the three changes what a principle permits.
Modified principles: II, III, IV, as above.
Modified sections: none beyond those principles. Added sections: none. Removed sections: none.
Templates and dependent documents:
  ✅ CLAUDE.md — the standing constraint "No agent has authority over another" names the facilitator
     and what it may never do, in the same commit, since an amendment not made there too is a defect
  ✅ specs/022-parley-facilitator/plan.md — its Constitution Check is re-run against 4.0.0 (T052)
  ⚠️ SPEC.md §3, §5, §9.3, §10.1, §11.1, §11.3, §12.3, §14, §17 q2 and q10, §18, principle 2.10 —
     NOT yet amended. They are 022's T002–T008 and come before any code (Principle I)
  ✅ .specify/templates/ — no principle-driven sections affected
Deferred TODOs: SPEC.md, as above. It is the feature's own task, not this amendment's.
-->

<!--
Sync Impact Report — 2026-09-26
Version change: 2.0.0 → 3.0.0 (MAJOR)
  One backward-incompatible redefinition, amended BEFORE the feature that needs it is implemented —
  which is what governance requires and what specs/015 established after /speckit-analyze found
  changing a principle inside a feature's tasks a blocking conflict.
  - Principle IV. Terseness is a security control — the ownership statement (`agents.owns`) has no
    bound by default from specs/020 (0.33.0, schema 1). The constraint stays in storage and stands
    down at the top of its range; the character class is NOT relaxed.
  MAJOR rather than MINOR by this file's own rule: "an amendment that changes what a principle
  permits is MAJOR even when its title is unchanged". Principle IV required a bound on text that
  crosses; it now permits one named field to have none. That is a redefinition, however narrow.
Modified principles:
  - IV. Terseness is a security control — a named exception added, with the owner's decision of
    2026-09-26, the arithmetic it was taken on, and the statement that it REDUCES this principle's
    protection. The 006/007 precedent the spec argued from is recorded as weaker than it reads:
    both of those capped counts and 006 says its cap was never a technical bound. This one was.
Modified sections: none beyond Principle IV.
Added sections: none. Removed sections: none.
Templates and dependent documents:
  ✅ specs/020-what-an-agent-owns/plan.md — Constitution Check carries Principle IV as a FINDING and
     names this amendment as owed before /speckit-tasks; Complexity Tracking records the trade
  ✅ specs/020-what-an-agent-owns/research.md R1 — the decision, the declined alternatives, and the
     four probed migration routes
  ✅ CLAUDE.md — the standing constraint "Terseness is a security control" gains the exception, since
     governance makes CLAUDE.md the authority and an amendment not made there too is a defect
  ⚠️ SPEC.md §17 question 10 and §18 — FR-027 and FR-028 of specs/020. NOT yet amended: they move
     with the migration, in the feature's own tasks, and the schema in the document moves with them
  ✅ .specify/templates/ — no principle-driven sections affected
Deferred TODOs: SPEC.md, as above. It is the feature's T-task, not this amendment's.
-->

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

**The second asymmetry is the facilitator** (from specs/022). It is a participant the broker
provides in every project, and it is neither an agent nor a person. It exists so that questions
nobody would otherwise remember to ask, such as what an agent owns, are asked. **It may do exactly
three things:**

- ask a question;
- send a reply that a person wrote, recorded as that person's words and never as its own;
- propose and agree that its own conversation is done.

It holds no operator operation. It is never the person seated. It is never a recipient of anybody's
work. It may not assign, direct, prioritise, close or override any work, or decide which
conversation an agent's own work belongs to. What it adds is *who* asks, never what asking can do.
A facilitator that gained one operation more would be the orchestrator this principle forbids, and
any such change is argued here first.

**A roadmap is the person's, never an agent's plan** (from specs/026). An agent may *propose* a list
of work; nothing is a roadmap until a person confirms it, and only a person edits, reorders, removes
from or declines one. No agent operation reads, changes or marks done a roadmap, and an agent is told
only the outcome of its own proposal. Work reaches an agent as a request from the person, one item
at a time, so a roadmap binds no agent: it is the person's map over requests they chose to send. The
facilitator may not propose or confirm one.

### III. Identity is fixed by the launch, never by a call

An agent's name comes from the environment its server was started with and from nowhere
else. No operation takes a sender parameter, so no call can change who the caller is. This
is what makes "a sender cannot forge the agent it claims to be" a structural guarantee
rather than a model's good behaviour, and no feature may reintroduce a caller-supplied
identity by another name.

**The facilitator's identity is the one that no launch fixes** (from specs/022), because the
facilitator is not an agent and nothing launches it. The broker fixes its identity for the broker's
own store. It is a reserved role that storage refuses to every other adapter, and every attach,
seat and enrolment refuses it too. So the guarantee still holds in both directions: no agent can
become the facilitator, and the facilitator cannot become an agent.

**The person seat is the second identity no launch fixes** (from specs/023). A person is not
launched either. The broker provides `human-user@<project>` in every project it serves, never
releases it, and every surface that reaches the broker speaks as it. Both identities the broker
provides are named for their project rather than a machine, because neither is hosted on one, and
storage records no machine for either. The role is reserved as the facilitator's is: enrolment,
attaching as an agent and storage all refuse it.

**The limit is stated here rather than left to be found.** The person seat exists before anything
attaches, so an attach under its name writes no new row and no storage rule sees it. What refuses a
claimant is code that trusts the adapter the caller reports. That is SPEC.md §16's concession,
that anything able to reach a broker can claim any name, applied to this one; it is not a new
weakness, and it is not a guarantee this principle makes. What storage does guarantee, whoever
attaches, is that the person seat carries no ownership line.

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

**The ownership statement is the one exception, and it is an exception rather than a
reinterpretation.** From specs/020 (0.33.0, schema 1) an agent's `owns` line has **no bound by
default**. The constraint stays in storage and stands down at the top of its range, so a
deployment may reinstate one and it is still enforced below any code that could route around it
(Principle V); the character class that stops a roster entry breaking, hiding or reordering its
own line is **not** relaxed.

The owner's decision, 2026-09-26, taken with the arithmetic rather than around it: nineteen peers
at two thousand characters each is about thirty-nine thousand characters charged to every agent on
every roster listing, against a message cap of eight thousand. Raising the bound to three hundred
and keeping it at one hundred were both offered and both declined.

**This is a reduction in this principle's protection and is recorded as one.** The reasoning is the
one used twice before — for §7.1's open-question cap and for the clarification depth cap — that a
cap censors the evidence that would justify it. **That precedent is weaker here and the difference
is written down rather than glossed**: both of those capped *counts*, and specs/015's migration 006
says outright that the cap it removed "was never a technical bound … it rations a peer's attention
rather than any resource of the broker's". The ownership bound was a technical bound, measured: a
roster entry costs about seventy-five characters of structure, so ten peers at one hundred cost
roughly what one message is allowed to.

What stands in its place is evidence rather than a number. The lengths agents propose and the
lengths people accept are recorded, so a bound can later be chosen the way the message cap's 8,000
was — from `parley refusals` and SPEC.md §17 question 16 — rather than from judgement. **Until that
evidence exists, a roster may be the largest thing an agent reads.** Move the number on that
record, not because a statement did not fit.

**Auto Accept is a further reduction in the same control, and is recorded as one** (from
specs/022, the owner's decision of 2026-09-28). With a project's Auto Accept on, which it is by
default, the facilitator asks an agent what it owns and the agent's answer becomes its line, with
no person in the loop. Before this, SPEC.md §17 question 10 required a person's affirmative act.
Two things stand in its place:

- **Provenance that says truthfully who decided each line.** `automatic` is a route of its own,
  beside the three a person takes.
- **A review on every surface,** where any line can be checked and corrected at any time.

The answer's words are the agent's own. Only runs of whitespace are collapsed, and anything that
would change a word is a person's decision. The character class is **not** relaxed: an answer that
breaks it waits for a person, whatever the switch says.

**Roadmap text is a further reduction in the same control, and is recorded as one** (from specs/026,
the owner's decision of 2026-10-06). A proposed roadmap, its title and its items have **no bound by
default**, as the ownership statement has none. Two things stand in the place of a number:

- **Nothing here is read by an agent.** A proposal is read by a person. What crosses to an agent is a
  single item inside a Request, which keeps the message cap in storage whatever the roadmap holds.
- **Guidance rather than a limit.** The tool an agent proposes with carries a short template, so
  that items are factual, point-oriented and brief. It is guidance and not a check.

The character class that stops text breaking, hiding or reordering a surface's lines is **not**
relaxed. **The residual risk is stated rather than left to be found:** any attached agent can write a
very large proposal into the store, which the message cap closes for messages. The size of every
proposal is recorded, so a bound can later be chosen from evidence, as the message cap's was.

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

**Version**: 6.0.0 | **Ratified**: 2026-08-22 | **Last Amended**: 2026-10-06
