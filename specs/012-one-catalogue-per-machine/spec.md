# Feature Specification: One catalogue per machine

**Feature Branch**: `012-one-catalogue-per-machine`
**Created**: 2026-09-10
**Status**: Draft
**Input**: A design conversation with the owner, from the observation that most agent names in the roster carried no `@machine` suffix.

## The defect this starts from

The owner noticed that most rows in the roster read as a bare role rather than `role@machine`, and asked whether those were agents registered on an older version. They were not. `AgentStanding.Join` synthesises a row for every catalogued role with no registration, so a bare name means **nothing has ever attached under it**.

Tracing why so many were in that state produced the actual defect, and then the design problem underneath it.

**The defect**: `parley enroll apply` writes two files and starts nothing. Its own success message says so — *"Restart your editor in each repository, and check the parley server connected"* — which exists only because enrolment leaves every repository unregistered. The terminal's `Add` does start what it enrolled; the CLI does not. **Two enrolment paths that disagree about whether adding an agent finishes the job.**

**The consequence**: a never-registered agent can receive no message at all. `IsUnroutableAsync` counts an unregistered name as unroutable, and both `Ask` (via `ExchangeRepository`) and `Notify` refuse an unroutable recipient before any wake. Pinned by `NeverRegisteredTests`. Only an operator `start` reaches it.

**The design problem**: the deployment has **two registers of existence** and nothing keeps them in step.

- the **catalogue** — a file on a machine, saying which roles it may start
- the **registry** — a table in a project's store, saying which agents have ever attached

They have independent lifetimes. Replace the store and the catalogue survives; purge an agent and the catalogue entry survives; hand-edit either and nothing notices. The owner's deployment had nineteen catalogue entries against a store that had lost its registrations — a violation produced by no user action at all.

## The owner's rule

> **There shall be no way to add an agent to the system that does not leave that agent in a fully ready and startable state.**

Everything below follows from taking that literally.

## Decisions

Each was settled in conversation. The argument is recorded because the argument is the part that cannot be recovered from the code.

### D1 — Adding an agent must end in a registration

Enrolment is not complete until the agent has attached at least once. The CLI's *"restart your editor in each repository"* step disappears, because it exists only to compensate for this.

**Registration cannot be fabricated.** §12.3 makes it the adapter's own act on attach, and Principle III says identity comes from the launch environment and nowhere else. A surface writing an `agents` row on a repository's behalf is a caller-supplied identity by another name. So "leave it registered" can only mean **start it so that it registers itself**.

**That is cheap, which is what makes this affordable.** `ResidentAgent.Start` and `DispatchTurn` are already separate calls, and only the second spends model tokens. An attach costs a process launch. `BrokerRuntime.AttachAsync` — *"Registers an agent if it is unknown"* — does the rest.

### D2 — The catalogue is machine-scoped and keyed by workspace

One catalogue per machine, recording any agent on that machine and how to start it. Not one per project.

**What this buys is a home for a constraint that has none.** 009 forbids one workspace being catalogued for unattended start in two projects, because two nodes would each launch the same checkout. Its own remark admits the cost:

> *"there is no schema either could hold the constraint in, and the constitution's fifth principle is deviated from here with that argument written down rather than assumed"*

It is a hand-rolled scan across every other project's catalogue plus a machine-wide lock, because two enrolments granting consent simultaneously both looked, saw nothing, and both wrote. Key one machine catalogue by workspace and the violation **cannot be expressed**. One workspace, one entry.

**The trade, stated because it is a real one.** Keying by role today makes it impossible to catalogue one role twice; keying by workspace makes it impossible to catalogue one workspace twice. Neither key prevents both. So a structural guarantee is exchanged for a different structural guarantee, plus a startup check for the one now unguarded — two workspaces claiming the same agent name in one project, which is the copied-repository case and which the enrol help calls out as the one thing Parley makes unforgeable.

### D3 — Project membership lives in the repository

A project is a grouping of catalogue entries that can talk to each other. Where that grouping is recorded matters, and it is **already recorded**: 009 made each `.mcp.json` entry `parley-<project>`, carrying `PARLEY_AGENT` and `PARLEY_PROJECT`.

So the repository declares which projects it answers in and what it is called in each. The catalogue must not also carry membership, or the drift this feature exists to remove is rebuilt one layer down.

A dividend: `.mcp.json` lives inside the folder, so **identity travels with a moved repository** and needs no repair.

### D4 — Consent moves to the store, and there is always a record

Presence in `node.json` is currently the consent to auto-start. That is the wrong home for it:

- the broker cannot see it, so no remote surface can read or change it — turning autostart off for an agent on another machine is **not implementable** today
- the wire carries nothing about it; a node announces role names and no more
- it cannot be reconciled with the registry, so it drifts

**Split capability from consent.** The catalogue says *this machine knows how to launch it*. The store says *and it may be launched unattended here*. **Consent is the store's, and the store wins.**

- **enrolment always writes a policy record**, defaulting to **autostart on** — the owner's reversal of today's opt-in
- **absence of a record is not consent.** An entry with no record is reported and not started

**An earlier draft of this section had the node's join create a record for any entry lacking one, defaulting to on. That was wrong and is corrected here rather than deleted, because the reasoning matters.** The owner's rule — always write a record, default on — was given about *enrolment*, where somebody ran the operation and consent plainly exists. Extending it to entries nobody enrolled hands autostart to a hand-edited file or a restored backup with no human decision, and the first draft then wrote that up as an accepted cost. It was not a decision; it was an over-application.

**D6 supplies the fix for free.** If the node is the only writer, an entry with no policy record did not come through enrolment — a hand-edit, a restored backup, or an enrolment interrupted between the node's write and the broker's. All three get the same treatment: surfaced, inert, resolved by an operator adopting it or re-running enrolment, which is idempotent.

That covers the crash case without provenance tracking. After a restart the node cannot tell an interrupted enrolment from a hand-edit — both are an entry with no record — and it does not need to, because both fail closed.

### D5 — One node per machine, and it is a supervisor

The node has no project identity. It is machine infrastructure.

`ServeCommand` already hosts a node per project in **one process**, so the blast radius is already machine-wide; this makes the structure match. And the justification for per-project nodes collapses on its own, because it rests on the catalogue being per-project:

> *"the manifest names a catalogue per project … So the projects this machine belongs to are exactly the ones that say so."*

A matching refusal is lifted — *"one explicit catalogue offered to several projects, because that is the choice nobody made"* — since under D3 the choice **is** made, by each repository.

**It is a supervisor, not a launcher.** The command stream carries `Joined`, `Start`, `Stop`, `Turn` and `Instruct`; only one of those is launching. `Turn` drives a running agent and `Instruct` carries an operator's words into one, and both exist because §4 gives the node ownership of its agents' standard input. Nothing else can drive an agent, which is why nothing else may start one.

### D6 — The node is the sole writer of the catalogue

Today the catalogue has a reader that runs continuously and writers that appear, write and vanish — `Enrolment`, reached from the CLI and the terminal. That is why every symptom in this feature looks the way it does.

Technically possible today with no structural change: `Parley.Cli` already references `Parley.Setup`, and `Enrolment` is public.

**What it deletes**: the machine-wide gate, and with it 009's documented deviation from Principle V. A lock exists to serialise writers that do not know about each other; a single long-lived owner needs none. Hand-edits also become detectable, because an unexpected change is no longer indistinguishable from a legitimate one.

### D7 — Enrolment becomes an operator operation

`parley enroll` is its own process and cannot call a method on the node. It asks through the broker, down the `Join` stream the node already holds, reporting back the way `Instruct` does.

**The sequence, and the gate is the owner's:**

1. an operator asks to enrol workspace **W** as **A** in project **P**
2. broker → node: write the entry, having validated it — folder exists, `.mcp.json` claims A in P, no other path claims A in P
3. node writes and reports success
4. **gated on step 3**: broker → node: start A; it launches, the adapter attaches, and **the adapter registers A**
5. **gated on step 4**: the broker writes the policy record

Registration arrives at step 4 under A's own identity. Nothing fabricates one.

**The policy record is written last, and that is what makes the state space small.** Written before the start, `entry + policy + no registration` is reachable and needs a reconciliation rule to complete it. Written last, that state cannot occur: a policy record exists only if the whole sequence finished, so it means *this agent exists, has registered, and may autostart* rather than *consent was recorded, hopefully the rest worked*.

What remains is two states, and FR-007a already covers the first: **no record** means enrolment did not complete — inert and reported — and **a record** means it did.

**The hazard this introduces, stated because it fails closed and looks like something else.** Once policy is consent, the natural next step is for the node to refuse to start anything without a record. Applied to step 4 that is a deadlock — no start without policy, no policy without a start — presenting as a broker that refuses to enrol. **Policy governs *unattended* starts.** An operator asking to enrol and start is its own authorisation, exactly as an operator `start` is today, and the two paths must not be unified.

**The window it leaves**: step 4 succeeds and step 5 fails, leaving a registered agent with no consent. Safe — nothing auto-starts — reported, and remedied by re-running enrolment, which is idempotent.

**It belongs on `Operators`, not `Agents`.** Changing what a machine will launch must not be reachable from the tool set an adapter binds. Easy to get wrong, because enrolment feels like something a repository does to itself.

**The failure this shape has already produced once.** 008 exists because the broker gave up before the node did and recorded a falsehood — it waited five minutes, the node waited twenty, and the real answer was refused on arrival. Same shape here: a timeout must mean **"I do not know"**, never "it was not written", and the record must be able to say so.

### D8 — Removal is soft; purge repoints to a sentinel

`AgentStatus.Retired` is already the soft delete: not routed to, not offered in `agents()`, history intact, `Reinstate` brings it back.

`Purge` stays as the authoritative *nuke this from orbit*. To keep the `to_agent` foreign key satisfied, purged conversations repoint to a reserved **agent purged** identity. It must be un-impersonatable — `parley:broker` is precedent, and the gate once caught a forged one.

**Cost**: attribution is lost. Two purged agents become indistinguishable. Acceptable for the operation's stated purpose, recorded so it is not a surprise.

### D9 — A missing workspace is checked, not watched

Nothing watches the filesystem and nothing should. Instead:

- the node verifies at startup that each catalogued workspace exists and holds an `.mcp.json` claiming that project
- an agent that fails is marked **missing**, and re-checked whenever traffic is addressed to it, so a wrong verdict costs one `stat`
- an explicit **it moved** action updates the entry

**CLAUDE.md and `.claude/` are not prerequisites** and must not be checked for. Parley needs exactly one thing in the folder: `.mcp.json` carrying an entry for this project. Checking for the others would mark healthy agents missing.

**Missing must not retire anything automatically.** §14 is explicit that retirement is *"a person's judgement that the agent will not come back"*. An unmounted drive, a rename, a laptop that is shut — all produce a missing folder and none is a deletion. 008 already paid for this exact error, reading a node's stream ending as its process dying. Detection is automatic; the judgement stays the operator's.

**Moving to another machine is not a move.** Identity is `role@machine`, so the destination is a different agent with none of the first one's history. The surface must say so.

## Requirements

- **FR-001**: Adding an agent MUST leave it registered, by starting it so its own adapter attaches.
- **FR-002**: Nothing MAY write an `agents` row on a repository's behalf.
- **FR-003**: Enrolment MUST refuse, and say so, when it cannot reach a broker and a node — writing files and leaving the agent unready is the state D1 forbids.
- **FR-004**: There MUST be exactly one catalogue per machine, keyed by workspace.
- **FR-005**: The catalogue MUST NOT record project membership; that is read from each workspace's `.mcp.json`.
- **FR-006**: The node MUST refuse to announce a project in which two workspaces claim the same agent name, naming both paths.
- **FR-007**: Enrolment MUST write a policy record, defaulting to autostart on.
- **FR-007a**: A catalogue entry with no policy record MUST be reported and MUST NOT be started. Absence of a record is not consent.
- **FR-008**: Consent MUST be the store's answer; a catalogue entry is capability only.
- **FR-009**: There MUST be one node per machine, holding one broker connection per project it serves.
- **FR-010**: The node MUST be the only writer of the catalogue.
- **FR-011**: Enrolment MUST be an operator operation, and MUST NOT appear on the agent-facing service.
- **FR-012**: A timeout awaiting the node's report MUST record "unknown", never "not written".
- **FR-013**: The node MUST verify each catalogued workspace at startup and re-check on addressed traffic.
- **FR-014**: A missing workspace MUST NOT retire an agent automatically.
- **FR-015**: Purge MUST repoint affected conversations to a reserved, un-impersonatable identity.

## What this deletes

Worth counting, because the change earns its cost here rather than in new capability:

- the machine-wide enrolment gate (`Catalogues`)
- 009's documented deviation from Principle V
- the cross-project catalogue scan
- `enroll`'s *"restart your editor in each repository"* step
- the per-project catalogue binding in `projects.json`, which shrinks to broker coordinates
- one of the two registers of existence

## Costs

- **Protocol 0.5.** New node command and report path, and the policy on the wire. Another coordinated upgrade of every machine, coming immediately after 0.4.
- **Schema.** A policy record, and purge's sentinel.
- **Migration.** Per-project catalogues merge into one per machine, and the merge can collide: the same role in two projects with different commands is representable today and is not afterwards.
- **First run changes.** Enrolment now requires a broker and a node, so a cold machine cannot enrol and start things later.
- **Lookup gains a hop.** Keyed by workspace, the node reads each workspace's `.mcp.json` to know which roles it serves — N small files, cheap, and it makes the repository self-describing.

## Open

- **When a catalogue entry disappears**, the proposal is that the policy record becomes the soft-deleted state — marked removed, history intact, and reinstating means re-cataloguing. Not yet confirmed.
- **Whether 0.5 ships alone or batches** with whatever else is pending.
- **Hostname collisions.** The machine half of every identity is `MachineName.Current`. Two machines sharing a hostname produce colliding identities, either able to collect the other's mail. Out of scope here, recorded because it is the one genuine hole in the identity model.

## Not in scope

- Enrolling a fleet from one console. Mechanism stays local, so adding an agent on a machine requires something running on it. A central enrolment surface is a different feature, not an extension of this one.
- Carrying conversation history between machines.
- §17 question 1 — what bounds Notify's volume — which 010 left open and this does not touch.
