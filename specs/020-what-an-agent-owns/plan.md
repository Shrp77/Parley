# Implementation Plan: What an agent owns, asked rather than assumed

**Branch**: `019-layout` (this repository builds features on the working branch) | **Date**: 2026-09-26
**Spec**: [spec.md](./spec.md)
**Version**: 0.33.0 · **Schema**: 12 · **Protocol**: 0.7, unchanged

## Summary

An agent's `owns` line stops being written by a person from memory and starts being **asked of the
agent and argued to agreement**. The ask is an ordinary Request in an ordinary conversation, the answer
binds nothing, a person accepts or edits or declines, and what is accepted is written to that
repository's launch configuration — which remains the only source of the line peers read.

Three things carry the risk, and all three were settled by probe or by the owner rather than by
argument:

1. **The hundred-character bound goes**, which cannot be done by altering anything: the number is a
   literal inside a table-level CHECK on `agents`, `agents` is a parent of eleven tables of which six
   refuse a cascade, and `PRAGMA foreign_keys = OFF` is a no-op inside the migrator's transaction. So
   `agents` is **rebuilt**, outside a transaction, with three triggers taken down and put back. Probed
   end to end. `research.md` R1.
2. **An ask is recognised by the conversation it opens**, never by a marker in a payload, because §13
   promises a receiver that a payload is inert data. `research.md` R2.
3. **Both surfaces write the launch configuration**, reversing a rule held twice — and both refuse when
   the agent's machine is not this one, which is the constraint that makes the reversal defensible and
   which applies to the terminal exactly as much as to the web UI. `research.md` R3.

## Technical Context

**Language/Version**: C# 13 / .NET 10
**Primary Dependencies**: Microsoft.Data.Sqlite + Dapper (store), Grpc.AspNetCore (transport),
Terminal.Gui v2 2.4.17 (terminal), ASP.NET Core Razor Pages (web UI)
**Storage**: SQLite, one store per project, schema 11 → 12 by `012_ownership_is_asked.sql`
**Testing**: xUnit v3 on Microsoft.Testing.Platform; host executables run directly, because `dotnet test`
reports `Zero tests ran` on this machine
**Target Platform**: Windows, Linux, macOS (macOS untested on this branch)
**Project Type**: Multi-project solution — domain, store, broker, transport, server, two surfaces, two tools
**Constraints**: `Parley.Core` no I/O and no package dependencies; `Parley.Client`, `Parley.Terminal`,
`Parley.Web` reach no store transitively; `Parley.Server.Tests` touches no `Broker` and no
`SqliteConnection`; warnings are errors and documentation is enforced
**Scale/Scope**: one new operator operation, one migration, two surfaces, ~19 agents on the owner's machine

## Constitution Check

*GATE: must pass before Phase 0 research. Re-checked after Phase 1 design.*

| Principle | Verdict |
|---|---|
| **I. The specification is the product** | **Pass, and it is the reason this feature is not a patch.** SPEC.md §17 question 10 is closed and says the field is not self-declared. It is **amended** (FR-027), with the cost written into it: the claim narrows from *a person composed it* to *a person was shown a proposal, was free to argue, and agreed*, and anchoring gets worse rather than better. §18's 0.7 entry and the document's schema move with the migration (FR-028). Nothing is silently changed to match code |
| **II. No agent has authority over another** | **Pass.** An agent proposes; a person decides. Two agents may claim one ground and nothing arbitrates (FR-030). No agent is told what another owns |
| **III. Identity is fixed by the launch, never by a call** | **Pass, and checked structurally rather than promised.** No operation accepts an `owns` line from the agent it describes. The new operation records an acceptance and **does not write `agents.owns`**; the registry is still filled from the launch environment on attach. It lives on `Operators`, so an adapter binding `Agents` has nothing to bind by accident |
| **IV. Terseness is a security control** | ⚠️ **FINDING — see below.** |
| **V. Invariants live in storage** | **Pass.** The bound stands down rather than being deleted, so a deployment that reinstates one still has it enforced below any code that could route around it. The character class is untouched, and the migration reproduces it verbatim |
| **Boundaries: what crosses is messages and files, never contexts** | **Pass.** The ask and the answer are messages. Nothing new crosses |
| **Boundaries: agent and operator operations are different surfaces** | **Pass.** One new operation, on §10.2, on `OperatorConsole`, on the `Operators` service. `SurfaceBoundaryTests` will fail if it lands on `Broker` |
| **Boundaries: `Parley.Core` has no I/O** | **Pass.** `OwnershipStatement` keeps its rule and loses its number |

### ⚠️ Principle IV: this feature reduces a control the constitution names explicitly

Principle IV says a roster entry is text that crosses, that it is charged against the working memory of
every agent that receives it, and that **"a bound is part of the design of any new text that crosses,
not a refinement added later"**. `001_initial.sql` records the hundred as *measured*: a roster entry
costs about seventy-five characters of structure, so ten peers at a hundred cost roughly what one
message is allowed to.

**FR-023 removes it.** The owner took that decision on 2026-09-26 with the arithmetic in front of them —
nineteen peers at two thousand characters each is about thirty-nine thousand characters charged to every
agent on every roster listing, against a message cap of eight thousand — and declined the alternative of
raising the bound to three hundred.

**The spec's argument is the 006/007 precedent, and that precedent is weaker than it looks.** Both of
those removed caps on *counts*, and 006 says in as many words that the cap it removed "was never a
technical bound … it rations a peer's attention rather than any resource of the broker's". This one is a
context-poisoning bound, which is the thing Principle IV exists to protect.

**What mitigates it**: FR-024 keeps a bound expressible and storage-enforced, FR-025 makes a refusal
visible, and FR-026 records the lengths so the number can later be chosen from evidence — the standing
§17 question 16 gave the message cap. **They mitigate; they do not eliminate**, and the residual is that
a roster may become the largest thing an agent reads.

**Recommended resolution before implementation begins, not during it.** 015 amended the constitution in
its own amendment *before* any implementation, because `/speckit-analyze` found changing it inside a
feature's tasks a **blocking conflict** — and 015's amendment is the direct precedent, since it reversed
"never files" in the same document. Principle IV should be amended to record that the ownership
statement's bound is off by default, on this evidence and at this cost.

**The bump is MAJOR — 3.0.0 — by the constitution's own rule, not 2.1.0 as a first draft of this plan
said.** Governance: *"An amendment that changes what a principle permits is MAJOR even when its title is
unchanged"*, and MAJOR covers *"a principle or boundary removed or redefined"*. Principle IV today
requires a bound on text that crosses; amended, it permits one field to have none. That is a
redefinition and not an expansion, however narrow the carve-out. 2.0.0 exists precisely because
0.26.0's redefinition of Principle II shipped without a bump.

**The carve-out is narrow on purpose**: the ownership statement specifically, named, with the evidence
and the cost. Weakening Principle IV in general is neither what was decided nor defensible.

**And it lands in three places or it is a defect.** Governance says this file is a projection of
`CLAUDE.md` and `SPEC.md`, which remain the authority, and *"an amendment here that is not also made
there is a defect"*. Doing any of it inside `tasks.md` repeats a conflict this repository has already
hit once.

## Project Structure

### Documentation (this feature)

```text
specs/020-what-an-agent-owns/
├── plan.md              # This file
├── research.md          # Seven decisions, with the probes and what they printed
├── data-model.md        # agents rebuilt; agent_ownership_accepted
├── quickstart.md        # The two claims worth doubting
├── contracts/
│   ├── asking.md        # The ask's exact wording, and why each paragraph is there
│   ├── refusals.md      # Every refusal, and what is deliberately never refused
│   └── operations.md    # The one new operator operation
├── checklists/
│   └── requirements.md  # From /speckit-specify
└── tasks.md             # /speckit-tasks — NOT created here
```

### Source code

```text
src/
├── Parley.Core/
│   └── OwnershipStatement.cs         # rule kept; MaxCharacters becomes a named sentinel
├── Parley.Store/
│   ├── Migrations/012_ownership_is_asked.sql   # the rebuild, outside a transaction
│   ├── SchemaMigrator.cs             # one named migration may opt out of the transaction
│   └── AgentRepository.cs            # agent_ownership_accepted: write, and read for pending
├── Parley.Broker/
│   └── OperatorConsole.cs            # AcceptOwnership (§10.2)
├── Parley.Transport/
│   └── proto/parley.proto            # Operators.AcceptOwnership
├── Parley.Server/
│   └── OperatorsService.cs           # binds it
├── Parley.Client/
│   ├── OwnershipAsk.cs               # the ask's one implementation, both surfaces read it
│   └── OwnershipProposal.cs          # latest agent message in the exchange; pending comparison
├── Parley.Setup/
│   └── (the existing launch-configuration writer)   # gains the owns write + machine check
├── Parley.Terminal/
│   └── (agents pane + the room)       # ask, bulk ask, read, discuss, accept
└── Parley.Web/
    └── (agents pages)                # the same, including the write

tests/
├── Parley.Store.Tests/               # the migration on a store carrying traffic
├── Parley.Broker.Tests/              # AcceptOwnership, and what it refuses
├── Parley.Server.Tests/              # over a real socket; no Broker, no SqliteConnection
├── Parley.Client.Tests/              # the ask's wording, the proposal derivation, pending
├── Parley.Terminal.Tests/            # driven, keys pressed rather than seams called
├── Parley.Web.Tests/                 # the pages, and the machine refusal
└── Parley.Spec.Tests/                # §17 q10's amendment, §18, and the schema executes
```

**Structure Decision**: no new project. The ask's one implementation goes in `Parley.Client`, which
exists for exactly this — what both surfaces need and neither should own — and which `GraphTests`
already holds store-free. The launch-configuration write stays in `Parley.Setup`, which already owns it.

## Complexity Tracking

| Violation | Why needed | Simpler alternative rejected because |
|---|---|---|
| Principle IV's bound removed | FR-023, the owner's decision of 2026-09-26. An agent's two-part answer, with an exclusion half and a named peer, frequently will not fit in a hundred characters | Raising it to 300 was offered with the arithmetic and declined. Keeping 100 was offered and declined. Both are recorded in `research.md` R1 |
| A migration that runs outside its transaction | The only way to move a literal inside a table-level CHECK. Probed: `DROP COLUMN` is refused, `writable_schema` changes nothing while reporting `ok`, a trigger cannot permit what a CHECK refuses, and `foreign_keys = OFF` is a no-op in a transaction | A separate `agent_ownership` table works inside the transaction and was probed working — rejected by the owner because it leaves `agents.owns` standing as a dead column with its CHECK while every reader moves, which is two places one statement could live. Kept as the fallback |
| The web UI writes a launch configuration | The owner's decision of 2026-09-26, and 0.26.0's every-operation-on-every-surface rule | Terminal-only was offered and declined. The hazard it raised — the browser is often not on the repository's machine — is answered by a machine check that **both** surfaces now apply, which the terminal needed anyway |

## What is owed before `/speckit-tasks`

1. **The constitution amendment above.** It is a blocking conflict if left to tasks, on 015's recorded
   experience.
2. **Nothing else.** Every open question in `spec.md` is settled in `research.md`, and the three that
   were the owner's are marked as theirs.
