# Implementation Plan: One catalogue per machine

**Branch**: `012-one-catalogue-per-machine` | **Date**: 2026-09-10 | **Spec**: [spec.md](./spec.md)

## Summary

Nine decisions, one defect at the bottom of them. The defect is cheap to fix and needs no protocol change; the architecture around it is expensive and needs one. **They ship as two releases, not one.**

## The shape: two releases

**Step 1 — enrolment registers. 0.24.0, no protocol change.**

`Parley.Setup` already opens gRPC channels and uses `Operators.OperatorsClient` directly (`ProjectCommand.cs:656`). The operator `Start` already exists on the wire and already starts a role nothing has registered — `StartOnDemandTests` proves it. So `enroll apply` can write its files, ask the broker to start each agent, and wait for the registration, using only what is there.

That alone:

- satisfies D1 and the owner's rule
- removes *"restart your editor in each repository"*
- makes the two enrolment paths agree
- ends the bare-row state for every newly enrolled agent

**Step 2 — the architecture. 0.25.0, protocol 0.5.**

D2 to D9: the machine catalogue, policy in the store, one node, sole writer, enrolment as an operator operation, the sentinel, the missing-workspace checks. One coordinated upgrade instead of three.

**Why split.** Step 1 is a day and delivers the thing the owner actually hit. Step 2 is a migration, a schema change, a wire change and a restructure of `serve`. Shipping them together means the fix waits for the redesign, and the redesign carries the pressure of a bug fix. 007 split on the same reasoning and its record says the split was what made SC-001 achievable.

## Technical Context

**Language**: C# / .NET 10 · **Storage**: SQLite via Dapper · **Testing**: xUnit v3 on MTP, host `.exe`s run directly
**Constraints**: `Parley.Core` no I/O; no store on `Client`/`Web`/`Top`/`Setup`; broker never holds launch details
**Scale**: ~20 agents on one machine, 2 projects — the deployment that produced this

## Constitution Check

| Principle | Verdict |
|---|---|
| I. The specification is the product | **Step 2 is a protocol change and SPEC.md leads.** §4.1's project illustration, §12's node catalogue and §12.3's spawn-on-demand all describe the current shape and must be rewritten before the code. Step 1 changes no protocol behaviour — it makes a tool finish its own job — so it touches SPEC.md not at all. |
| II. No agent has authority over another | **Held, and worth checking.** Enrolment becomes an operator operation on `Operators`. An agent must not be able to enrol anything, and `SurfaceBoundaryTests` plus the service split enforce that structurally. |
| III. Identity is fixed by the launch | **The load-bearing one.** Nothing here writes an `agents` row. Registration is always the adapter attaching under its own environment; enrolment causes it by starting the process, never by inserting. Any design note that drifts towards "the broker records the agent" is wrong. |
| IV. Terseness is a security control | New fields are an enum and a boolean. Nothing new that can hold a paragraph. |
| V. Invariants live in storage | **This is where the feature earns itself.** 009 records a deviation — *"there is no schema either could hold the constraint in"* — with a machine-wide lock standing in. D2 makes the workspace constraint structural in the file's key, and D6 gives the rest a single serialising owner. The deviation is removed rather than re-argued. |

**No violation to justify.** Principle III is the one that could be breached by accident, and the sequence in D7 is written to make it visible if anyone tries.

## Where each rule lives

| Rule | Where | Step |
|---|---|---|
| enrolment ends in a registration | `EnrollCommand` apply path | 1 |
| enrolment refuses without a broker | `EnrollCommand`, before writing | 1 |
| terminal and CLI agree | `Adding` already starts; CLI joins it | 1 |
| catalogue keyed by workspace | `NodeCatalogue`, new format | 2 |
| membership from the repository | node, reading each workspace's `.mcp.json` | 2 |
| two workspaces, one agent name | node, at join | 2 |
| policy record | new table, `006_agent_policy.sql` | 2 |
| absence of a record is not consent | broker, when the node announces | 2 |
| one node per machine | `ServeCommand`, `NodeCommand` | 2 |
| node is sole writer | `NodeCommand` calling `Enrolment` | 2 |
| enrolment as an operator operation | `Operators` + a `NodeCommand` variant | 2 |
| purge sentinel | `007_purge_sentinel.sql` | 2 |
| missing workspace | node at start-up and on addressed traffic | 2 |

## Step 1 — phases

Each leaves the tree green.

**1.1 — a failing test.** `parley enroll apply` against a live broker leaves the agent registered. It must fail for the right reason: written, not registered.

**1.2 — refuse without a broker.** FR-003. Enrolment that cannot reach one writes nothing and says which project it could not reach. This is the behaviour change most likely to surprise, so it lands first and alone.

**1.3 — start what was enrolled.** After writing, ask the broker to start each agent. Report per repository.

**1.4 — wait for the registration, and be honest about not knowing.** A start that is asked for is not a registration that happened. Poll for the agent to appear, bounded; on timeout say **"started, not seen to register"** rather than either success or failure. 008's lesson, applied before the same mistake is available to make.

**1.5 — the output loses a step.** Remove *"Restart your editor in each repository"*. `DocumentedClaimsTests` guards claims the tool makes about itself; check it.

**1.6 — the terminal's `NotStarted` message.** PR #88 corrects a sentence that says a question will start an unregistered agent. Under 1.3 that path becomes rare rather than usual, but the sentence is still false. **Merge or supersede #88 rather than leaving it open.**

## Step 2 — phases

**2.1 — SPEC.md first.** §4.1, §12 and §12.3 describe a per-project catalogue. The document changes before the code, and §18 gains 0.5.

**2.2 — the policy table.** `006_agent_policy.sql`, keyed by agent name, carrying autostart and the removed/missing state D8 and D9 need.

**A table rather than a column on `agents`, and the reason survived the sequence being flipped.** With policy written last the agent *has* registered by then, so a column would now be writable — but the record must also describe an agent whose catalogue entry has gone (D8's soft delete) and one whose workspace is missing (D9), neither of which is a fact about a live registration. And `agents` is the registry: what an adapter said about itself. Policy is what the deployment decided about it. Two different authorities, and 005's gate already found what happens when one field serves two.

**2.3 — policy on the wire, and both surfaces.** The node learns consent from the broker instead of inferring it from a file's contents. This is the half that makes turning autostart off work for an agent on another machine — impossible today.

**2.4 — the catalogue's new format.** Keyed by workspace, machine-scoped. **The file has no version marker today**, so the reader must tell the shapes apart by inspection — old is `agents` keyed by role, new is keyed by path — and the new format should carry a version so this is the last time.

**2.5 — the migration.** Per-project catalogues merge into one. **The merge can collide**: the same role in two projects with different commands is representable today and is not afterwards. A collision must stop and name both, never pick one.

**2.6 — membership from the repository.** The node reads each workspace's `.mcp.json` to learn which projects it serves and under what name. Cache by mtime; the watch interval is 2 seconds and twenty small files is nothing, but re-reading them all every tick is still silly.

**2.7 — one node.** `ServeCommand` stops looping projects to spin nodes; one node, N broker connections. Lift the refusal on one catalogue serving several projects, and replace its justification.

**2.8 — the node as sole writer.** `NodeCommand` calls `Enrolment`. Remove the machine-wide gate and 009's deviation with it.

**2.9 — enrolment as an operator operation.** The `Operators` call, the `NodeCommand` variant, the report path. `Instruct` is the pattern, twice built.

**2.10 — the checks.** Missing workspace at start-up and on addressed traffic; two workspaces claiming one name; entry with no policy record.

**2.11 — purge's sentinel.** `007_purge_sentinel.sql`, and a reserved name that cannot be impersonated.

**2.12 — the review gate.** Three consecutive clean runs per lens.

## There is no reconciliation, and that is the point

An earlier draft of this plan had the node complete unfinished enrolments on join. **Flipping D7's steps 4 and 5 removed the need**, and removing it is worth more than the mechanism was.

With the policy record written last, `entry + policy + no registration` cannot occur. Two states remain: **no record** — enrolment did not complete, so the entry is inert and reported — and **a record** — it did. Nothing has to decide which kind of incomplete it is looking at, because they are all the same kind.

**Nothing is ever adopted automatically. Enrolment is the only thing that grants consent.** One rule, no exception, and the rule it replaces was the one that would have handed autostart to a hand-edited file.

The cost is that the owner's wiped-database case recovers by re-running enrolment rather than on the node's next join. One command against nineteen repositories, and it is the honest version: the automatic one was indistinguishable from adopting a file nobody wrote.

**The exemption this creates, and it must be explicit.** Once policy is consent, the node will want to refuse to start anything without a record. Applied to D7 step 4 that deadlocks — no start without policy, no policy without a start — and presents as a broker refusing to enrol. Policy governs *unattended* starts; an operator asking to enrol and start is its own authorisation. The two paths must not be unified, and a test should say so before somebody unifies them.

## What must not be assumed

- **`.mcp.json` is the only prerequisite.** Not CLAUDE.md, not `.claude/`. Checking for those marks healthy agents missing.
- **A missing folder is not a deletion.** An unmounted drive, a rename, a laptop that is shut. Detection is automatic; retirement stays a person's judgement (§14). 008 already paid for the inverse.
- **A timeout means "I do not know".** Never "it did not happen". The record must be able to say so.
- **Moving to another machine is not a move.** Identity is `role@machine`; the destination is a different agent with no history.

## Open

- **A catalogue entry disappearing** → the policy record becomes the soft-deleted state. Proposed, unconfirmed.
- **Whether 0.5 batches** with anything else pending.
- **Hostname collisions.** Two machines sharing a hostname produce agents either of which can collect the other's mail. Out of scope, recorded.

## Documentation

`research.md` for the decisions that came from reading rather than reasoning; `contracts/wire.md` for step 2's additions; `quickstart.md` for verifying a fresh deployment end to end — which is now testable in a way it was not, because the owner's machine is at a clean slate.

No `data-model.md` for step 1: nothing is stored.
