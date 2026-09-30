# Research: The person always has a seat

Each decision below was taken from reading the code as it stands on `019-layout` (0.35.3), not from reasoning
about it. File references are to that tree.

## What the code does today

- **A seat is a row the surface creates.** `SeatBook.TakeAsync` opens `Agents.Attach` with adapter
  `parley-console` under a name the person typed, and the broker registers the row on attach
  (`BrokerRuntime.AttachAsync`). The name's machine half is the *surface's* machine (`Seat.Name`,
  `Seat.cs:42`).
- **A seat is held by a stream.** `SeatHold` keeps the attach stream open and re-attaches after a broker restart.
  `agents.connected` counts open streams and is the only presence fact the broker has.
- **Idle is client-side.** `SeatBook.Sweep` drops a hold untouched for `IdleAfter` (20 minutes). The terminal then
  says *"Your seat was released"* and forgets who the person is (`Shell.Tick`, `Shell.cs:2095`). The web UI keeps
  a cookie and asks the person to join again.
- **A message to a seat is always Unreachable.** `RequestAttentionForMail` special-cases only the facilitator.
  For a seat it calls `Ask`, which asks a node to start or give a turn to the seat's role. No node catalogues a
  person, so the answer is `Unreachable` whether the seat is attached or not (`BrokerRuntime.cs:1079-1103`).
- **A seat reads as an agent.** `AgentLiveness.Read` gives it `running`, `startable` or `stranded` like any agent.
  An unattached seat on a machine with a node reads `startable`, the oddity §17 question 2 records.
- **Flags know nothing of seats.** `FlagRepository` has no adapter check. A request waiting on the person reads
  "has not collected it" with the remedy "Ask the agent why".
- **Agents are told nobody may be seated.** `ParleyInstructions.cs:43` and the `parley_result` description
  (`ParleyTools.cs:235`) both say *if nobody is seated, report blocked*.

## R1. The broker provides the seat, the way it provides the facilitator

**Decision**: `BrokerRuntime`'s constructor calls a new `AgentRepository.EnsurePersonAsync(project)` beside
`EnsureFacilitatorAsync`. It finds the row **by role** (`human-user`) and inserts `human-user@<project>`, adapter
`parley-console`, tier `pull`, status `active`, only if none exists.

**Rationale**: The owner's answer A. `EnsureFacilitatorAsync` is the pattern already paid for: idempotent, runs
before anything is served, survives restarts. Looking up by role rather than by name means the row is found
however it was named when it was written, as the facilitator's is by adapter.

**Why by role and not by adapter**: `parley-console` rows from before this change, taken under typed names,
may exist in a developer's store. Looking up by adapter could adopt one of them as the person.

**Alternatives considered**: A surface registering it on first open (answer B) was declined by the owner.
Registering on the first `Attach` as today was rejected because a project nobody has opened would have no person.

## R11. The part after `@` is the project, for both broker-provided participants

**Decision** (the owner's answer to `/speckit-analyze` issue 2): `human-user@<project>` and
`parley-facilitator@<project>`.

- **The broker learns its project name.** `BrokerRuntime.Open` takes the store path alone today. It gains a
  required `ProjectName`, passed by `ServeCommand` (14 call sites). `OpenInMemory`, which only tests use (128
  call sites across 126 files), gains an optional one defaulting to `test`, so the suites do not change for no
  gain.
- **`Facilitation.On` and `EnsureFacilitatorAsync` take the project**, not `MachineName.Current`. Seven test sites
  pass `"test"` already, which is a valid project name, so they read the same.
- **No machine at all** (the owner's second answer, 2026-09-30). `agents.machine` is a generated column with a
  foreign key to `nodes`, so skipping the node row alone would get the agent row refused. The column is NULL for
  the two reserved roles instead, which the key allows, and `tr_agent_declares_its_machine` skips them. A check
  holds the part after `@` to a project's grammar, since the machine check passes on NULL. Probed on SQLite
  3.50.4; data-model.md has the DDL and what was probed. The alternative, a `nodes` row named for the project
  that never connects, was offered and declined.
- **Every valid project name is a valid machine half** (`^[a-z][a-z0-9-]{0,31}$` inside `^[a-z0-9][a-z0-9.-]{0,63}$`),
  so `AgentName` needs no change.
- **`AgentName.Machine` still returns the part after `@`**, because it reads the string. Every C# reader of it
  that could meet one of these two is audited (tasks.md T013a).
- **Nothing routes on it.** `Ask` looks a machine up by the name's second half. The person never reaches `Ask`
  (R5) and the facilitator never has (`RequestAttentionForMail` returns before it). The one code path that reads
  the facilitator's machine is its creation, found by grep.
- **Existing stores keep a machine-named facilitator.** `EnsureFacilitatorAsync` finds the row by adapter and
  reuses it, and renaming a row other tables reference is not something storage does. Nothing has been released;
  `reset.ps1` clears a developer's store.

**Rationale**: the owner's. A person is not hosted on a machine, and neither is the facilitator, so a machine in
either name said something untrue. Naming both for the project gives the two one rule.

## R2. The role is reserved in storage, in both directions

**Decision**: Triggers in `001_initial.sql` mirroring the facilitator's:

- role `human-user` ⇔ adapter `parley-console`, on insert and on update of name or adapter;
- the person seat is never retired and never purged.

**Rationale**: Principle V. The facilitator's four triggers (`001_initial.sql:814-845`) are the template, and the
code-level refusals follow them (`AgentsService.Attach`, `AgentsService.Caller`, `EnrollmentPlan`, `SeatBook`).

**When each lands** (`/speckit-analyze` issue 4): "never retired", "never purged" and "one person" are keyed on
the role and land in Phase 2, because typed seats still carry `parley-console` until Phase 4. The pair tying role
to adapter lands in Phase 7, in SPEC.md and `001_initial.sql` together.

**What storage cannot do** (`/speckit-analyze` issue 1). Registration is `INSERT … ON CONFLICT(name) DO NOTHING`
(`AgentRepository.cs:543`). The person row exists before anything attaches, so an attach under its name writes no
row and fires no trigger. The refusal at attach is code (`AgentsService`) comparing an adapter the caller reports.
Recorded honestly in the spec's *What the reservation does not do*, on the owner's call, rather than adding a
storage rule that would still trust the same self-report.

**What storage does stop, whoever attaches** (the second `/speckit-analyze`, issue 3): an ownership line on either
broker-provided participant. Every attach refreshes `owns` from the launch (`RefreshOwnershipAsync`), so without
the check a claimant could put its own line in every agent's roster as the person's. A check on `agents` refuses
it; a claimant whose launch carries a line fails at that refresh.

**"Never purged" narrows FR-002**, which allowed an operator purge. The broker recreates the row on its next
start, so a purge would remove nothing permanent. It would only cut the person's words out of every transcript
and leave the row back again a moment later. The spec is updated to say so.

**What `parley-console` means afterwards**: only the person seat. A typed-name seat can no longer be inserted.
Rows already in a developer's store are left alone (spec, edge cases). `reset.ps1` clears them.

**Schema number**: stays 1, edited in place. Nothing has been released, which is the ground 020 and 022 edited
the same file on. The first release makes every schema change a migration again.

## R3. Presence stays a fact about a connection, and the seat stops depending on it

**Decision**: A surface holds the attach stream while the person is active, exactly as `SeatHold` does now.
After `IdleAfter` it drops the stream, and the broker counts the person **away**. The next keystroke, click or
page render re-attaches silently. Nothing tells the person their seat was released, because it was not.

**Rationale**: This keeps SPEC.md §4.0's rule that a connection is evidence — here it is evidence of presence
rather than of reachability. It needs no new timer on the broker and no new stored state: *here* is
`connected > 0`, *away* is `connected = 0`, and both are written only when a connection opens and closes.

**Alternatives considered**:

- *Keep the stream open while idle and report activity separately.* Needs a new "touched" message on the wire
  and a stored timestamp on the broker, which is the stale-timestamp design §14 records replacing.
- *Broker-side timer.* The broker cannot see keystrokes.

## R4. Presence has its own two values

**Decision**: `AgentPresence` gains `Here` and `Away`. `AgentLiveness.Read` takes the adapter and returns `Here`
or `Away` for `parley-console`, never `Running`, `Startable` or `Stranded`. The wire's presence enum gains the two
values.

**Rationale**: One implementation. The alternative, each renderer turning `running` into "here" when the adapter
is `parley-console`, is four surfaces each spelling one rule, which is how they drift. §14's table gains two rows.

## R5. A message to the person gets its own wake outcome

**Decision**: `WakeOutcome.WithThePerson` (Core 4, wire `WAKE_OUTCOME_WITH_THE_PERSON = 5`).
`RequestAttentionForMail` returns it for the person seat before claiming attention, after `NudgeAsync` has pushed
the count to any surface holding the stream. `Ask` is never reached for a person.
`WakeForOutstandingWorkAsync` skips the person.

**Rationale**: FR-009. `AlreadyComing` would be closer than `Unreachable` but still false: nothing is coming on
its own, a person has to decide. The two renderers (`ParleyTools.Woken`, `SeatVoice.Waking`) get a sentence
saying the person has it and will answer, and that waiting for them is correct.

**Protocol number**: 0.7 is unreleased. §18's precedent is to record a further 0.7 amendment rather than spend a
number, as 022 did.

## R6. Surfaces take the seat from the registry, not from a name typed or a local machine

**Decision**: `Seat` holds the `AgentName` read from the registry row whose role is `human-user`, not a person
plus `MachineName.Current`. `SeatBook.TakeAsync(project, registry)` finds that row and attaches as it. Both
surfaces take the seat in every project when they open and re-take it on activity after an idle drop.

**Rationale**: FR-001a and FR-017. A surface on a laptop reaching the broker through a tunnel speaks as
`human-user@<project>`, read from the broker, and a project never holds two people. Both surfaces already read `DescribeRegistry`
before seating, so no new call is needed.

**Removed**: the terminal's "seats — withdrawn" group, the "Join as" dialog, `Seat.TryRead`/`Correct`/`Rule` and their name rules, `Leave`, the retired-seat
reinstate path (a person seat cannot be retired), `SeatCookie`, `/Join` and `/Leave`, the web composer's
fallback to an operator instruction when no seat is held, and every "take a seat to…" prompt.

**Kept**: `Instruct` as the operator's own act, behind its own door, which `SurfaceTests` already holds.

**If the registry has no person row** (an old broker, or a broker that could not be read), the surface says so and
does not invent one. SeatBook's rule that an unreadable registry refuses rather than guesses is kept.

## R7. Agents are told who the person is and that waiting for them is correct

**Decision**: `ParleyInstructions` and the `parley_result` description say: the person is the participant whose
role is `human-user`, listed by `parley_agents`; they may be away; a message to them waits; waiting for their
decision is correct and is not a reason to report `blocked`. The *if nobody is seated* clause goes, because a
served project always has one.

**Rationale**: FR-012, FR-013 and SC-006. Instruction text is the only way an agent learns this.

**The envelope is unchanged.** §13 treats every payload as inert data whoever wrote it, and a person's Request is
work like any other. `human:human-user` is the responder the seat reports.

## R8. A flag on the person's own work reads as theirs

**Decision**: A shared `PersonSeat.Is(AgentName)` in Core (role test, like `Facilitation.Is`). The flag renderers
in the CLI, the terminal and the web UI word a flag whose owing participant is the person as *waiting on you*,
naming who asked, with no "ask the agent" remedy. `FlagRepository` is unchanged: whether to flag is the same
question, only the sentence differs.

## R9. The constitution changes first

**Decision**: Principle III gains the person seat beside the facilitator as a second identity the broker fixes
rather than a launch. That changes what the principle permits, so it is **5.0.0**, MAJOR, in its own commit before
any code. The Governance rule and 022's precedent both require it.

## R10. Blast radius, counted

About 53 test files seat somebody under a typed name (`arvind@test` and similar): Client 9, Terminal 37, Web 7,
plus 22 that test seat identity by adapter. The terminal's `Seated` fixture is the root of 37 of them, so it is
converted first, and the rest are sorted into those that describe the removed design (replaced or deleted) and
those that guard a property that must survive the new route (kept). That sort is recorded in `tasks.md`, as 017's
was, because silently deleting a guard is how a property is lost.

The README's "Taking part yourself" and the web UI help text describe joining and are rewritten.
