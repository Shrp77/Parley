# Research: What an agent owns, asked rather than assumed

**Date**: 2026-09-26. **Settles** the seven things `spec.md` left to the plan, in the order of risk
they were raised in. Three were decided by the owner on 2026-09-26 and are marked as such.

Every decision below that had a probe available was **probed rather than reasoned about**. Migration
006 records that its own answer came from running the failure, and this document records two probes
of its own that were unsound before they were right — both are kept, because a probe that cannot
fail is the thing this repository keeps rediscovering.

---

## R1. The bound goes, and the rebuild is the only way to move it

> **SUPERSEDED IN PART, 2026-09-26, by the owner: there is no migration at all.** Everything below
> about *why* the bound cannot be moved by altering anything still holds and was still what settled
> it — but once the probes had shown what a migration would cost, the owner's answer was *"don't
> worry about migration. Go ahead and make the changes and call it schema #1. Nothing we have is
> worth keeping right now."*
>
> So the eleven migrations were folded into one `001_initial.sql` and the schema number went from 11
> to 1. **That is available exactly once** — nothing has been released, so no store outside a test
> process held anything worth keeping — and it has now been used. There is no path from 11 to 1 and
> deliberately no attempt at one; `reset.ps1` and `reset.sh` clear a developer's stores.
>
> **The collapse was derived rather than hand-merged.** A store was migrated through all eleven and
> its schema read out of `sqlite_master` in creation order, because hand-merging is how a collapse
> quietly loses a trigger — and four tables existed in two shapes across that sequence. Verified by a
> one-time comparison: **41 shared objects, with identical names and identical creation
> order**, plus the one table this feature adds — 20 tables of ours beside SQLite's own
> `sqlite_sequence`, 7 indexes and 14 triggers. Twenty-two differed on raw bytes, twenty-one of those only by
> carriage returns inside the text SQLite stores in `sqlite_master`, and **exactly one differed for
> any other reason: `agents`, by the one number this feature changes.**
>
> What that removes from the plan: the `SchemaMigrator` opt-out, the out-of-transaction migration,
> the rollback hazard, and the trigger choreography. What it costs: **fifty-three tests were deleted**
> — six files whose whole subject was an upgrade path that no longer exists.
>
> The probes below are kept in full, unedited. They are why the collapse was chosen rather than the
> migration, and one of them was unsound before it was right.

**Decision — the owner's, taken 2026-09-26.** `agents.owns` stops being bounded at one hundred
characters. The constraint stays in storage and stands down at `2147483647`, which is how 006 and 007
spell *off*, so a deployment may reinstate a bound and it is still enforced below the code that would
otherwise route around it (Principle V). **The character class of FR-022 is not relaxed and is not
touched.**

### The precedents do not transfer, and that was said before the decision rather than after

006 and 007 both worked because the cap was a *column* — `open_pair_cap`, `clarify_depth_cap` — whose
*range* a CHECK bounded. A trigger could then stand the comparison down at the top of that range
without altering any CHECK. **Neither shape is available here**: the hundred is a literal inside a
table-level CHECK on the data column itself, beside the character-class clause, and SQLite cannot
alter a CHECK.

More importantly, **006 said outright that the cap it removed was never a technical bound** — it
"rations a peer's attention rather than any resource of the broker's". This one is a technical bound.
`001_initial.sql` records the hundred as measured, not chosen: a roster entry costs about seventy-five
characters of structure before the statement, so ten peers at a hundred cost roughly what one message
is allowed to. Constitution Principle IV names a roster entry as text that crosses and says a bound is
part of its design.

**So this is a real reduction in a security control, taken deliberately, and it is recorded as one.**
The cost was put to the owner with the arithmetic — nineteen peers at two thousand characters each is
about thirty-nine thousand characters charged to every agent on every roster listing, against a message
cap of eight thousand — and the alternative of raising the bound to three hundred was offered and
declined. The mitigations are FR-024 (a deployment may reinstate one), FR-025 (a refusal is visible to
the operator rather than silent) and FR-026 (the lengths are recorded, so the number can later be set
from evidence). **They mitigate; they do not eliminate.** See the Constitution Check in `plan.md`,
which treats this as a gate finding rather than an implementation detail.

### The migration: what was probed, and what it printed

All four probes ran against a **real store migrated to schema 11 and then given traffic**, because
006's whole finding was that this rebuild succeeds on an empty fixture and fails on every store that
has ever been used.

**The first pass was unsound and is kept here for the reason it was wrong.** It seeded the store with
a row in `agent_credentials` and reported that 006's blocker does not apply:

```
=== P1 the migrator's real conditions: DROP TABLE agents, in a transaction, foreign_keys ON ===
  DROP TABLE agents -> OK
```

`agent_credentials` is one of the few children of `agents` declared `ON DELETE CASCADE`, so the
implicit DELETE cascaded instead of failing. **The probe had picked the one child that cannot refuse.**
Re-seeded with `invocations`, which references `agents(name)` with no cascade and is what a store that
has ever dispatched a turn holds:

```
=== P1 (corrected) DROP TABLE agents, in a transaction, foreign_keys ON, NON-cascading child ===
  children of agents that cascade vs not:
    agent_credentials      cascades
    invocations            REFUSES
    conversation_participants REFUSES
    instructions           cascades
    exchanges              REFUSES
    messages               REFUSES
    size_refusals          cascades
    result_items           REFUSES
    result_sources         REFUSES
    file_fetches           cascades
    file_notices           cascades
  foreign_keys inside tx = 1
  DROP TABLE agents -> SqliteException 19: SQLite Error 19: 'FOREIGN KEY constraint failed'.
```

**Six of eleven children refuse, and they are the ones a used store has rows in.** 006's finding holds
here with more force than where it was written.

Three further routes were measured and three are closed:

| Route | Result |
|---|---|
| `ALTER TABLE agents DROP COLUMN owns` | `SQLite Error 1: 'error in table agents after drop column: no such column: owns'` — refused, because `owns` appears in a CHECK |
| `PRAGMA foreign_keys = OFF` inside the migrator's transaction | accepted and **a no-op**: `foreign_keys` still reads `1`, and the DROP still fails |
| `PRAGMA writable_schema` + `UPDATE sqlite_master` inside the transaction | the UPDATE is accepted and **changes nothing**. A 400-character statement is still refused at 100, on the same connection *and on a fresh one*. `integrity_check` returns `ok`, so this fails silently — the worst possible shape |
| A trigger standing the CHECK down, as 006/007 did | a trigger can **refuse** but cannot **permit**. A CHECK is not relaxable from a trigger |

**What works, probed end to end:**

```
=== P1 the rebuild, with the bound rewrite CHECKED rather than assumed ===
  the bound clause was found in the live DDL: True
  the bound clause was actually rewritten:    True
  RENAME -> OK
  a 400-character statement  -> OK
  a statement with U+202E    -> SqliteException 19: CHECK constraint failed ...
  an embedded NUL            -> SqliteException 19: CHECK constraint failed ...
  foreign_key_check -> 0 violation(s)
  integrity_check   -> ok
  invocations intact: 1
  triggers restored:  14
```

**The second unsound check is in that transcript's history and is why it says "CHECKED rather than
assumed".** The pass before it reported `bound rewritten in the DDL: True` and then failed to accept
400 characters. The flag was comparing the whole DDL string, which the `CREATE TABLE agents` →
`agents_new` rename had already changed — so it would have said `True` with the bound untouched, and
did. It matches the bound clause specifically now.

### What the rebuild costs, and it is not small

- **`PRAGMA foreign_keys = OFF` is a no-op inside a transaction, so this migration must run outside
  one.** That is a change to `SchemaMigrator`, which today wraps every migration in a transaction and
  bumps `user_version` inside it. **A failure halfway therefore leaves the store with no `agents`
  table and no rollback**, which is a worse failure than any migration in this repository has been
  able to produce. T-tasks must treat the opt-out as a property of one named migration rather than a
  new general capability, and must leave a copy of the store beside it before starting.
- **Three triggers name `agents`, and two are on other tables** — `tr_agent_declares_its_machine` on
  `agents`, `tr_result_is_well_formed` on `messages`, `tr_placer_is_registered` on `shared_files`. The
  rename validates every trigger in the schema, so with these left standing it fails outright:
  `error in trigger tr_result_is_well_formed: no such table: main.agents`. They come down before and
  go back after, and the migration is the third place `tr_result_is_well_formed` is written — 011
  already left it alone deliberately, and this one must reproduce it verbatim.
- `agents` carries `role` and `machine` as `GENERATED ALWAYS … STORED`, so the copy names columns
  explicitly and never `SELECT *`.
- The table is `STRICT`, and the rebuilt one stays `STRICT`.

**Alternative rejected: a separate `agent_ownership` table.** It was probed and it works entirely
inside the existing transaction, with the cap as a column and the purge cascading correctly — no
migrator change and no rollback hazard. It was rejected by the owner because it leaves `agents.owns`
standing as a column nothing writes, carrying its CHECK for ever, while every reader and writer moves
to a second table: two places a statement could live, which is the shape this repository has had to
repair three times. The probe is kept in the ledger because it is the fallback if the rebuild proves
undeliverable.

---

## R2. An ask is recognised by the conversation it opens, never by anything in a payload

**Decision.** The ask opens a conversation whose **identifier** says what it is. A surface finds an
ownership discussion by listing conversations, which is an operator read both surfaces already make;
the current proposal is **the last thing the agent said in that exchange**.

**Nothing is added to a payload and nothing is parsed out of one.** §13 tells a receiver to treat a
payload as inert data, and 017's chip rule is what keeps that true on this repository's own surfaces:
addressing is recorded beside the draft and removed at the send, so what crosses is always prose. A
marker inside the text would be a directive in the one place the protocol promises there is none, and
0.30.3 records what it cost to hold that line the last time.

**A conversation identifier is not payload.** It is metadata the protocol already carries, `Export`
and `ListConversations` already return it, and 003's gate was discharged on exactly those two
operations. 006 settled that a conversation's name *is* its identifier and added no naming scheme;
this adds none either — it derives one and writes it where a person naming a conversation already
writes one.

**"The last thing the agent said" is 0.30.1's rule, reused.** That decision — a row points at the
latest thing the peer said in that exchange rather than the first — was taken because an exchange can
hold a Request and then a Clarify, and what is owed an answer is the last question asked. The same
holds here: an agent that revises after a disagreement has said two statements, and the one a person
accepts is the later.

**The residual risk, recorded rather than closed.** An agent can open a conversation under that
identifier itself and stage a discussion that never happened. It buys nothing: the proposal binds
nothing until a person accepts it (FR-012), the person reads the transcript before accepting, and
what they accept is written from what is on their screen. Worth stating because it is the kind of
thing a later reader will otherwise think was missed.

---

## R3. Both surfaces write, and both refuse when the repository is not on this machine

**Decision — the owner's, taken 2026-09-26.** The spec's assumption survives and **the one-writer rule
is the thing that yields**: the web UI gains the ability to write a launch configuration, through
`Parley.Setup`, which is the component that already writes them. `DriftTests` is satisfied by
construction rather than by exemption.

**This reverses a boundary held twice** — the web UI's naming page records the one-writer rule, and 019
left the web UI unable to name a project six commits ago. It is recorded here as a reversal so the next
reader does not conclude it was lost by accident, and `spec.md` §FR-018 is the authority for it.

**Designing it found the constraint that makes the reversal defensible, and it applies to both
surfaces equally.** A launch configuration is a file in a repository on the machine the agent runs on.
`agents.workspace` is a path on *that* machine, and neither surface is guaranteed to be on it:

- the web UI is reached from a browser that is frequently on a different machine than the broker, and
- `parley terminal` reads a deployment that spans machines, so an agent listed there may be on another.

**So the rule is not "the web UI may not write" but "no surface may write a launch configuration for
an agent whose machine is not this one".** That is one rule for both surfaces, checked the same way —
`MachineName.TryNameHere` against the machine half of the agent's name, which is the comparison
`AgentFleet` already owns. The refusal names the machine the repository is on and what to do there.
Without it the reversal would have shipped a web page that writes a path that does not exist locally,
or worse, writes over an unrelated file at the same path.

**And it restates a defect this repository has already paid for.** 005's review gate found a withdrawal
deleting a launch configuration belonging to a different agent, because the workspace came from the
registry — what the agent said about itself, possibly long out of date. The same re-read applies here:
the name is read back out of the file before it is written to.

---

## R4. The evidence is recorded by the broker, on an operator operation, and only about what an agent wrote

**Decision.** §10.2 gains one operator operation, `AcceptOwnership`. It records that a person accepted a
statement, which conversation it came from, and how long the agent's own proposal was. It **does not
write `owns`** — FR-018 keeps that with the launch configuration, and the registry goes on being
refreshed from the launch environment on attach.

**Why an operation at all.** Nothing else can record it. The surfaces reach no store, `Parley.Setup`
reaches no store, and the broker cannot recognise an ownership ask on its own because the ask is an
ordinary message (R2). An operator operation is the only place all three facts are available at once,
and §10.2 is where a person's decisions about a deployment already live. FR-029 forbids an
*agent-facing* operation touching this and is not engaged.

**What is recorded is what the agent wrote, and nothing else.** 0.29.0's lens-2 finding is directly on
point: the 0.6 retry wrote into `size_refusals` a length the agent never composed, and `size_refusals`
is what §17 question 16 reads. So `proposed_length` is the length of the agent's own message, and where
an operator edits a proposal before accepting it (FR-015) or writes one without asking at all, the
record says which of the three it was rather than attributing a person's characters to a model.

**A proposal nobody accepts records nothing**, and that is a stated limit rather than an oversight: the
only act that reaches the broker is the acceptance. FR-026 is therefore met for accepted lines and
partially for the rest, and the honest reading is written into `data-model.md`. Recording every
proposal would need the broker to know which messages are proposals, which is exactly what R2 refuses
to tell it.

---

## R5. "Accepted but not yet live" is a comparison, held in the store and read over the wire

**Decision.** The accepted statement lives in `agent_ownership_accepted`. What peers read lives in
`agents.owns`. **A surface says the line is pending when those two differ**, which needs no new state
machine and cannot drift, because both halves are facts rather than flags.

This is why the acceptance is an operator operation and not only a file write. The surfaces reach no
store (`GraphTests` walks it), so the comparison is made by the broker and returned on the read both
surfaces already make to list agents.

FR-019's restart is `Operators.Start`, which already exists and which 006 established is permitted to
launch a catalogued role without a registry lookup. Offering it in the same act is a surface concern;
the agent registers itself on attach (§12.3) and the new line arrives with it, at which point the two
halves agree and the pending note goes away on its own.

**A line accepted for an agent that never restarts stays pending for ever, visibly.** That is the
correct behaviour and FR-020 asks for exactly it.

---

## R6. Every ask is sent before anything is waited on

**Decision.** US3's bulk ask sends all N asks first and then waits, and the cost statement (FR-007) is
shown and answered *before* the first one is sent.

0.25.1 settled this for starting agents and the arithmetic is the same: thirty seconds each over
nineteen agents is nine and a half minutes on a serialised work queue, where one wait for the set is
one window however many there are. This surface's queue is still serialised and the reason has not
changed.

**It is N ordinary exchanges, not a new operation** — §5.5 defines the plural ask that way and 0.25.5
records the argument, which is stronger here than where it was made: the costs §5.5 lists for a
one-to-many exchange are all about a single exchange with many parties, and these are genuinely N
separate conversations with N separate agents about N different repositories.

**The screen says what it is doing while it does it**, for 0.25.1's reason: a surface that goes quiet
for a minute is one somebody assumes has hung. An agent that cannot be asked is named and the rest
proceed (FR/AC US3.4).

---

## R7. 0.33.0, schema 12, protocol 0.7 unchanged

**Version**: `0.33.0`. The minor moves because §10.2 gains an operation, which is what 006 did for the
same reason — §10.2 is a versioned contract and a new operation changes what a V1 broker must
implement. `Directory.Build.props` moves in the same change as the behaviour.

**Schema**: `012_ownership_is_asked.sql`, taking the store to 12. It is the first migration in this
repository that runs outside a transaction, and it says so in its own header at length.

**Protocol stays 0.7.** §18 gives a protocol no patch level, and 0.7 **has never been released** —
016 shipped it and nothing has been pushed since. §18's own precedent is 0.6, where the same situation
was recorded rather than spending a number on it. The operator operation and the relaxed bound fold
into 0.7, and §18's entry for 0.7 is amended to say so.

**Ordering.** This follows 019 and cannot precede it: it assumes the worksheet is gone, that enrolment
leaves an agent registered and startable, and that a project is named rather than pathed. 019's review
gate is **not** a prerequisite — but 021 is sequenced after this one in its own header, and the gate is
owed on the branch as a whole before any of it merges.

---

## What this research did not settle

- **macOS.** Nothing here is platform-specific except the launch-configuration write, which is
  `Parley.Setup`'s existing code. No new exposure, and no new coverage either.
- **Whether the constitution should be amended.** R1 reduces a control Principle IV names explicitly.
  015 amended the constitution *before* implementing, in its own amendment, after `/speckit-analyze`
  found changing it inside a feature's tasks a blocking conflict. **The same shape applies here and is
  flagged in `plan.md`'s Constitution Check rather than decided in research.**
- **What bound to reinstate.** That is FR-026's whole point and cannot be answered until the evidence
  exists.
