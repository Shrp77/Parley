# Research: An instruction survives the turn it asks for

Ten decisions, each settled against the code rather than against a recollection of it. Where a
decision rests on a fact about this build, the fact was read and is named.

---

## R1 — The version collision, and it is blocking

**Decision: this feature ships 0.21.0 and does NOT carry the removal of the deprecated `console`
spellings. The deprecation moves to 0.22.0, explicitly and in writing, with the reason recorded in
CLAUDE.md beside the original date.**

§10.2 gaining an operation is a minor bump on this repository's convention — 006's four new
operations moved 0.17 to 0.18 — so this feature lands on 0.21.0, which is the exact version at
which CLAUDE.md promises `parley console`, `--with-console`, `--console` and `--no-console` are
removed.

**The argument for carrying the removal is strong and was nearly taken.** The version number *was*
the promise. A deprecation date that slips the first time it is inconvenient is not a date, and
every later one is read as advisory. That is a real cost and it is being paid here.

**What outweighs it is that the two changes cannot be reverted in halves.** One release that both
changes a versioned wire contract and removes a flag a live scheduled task depends on is a release
whose failure cannot be bisected: an operator seeing a broker that will not start at logon cannot
tell whether the protocol change or the flag removal did it. The wire change is the one that needs
to be revertible on its own, because it is the one this feature exists for.

Two facts about the deployment, both checked:

- **The owner's scheduled task holds `--with-console` right now.** `AutostartPlan` writes
  `--with-web-ui` today, so a reinstall repairs the entry — but the repair happens only if the
  install re-registers autostart, which it does only when the broker's own binary is among what it
  replaces. An upgrade that installs the broker does re-register. An upgrade that does not, does
  not.
- **`InstalledEntry` must keep reading `--console` regardless**, for entries written before 0.19.0.
  So the removal is not "delete every mention"; it is "stop writing and stop accepting, keep
  reading", which is a narrower and more careful change than it sounds and deserves its own
  attention rather than a corner of this one.

**The re-dating is explicit, not silent**, which is the whole of what was asked. CLAUDE.md gains a
line saying the date moved, from what to what, and why — so the next reader sees a decision rather
than a lapse.

**Alternatives considered.** *Carry the removal as a separate final commit on this branch* — good on
revertibility, but a squash-merge repository has one commit on `main` per branch, so the halves are
separable only until the merge, which is exactly when it matters. *Hold this feature at 0.20.x* —
refused: §10.2 is a versioned contract and pretending an added operation is a patch is the drift the
convention exists to prevent.

---

## R2 — What `instruct` returns when the broker stops waiting

**Decision: it returns `pending`, carrying the instruction's identifier, and settles nothing.**

FR-001 forbids recording an outcome because the broker stopped waiting. That leaves the question of
what the caller is told, and the answer already exists in every layer but one:

- `INSTRUCTION_OUTCOME_PENDING` is in the wire enum.
- `InstructionOutcome.Pending` is in the domain.
- The schema pairs `pending` with a null `settled_utc` and refuses a reply on it.
- The web console already renders a branch reading *"Still pending."*

Everything is built. What was missing is that nothing could ever produce the value, so the branch
was dead and the state was unreachable. Producing it is a smaller change than inventing anything.

**§10.2's prose moves from four outcomes to five**, and that is a genuine specification change
rather than a technicality. Today it reads *"there are four outcomes and they are different
facts"* and lists them. The fifth is *pending* — the turn is running and no answer exists yet —
and it is a different fact from all four: notably from *failed*, which §10.2 defines as "the turn
was attempted and did not complete", a claim nobody is entitled to make while it is still being
attempted.

**Is a fifth outcome breaking beyond the minor already taken?** No, and the reason is worth stating
rather than assuming. A client that switches on the outcome and does not know `pending` would fall
to its default branch — which in this repository is `"Still pending."` in the console and a
`_ =>` arm elsewhere. The value was always in the enum, so no wire schema changes shape; what
changes is that it now occurs. The minor is being taken for the new operation regardless.

**`InstructReply` gains `instruction_id`.** An added field on a protobuf message is compatible in
both directions, and FR-012 needs it: an operator told *pending* with no identifier has been told
to come back and not told where to.

**`instruct` keeps blocking**, and keeps its meaning for a short turn. Changing it to return
immediately would break every existing caller for no gain, and a turn that finishes in two seconds
is genuinely better answered in one call.

---

## R3 — How long the broker waits, and where the number lives

**Decision: the broker's wait becomes short (30 seconds) and settles nothing. The invariant FR-006
demands moves from the wait to the sweep, because the wait is no longer capable of destroying
anything. The longest a turn may run becomes `TurnBudget` in `Parley.Core`.**

**`Parley.Server` cannot see `Parley.Headless`, which was checked before anything was designed
around it.** Server references Broker and Transport only. Headless is referenced by `Parley.Cli`
alone. So the twenty-minute turn timeout cannot simply be shared where it sits.

The common ancestor is `Parley.Core`: Broker → Store → Core, and Headless → Store → Core. And there
is precedent sitting in it already — `MessageLimits` is a domain bound that both the broker and
things far from it read. **`TurnBudget` goes there**, `InvocationCommand.DefaultTimeout` is derived
from it, and the sweep threshold is derived from it too.

**The five-minute wait was never the real defect; it was the settling that followed it.** Once
giving up produces `pending` rather than `failed`, the wait can be as short as is comfortable, and
short is better: it bounds how long any caller — a browser most of all — is held. Thirty seconds
answers a quick turn in one call and hands back an identifier for anything longer.

**What FR-006 now enforces** is that the *sweep* must not settle a turn a node may still be
running. That is the surviving version of the same hazard: a threshold shorter than `TurnBudget`
would re-create the exact loss this feature exists to fix, one mechanism further along. The test
compares the two and fails if the sweep could reach a live turn.

**Alternative considered and rejected: raise the wait above the turn budget.** It fixes the loss and
leaves a caller blocked for twenty minutes, which is the console defect restated as a virtue.

---

## R4 — The readback operation's shape

**Decision: one operation, `instructions`, narrowable by agent or by identifier, returning bounded
pages newest first.**

One rather than two, because "get one" and "list some" differ only in a filter, and two operations
would mean two paths to the same rows that can disagree about what a row looks like. The request
carries an optional agent, an optional identifier, a page size and an opaque page token; an
identifier, when given, narrows to exactly that instruction.

**Bounded by the broker regardless of what was asked**, exactly as `ListConversations` is, and for
the reason recorded there: §10.2 makes rule 8.5's argument one level up, and a caller's number is
a caller's number.

**The page token is opaque**, for the reason `ListConversations` already gives: a token whose shape
a client parsed would be a shape the broker could not change. Instructions have a monotonic
identifier, so the token is simpler than the conversation one — which needs an instant *and* an
identifier to break ties — but it stays opaque anyway, because the asymmetry is not worth a second
convention.

**A malformed agent filter is refused, not ignored** (FR-010), matching `ListConversations`
verbatim: dropping a filter answers a narrow question with a wide result, and an empty list invites
a second look where a full one reads as the answer.

**The repository gains `ListAsync`.** `GetAsync` exists and nothing calls it; it stays, because
narrowing by identifier is a genuine single-row read and expressing it as a one-element list would
be worse.

**No schema change is needed, and that is worth saying out loud.** The table already carries the
agent, the text, the outcome, the reply, the issue instant and the settle instant. There is no
migration in this feature. What was missing was never storage.

---

## R5 — What settles an instruction now that giving up does not

**Decision: two mechanisms, one precise and one a backstop.**

**The precise one: a node disconnecting settles its agents' running instructions as failed.**
`BrokerRuntime.ReleaseNodeAsync` is the existing seam — it already removes the node and opens a
connection to record the departure through `NodeRepository.LeaveAsync`. The settle joins that same
unit of work. This is the honest `failed`: the machine that held the turn is gone, so the turn
demonstrably did not complete.

**The backstop: a sweep for instructions older than any permitted turn.** A node can stay connected
while the agent process behind it dies, so the disconnect is not sufficient. The sweep runs from
`Broker.TickAsync`, which already exists and already sweeps for §14's ghosting — a second reaper
would be a second schedule to reason about for a strictly rarer case.

**FR-016 is free.** First-come settlement is enforced by the UPDATE's `WHERE outcome = 'pending'`,
so a sweep cannot alter a settled record however it is written. That is worth a test rather than a
shrug, because it is the property that makes the sweep safe to run at all.

**The sweep threshold must exceed `TurnBudget`** — see R3. It is the same invariant that the old
five-minute wait violated, and it is the one FR-006 now tests.

---

## R6 — The web console

**Decision: issuing an instruction fires it and returns; the answer is read back from the agent's
own page.**

`Act` stops awaiting the turn. It records the instruction, gets the identifier, and returns
immediately — either as *taken* with the reply, when the turn was quick enough to finish inside the
broker's short wait, or as *running*, naming where to look.

**The list of an agent's recent instructions goes on `Pages/Projects/Agent.cshtml`**, which already
exists and already shows one agent. A new page would be a second place to look for a fact about the
same agent.

**Nothing new neutralises text.** Razor encodes `@value` by default, which is what stops a reply
from becoming markup; what FR-020 asks for beyond that is that a reply cannot be mistaken for the
console's *own words*, and that is a layout property — the reply is rendered inside a block
attributed to the agent, not as free-standing prose in the page's voice. This is the same argument
the transcript's column-zero rule makes in the terminal, reached differently because the media
differ.

**The `RequestAborted` token stays** on the call that issues the instruction, and that is now
harmless rather than harmful: the call no longer decides anything, the record is written before
delivery is attempted, and the identifier is returned. An aborted issue leaves a recorded
instruction that the readback can find, which is the opposite of today.

---

## R7 — The terminal

**Decision: the terminal binds the readback in its agents view, and `parley instructions` binds it
on the command line.**

`DriftTests` fails the build when an operation on `BrokerClient` appears in none of three surface
trees — `src/Parley.Web`, `src/Parley.Top`, `src/Parley.Cli` — with an escape hatch that requires
the exemption to be written in prose in a contract, not in the test.

**One binding would satisfy the check; three are wanted for different reasons.** The command line
gets it because `parley instructions` is the same shape as `parley open` and is what somebody
diagnosing a deployment over SSH will reach for. The console gets it because that is where the
defect was met. The terminal gets it because FR-021 exists, and because 005's record already notes
four operations the console binds and the terminal does not — a fifth would make that a pattern
rather than a noted exception.

---

## R8 — Retention

**Decision: instructions get their own delete statement in the pruner, aged on when they were
issued, under the same cap as message traffic.**

**Their own statement rather than joining the messages one**, because the floors differ entirely.
The message delete carries three clauses about exchanges, deduplication windows and undelivered
inboxes, none of which mean anything for an instruction; sharing a statement would mean carrying
predicates that are always true and inviting a later reader to think they matter.

**Aged on the issue instant, not the settle instant**, and this is the trap worth naming: the
schema's CHECK pairs settlement with its timestamp, so a still-running instruction has no settle
instant at all. Ageing on settlement would skip exactly the rows most in need of a bound — the ones
nothing ever settled — which is how an unbounded table hides inside a retention policy.

**A settled instruction is never pruned before an unsettled one of the same age**, which follows
from ageing on issue and is worth a test, because the opposite would let retention destroy an answer
somebody had not read while keeping a row that says nothing.

---

## R9 — What SPEC.md gains

- **§10.2's table gains a row** for the readback, and its prose gains the paragraph explaining why an
  operation that only reads back exists at all — which is the argument already in the section, one
  step further: an operator who cannot see whether an instruction was taken has a button and no
  indicator, and an operator who could see it only while holding a connection open has an indicator
  that goes out when they look away.
- **§10.2's four outcomes become five.** The prose lists them and the list is what changes.
- **§9.2 gains commentary, not DDL.** The table is already right. What it lacks is a sentence saying
  that the unsettled state is a real state a reader will encounter, rather than a transient the
  writer expects to be brief.
- **§9.4 gains instructions**, with the ageing rule and the reason it is measured from issue.
- **§14 gains the sweep**, beside the ghosting it already describes, because both are the same shape:
  a thing left open by something that went away, closed on a bound rather than left for ever.

**Three checks constrain this and were read rather than assumed.** `tests/Parley.Spec.Tests`
executes every schema fragment in the document against SQLite in the order written, so commentary
around DDL must not break the DDL; it resolves every section reference, so a new cross-reference
must point somewhere real; it checks no operation name spans the §10.1/§10.2 boundary, so the new
name must not collide with anything an agent can reach — `instructions` does not, and is
deliberately plural and passive where §10.1's names are verbs.

---

## R10 — Sequencing

**Two shippable steps.**

**Step 1 — the protocol and the broker (0.21.0).** Stop settling on give-up; return `pending` with an
identifier; add the readback through store, broker, wire and client; settle on node disconnect; sweep;
retention; SPEC.md. Bind `parley instructions` so `DriftTests` is satisfied by a real surface rather
than by an exemption.

**Independently verifiable**: the loss probe already committed
(`tests/Parley.Store.Tests/InstructionSettlingTests`) inverts — the case that documents the loss
becomes the case that proves it cannot happen — and a wire test reads an instruction back from a
connection that did not issue it.

**Step 2 — the surfaces (0.21.1).** The console stops blocking and shows an agent's recent
instructions; the terminal binds the readback.

**US1 and US2 are one step, not two, and that is a departure from the spec's priorities.** US2 —
stop destroying replies — has no observable effect without US1, because a reply that survives into a
row nothing can read is indistinguishable from one that was discarded. Shipping US2 alone would be
shipping a change whose only evidence is a unit test. They go together.

**US3 and US4 are separable and US4 goes first within step 1**, because removing the settle-on-give-up
removes the one thing that guaranteed a record eventually settled. The replacement lands in the same
change as the removal or there is an interval where rows can hang for ever.
