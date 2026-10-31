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

**The second asymmetry is the facilitator.** It is a participant the broker provides in every
project, and it is neither an agent nor a person. It exists so that questions nobody would
otherwise remember to ask, such as what an agent owns, are asked. **It may do exactly three
things:**

- ask a question;
- send a reply that a person wrote, recorded as that person's words and never as its own;
- propose and agree that its own conversation is done.

It holds no operator operation. It is never the person seated. It is never a recipient of anybody's
work. It may not assign, direct, prioritise, close or override any work, or decide which
conversation an agent's own work belongs to. What it adds is *who* asks, never what asking can do.
A facilitator that gained one operation more would be the orchestrator this principle forbids, and
any such change is argued here first.

**A roadmap is the person's, never an agent's plan.** An agent may *propose* a list of work;
nothing is a roadmap until a person confirms it, and only a person edits, reorders, removes from or
declines one. No agent operation reads, changes or marks done a roadmap, and an agent is told only
the outcome of its own proposal. Work reaches an agent as a request from the person, one item at a
time, so a roadmap binds no agent: it is the person's map over requests they chose to send. The
facilitator may not propose or confirm one.

### III. Identity is fixed by the launch, never by a call

An agent's name comes from the environment its server was started with and from nowhere
else. No operation takes a sender parameter, so no call can change who the caller is. This
is what makes "a sender cannot forge the agent it claims to be" a structural guarantee
rather than a model's good behaviour, and no feature may reintroduce a caller-supplied
identity by another name.

**The facilitator's identity is one that no launch fixes**, because the facilitator is not an agent
and nothing launches it. The broker fixes its identity for the broker's own store. It is a reserved
role that storage refuses to every other adapter, and every attach, seat and enrolment refuses it
too. So the guarantee holds in both directions: no agent can become the facilitator, and the
facilitator cannot become an agent.

**The person seat is the second identity no launch fixes.** A person is not launched either. The
broker provides `human-user@<project>` in every project it serves, never releases it, and every
surface that reaches the broker speaks as it. Both identities the broker provides are named for
their project rather than a machine, because neither is hosted on one, and storage records no
machine for either. The role is reserved as the facilitator's is: enrolment, attaching as an agent
and storage all refuse it.

**The limit is stated here rather than left to be found.** The person seat exists before anything
attaches, so an attach under its name writes no new row and no storage rule sees it. What refuses a
claimant is code that trusts the adapter the caller reports. That is SPEC.md §16's concession,
that anything able to reach a broker can claim any name, applied to this one; it is not a
guarantee this principle makes. What storage does guarantee, whoever attaches, is that the person
seat carries no ownership line.

### IV. Terseness is a security control

The message cap is a context-poisoning defence, not a style preference, and it lives in the
schema so no code path can route around it. Anything that crosses the boundary between
agents — a message, a roster entry, a field returned by an operation — is charged against
the working memory of every agent that receives it. A new field that can hold a paragraph
will eventually hold one, so **a bound is part of the design of any new text that crosses**,
not a refinement added later.

**A file is not text that crosses.** Its bytes are written to disk in the receiving agent's own
repository and are never returned into a model's context by Parley. What an operation returns
*about* files — names, sizes, hashes, lists, gone and tampered notices — is text that crosses, and is
bounded like any other: a name has a maximum length, and a list or a batch of notices has a
maximum count per call.

**The ownership statement is an exception, and it is an exception rather than a
reinterpretation.** An agent's `owns` line has **no bound by default**. The constraint stays in
storage and stands down at the top of its range, so a deployment may reinstate one and it is still
enforced below any code that could route around it (Principle V); the character class that stops a
roster entry breaking, hiding or reordering its own line is **not** relaxed.

**This is a reduction in this principle's protection and is recorded as one.** A cap censors the
evidence that would justify it, so none is set. The lengths agents propose and the lengths people
accept are recorded, so a bound can later be chosen the way the message cap's 8,000 was — from
the size refusals report and SPEC.md §17 question 16 — rather than from judgement. **Until that evidence
exists, a roster may be the largest thing an agent reads.** Move the number on that record, not
because a statement did not fit.

**Auto Accept is a further reduction in the same control, and is recorded as one.** With a
project's Auto Accept on, which it is by default, the facilitator asks an agent what it owns and
the agent's answer becomes its line, with no person in the loop. Two things stand in its place:

- **Provenance that says truthfully who decided each line.** `automatic` is a route of its own,
  beside the three a person takes.
- **A review on every surface,** where any line can be checked and corrected at any time.

The answer's words are the agent's own. Only runs of whitespace are collapsed, and anything that
would change a word is a person's decision. The character class is **not** relaxed: an answer that
breaks it waits for a person, whatever the switch says.

**Roadmap text is a further reduction in the same control, and is recorded as one.** A proposed
roadmap, its title and its items have **no bound by default**, as the ownership statement has none.
Two things stand in the place of a number:

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
safe to commit. **Not built yet**: nothing takes a credential today (SPEC §12); this is the rule for
when something does.

**The protocol is the product; the transport is disposable.** If a platform ships native
cross-session messaging, Parley should run over it rather than compete with it.

**No network bind; tunnel only.** All three gRPC services share one port, so a reachable port is the
control plane with no authentication on the wire (SPEC.md §16). Brokers bind loopback; the
supported remote path is an SSH port-forward.

**A store has one broker.** The store lock is taken before anything else touches the store, and a
second launcher says so and exits.

**Nothing times out.** An exchange that stops moving is flagged to the person; a conversation ends
by consensus of the participants asked, or by a person.

**A person's line to an agent is a new Request, except a reply to a question the person chose to
answer**, which is a Clarify on that question's exchange (SPEC.md §5.6). The agent, never the broker
or a surface, decides a question is answered.

**The plugin ships no MCP server configuration.** A bundled server would hand every repository the
same agent name; the name comes from the launch environment, and the trust model rests on that.

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

**Review is three consecutive clean runs per lens.** Three lenses — correctness and
concurrency; security and data integrity; API surface and maintainability — run after the last
edit rather than alongside it. A finding above low severity resets that lens to zero; the lenses
are independent and all three must reach three. Each run must probe something the previous runs
did not, or it is one run repeated. The running count belongs in `REVIEW-LEDGER.md`, which is
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
first, and the feature's plan then checks against the amended text. Versions follow semantic
versioning: MAJOR for a principle or boundary removed or redefined, MINOR for one added or
materially expanded, PATCH for wording. **An amendment that changes what a principle permits is
MAJOR even when its title is unchanged.**

**Version**: 6.1.1 | **Ratified**: 2026-08-22 | **Last Amended**: 2026-10-29 (says credential references are not built yet)
