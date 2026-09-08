# Feature Specification: An instruction survives the turn it asks for

**Feature Branch**: `008-instruction-readback`
**Created**: 2026-09-08
**Status**: Draft
**Input**: An operator instruction must survive the turn it asks for: the broker must stop discarding an agent's reply, and §10.2 must be able to read an instruction back.

## Why this exists

An operator types an instruction to one of their own agents and it becomes a turn (SPEC.md §11.5). The
agent does the work and says what it did. Today, three things can happen to that answer, and two of
them lose it.

**The three defects below were established by probe and by reading the code, not by reasoning about
it.** Each is named with the evidence.

### Defect 1 — the broker gives up before the agent does, and destroys the answer when it arrives

The broker waits five minutes for a turn. The machine running the agent will wait twenty. So any turn
taking longer than five minutes is recorded as *failed, with nothing said* while the agent is still
working — and when the agent finishes and its machine reports what it said, that report is refused,
because the record has already been settled and settlement is first-come.

The agent did the work. The record then asserts two things that are both untrue: that the turn did
not complete, and that the agent said nothing. There is no other copy of either.

Pinned by `tests/Parley.Store.Tests/InstructionSettlingTests`, which passes today and describes the
loss rather than preventing it.

**First-settle-wins is the right rule and is not what is being changed.** A late report overwriting a
settled record would change an operator's history after they had read it. The defect is that the
broker settles a record on the strength of having stopped waiting, which is not a fact about the
turn.

### Defect 2 — patience is punished

The operator's own connection governs both the waiting and the settling that follows it. An operator
who gives up — closes the browser tab, loses the connection — cancels the settling write, so the
record stays open, the agent's real report lands later, and **the answer is kept**. An operator who
waits the full five minutes gets *failed* written first, and **the answer is destroyed**.

The behaviour is exactly inverted from what anyone would predict, and neither operator is told which
of the two happened to them.

### Defect 3 — an answer is recorded that nothing can read

The store keeps the instruction, its outcome, the agent's reply, when it was issued and when it
settled. §10.2 promises this in as many words: *"The reply is the agent's own words and is kept
beside the instruction rather than in the message log."*

There is no operation that reads any of it back. The only route to a reply is the return value of
the single call that blocks for the whole turn, so anything that interrupts that call loses the
answer for ever — even though the broker wrote it down and still has it.

Every part of an asynchronous design is already built and unreachable: the record has an
*unsettled* state, the wire vocabulary has a *pending* outcome, the schema pairs settlement with its
timestamp, and the web console renders a branch reading *"Still pending."* that nothing can ever
resolve.

### The consuming defect, which is what the owner actually hit

Both places the web console issues an instruction hold the browser's request open for the entire
turn, and tie the operation to that request. On a desktop with a stable link this is merely
unpleasant. On anything else the request dies long before a real turn finishes, and by Defect 3 there
is then no way to find out what happened.

**This is not a mobile problem and must not be scoped as one.** It is a defect in the web console on
every screen. It was found while discussing whether a phone could be used, which is how it came to
look like a phone problem; it is not one.

## The decision this feature records

**Which artifact is wrong is a decision, not an assumption.** The specification and the code do not
disagree here — the specification is *incomplete*, and the code faithfully implements the incomplete
thing.

§10.2 promises the reply is kept and gives no operation to retrieve it, which is storage with no
reader. Its own argument for returning an outcome at all is the argument for the readback:

> An operator who cannot see whether an instruction was taken has been given a button and no
> indicator, and the obvious response — issue it again — is the one thing that makes the situation
> worse.

The missing readback produces exactly that failure, only delayed past the request rather than
prevented. **So SPEC.md §10.2 gains an operation, and this is a protocol change**, not a console fix
wearing one.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Find out what an agent said, after the fact (Priority: P1)

An operator instructs an agent, and later — after the browser was closed, after the connection
dropped, from a different machine, or simply the next morning — asks what became of that instruction
and what the agent said. They get the instruction's own words back, what became of it, and the
agent's reply.

**Why this priority**: It is what makes every other part of this feature observable. Without it, a
reply that survives is a reply nobody can reach, and the difference between *the agent is still
working* and *the agent failed* cannot be told from outside.

**Independent Test**: Issue an instruction, let it settle, discard the issuing connection entirely,
and ask the broker what became of that instruction. The outcome and the reply come back.

**Acceptance Scenarios**:

1. **Given** an instruction that was taken and answered, **When** an operator reads it back, **Then**
   they see the outcome *taken* and the agent's reply in full.
2. **Given** an instruction whose turn is still running, **When** an operator reads it back, **Then**
   they see that it is still running and that nothing has been said yet — not that it failed.
3. **Given** an operator who asks for more instructions than the broker will return at once,
   **When** the broker answers, **Then** it returns a bounded page and a way to ask for the next one.
4. **Given** an agent that has been purged, **When** an operator reads back instructions, **Then**
   that agent's instructions are gone with it, because they refer to nothing.

---

### User Story 2 - A slow turn's answer is not thrown away (Priority: P1)

An operator instructs an agent to do something that takes twenty minutes. The agent does it and says
what it did. That answer is recorded, whatever the operator's connection did in the meantime.

**Why this priority**: It is silent data loss on the operation that costs the most — a real turn, of
real work, charged to whoever hosts the agent. Losing the answer means the work is done and
unreportable, and the operator's obvious next move is to issue it again.

**Independent Test**: Arrange a turn that outlasts the broker's willingness to wait, then let it
report. The recorded outcome and reply are the agent's, not the broker's guess.

**Acceptance Scenarios**:

1. **Given** a turn that takes longer than the broker will wait, **When** the broker stops waiting,
   **Then** the record still says the turn is running, and no outcome is written.
2. **Given** that same turn finishing later, **When** the machine reports what the agent said,
   **Then** the report is accepted and the reply is stored.
3. **Given** an operator who waited and one who walked away, **When** both instructions settle,
   **Then** both records are identical — waiting neither preserves nor destroys anything.
4. **Given** an agent whose machine is not connected at all, **When** an operator instructs it,
   **Then** the record settles immediately as undeliverable, because nothing was ever given a turn.

---

### User Story 3 - Issuing an instruction does not tie up the browser (Priority: P2)

An operator issues an instruction from the web console. The page comes back immediately, saying the
instruction is running and where to look for the answer. The operator can leave, close the tab, and
come back to it.

**Why this priority**: It is the defect the owner met. It depends on User Story 1 — a console that
returned immediately without a readback would have made the answer *less* reachable, not more.

**Independent Test**: Issue an instruction whose turn does not finish, and observe that the page
returns promptly and names the instruction, rather than waiting.

**Acceptance Scenarios**:

1. **Given** an instruction whose turn is still running, **When** the operator issues it, **Then**
   the page returns without waiting for the turn and says the instruction is running.
2. **Given** an instruction that has since finished, **When** the operator looks again, **Then** they
   see the outcome and the agent's reply.
3. **Given** an instruction that was declined, **When** the operator looks, **Then** it is shown as a
   decline and not as a failure, because §10.2 requires the two to be told apart.
4. **Given** a reply containing control characters or text shaped like the console's own annotations,
   **When** it is displayed, **Then** it is displayed as the agent's words and cannot forge the
   console's.

---

### User Story 4 - Nothing stays unsettled for ever (Priority: P3)

An operator instructs an agent, and the machine holding it dies mid-turn and never reports. The
record does not stay *running* indefinitely; it settles as a failure, which is what actually
happened.

**Why this priority**: It is the safety net for User Story 2. Removing the broker's habit of settling
records because it stopped waiting removes the one thing that guaranteed a record eventually
settled, so the guarantee has to be put back on a footing that is true.

**Independent Test**: Issue an instruction, remove the machine holding the agent, and observe the
record settle as failed rather than remaining open.

**Acceptance Scenarios**:

1. **Given** an instruction still running, **When** the machine holding that agent disconnects,
   **Then** the instruction settles as failed, because the turn genuinely did not complete.
2. **Given** an instruction still running long past any turn a machine will allow, **When** the
   broker next sweeps, **Then** it settles as failed.
3. **Given** an instruction that settled from a report, **When** a sweep runs afterwards, **Then**
   the record is untouched, because settlement is first-come and a sweep is not a fact about a turn.

---

### Edge Cases

- **A report arrives for an instruction that was already settled.** Refused, silently, and that is
  correct — but it must no longer be reachable by the broker having merely stopped waiting.
- **A report arrives for an instruction nobody issued, or for another deployment's identifier.**
  Refused; an identifier is not an authorisation and the record either exists or does not.
- **Two operators instruct the same agent at once.** Both are recorded; the agent's own machine
  decides what a second concurrent turn means, and that is unchanged by this feature.
- **A reply is longer than the store will hold.** The instruction text is capped; a reply arrives from
  the agent's own machine and the behaviour when it exceeds what can be stored must be decided rather
  than discovered.
- **An operator reads back instructions for an agent name that is not a name.** Refused, rather than
  silently answering a narrower question with a wider result.
- **A turn finishes in under a second.** The operator sees the answer without a second look — a
  readback exists for the slow case and must not make the fast case worse.

## Requirements *(mandatory)*

### Functional Requirements

#### The record must tell the truth

- **FR-001**: The broker MUST NOT record an outcome for an instruction on the grounds that it has
  stopped waiting for one. Ceasing to wait is a fact about the broker, not about the turn.
- **FR-002**: An instruction whose turn is still running MUST be readable as *still running*, and
  MUST be distinguishable from one that failed.
- **FR-003**: When the machine holding an agent reports what became of an instruction, that report
  MUST be recorded, provided the instruction has not already settled.
- **FR-004**: Settlement MUST remain first-come. A later report MUST NOT overwrite a settled record.
- **FR-005**: An instruction that could not be delivered at all — no machine connected, the role not
  one that machine can start — MUST settle immediately as undeliverable, since no turn was begun.
- **FR-006**: The relationship between how long the broker will wait and how long a turn may run
  MUST be enforced mechanically, so the two cannot drift apart again. A test MUST fail if the broker
  would give up on a turn a machine is still permitted to be running.

#### The record must be readable

- **FR-007**: §10.2 MUST gain an operation that reads instructions back, returning for each: which
  agent, the instruction's own words, what became of it, what the agent said, when it was issued and
  when it settled.
- **FR-008**: That operation MUST be narrowable to one agent, and to one instruction.
- **FR-009**: That operation MUST be bounded by the broker regardless of what the caller asks for,
  and MUST offer a way to ask for the next page. §10.2 makes rule 8.5's argument one level up: a
  caller's number is a caller's number.
- **FR-010**: A request narrowed by an agent name that is not a name MUST be refused rather than
  ignored, because dropping the filter answers a narrow question with a wide result and the caller
  cannot tell.
- **FR-011**: The readback MUST include the instruction's own text. It is the operator's words to
  their own agent, on a surface no agent can reach.
- **FR-012**: Issuing an instruction MUST return an identifier by which that instruction can later
  be read back.
- **FR-013**: `instruct` MUST keep working for callers that already use it, returning what became of
  the instruction when the turn finishes within the broker's wait.

#### Nothing stays unsettled

- **FR-014**: An instruction still running MUST settle as failed when the machine holding its agent
  disconnects.
- **FR-015**: An instruction still running for longer than any turn a machine will permit MUST
  settle as failed on a subsequent sweep.
- **FR-016**: A sweep MUST NOT alter an instruction that has already settled.

#### The console must not hold the request

- **FR-017**: Issuing an instruction from the web console MUST return a response without waiting for
  the turn to finish.
- **FR-018**: The console MUST show, for an agent, the instructions recently given to it and what
  became of each.
- **FR-019**: A decline MUST be shown as a decline and not as a failure. §10.2 requires it, and an
  interface that renders refusal as malfunction teaches whoever reads it to work around the agent.
- **FR-020**: An agent's reply MUST be rendered so that it cannot be mistaken for the console's own
  words, on either surface.
- **FR-021**: The terminal surface MUST bind the readback operation as well. Two surfaces that
  implement the operator's view separately disagree about it, which is the drift the repository
  already has a check for.

#### Boundaries that do not move

- **FR-022**: No agent-facing surface MUST gain any ability to issue, read or inspect an
  instruction. §10.2 opens by warning that surfacing an operator operation into an agent's tool set
  breaches §10.1, and `instruct` is named there as the one operation that puts a person's words into
  an agent's turn.
- **FR-023**: The broker MUST remain the only process that opens the store. Both surfaces read
  instructions over the transport like everything else.
- **FR-024**: The obligation on the receiving side (§11.5) is unchanged: an adapter must report what
  became of an instruction and return whatever the agent said, and silence is not an outcome.

#### Retention

- **FR-025**: Instructions MUST be bounded in age on the same terms as message traffic. A table that
  only grows is what §9.4 exists to prevent, and instructions are today the one record with no bound
  at all.

### Key Entities

- **Instruction**: A person's words to one of their own agents, and what became of them. Carries the
  agent it was for, the text, an outcome, the agent's reply when there is one, when it was issued and
  when it settled. Belongs to the agent, and goes when that agent is purged. Not a message: the
  message log records what agents said to each other, and a person's words filed among them would
  make every later reader ask which rows were really traffic.
- **Outcome**: What became of an instruction. *Still running* — given to a machine, not yet answered.
  *Taken* — the agent was given the turn and acted. *Declined* — it was given the turn and would not.
  *Undeliverable* — the machine is not connected and nothing was given at all. *Failed* — the turn
  was attempted and did not complete. The five are different facts and a surface must not merge them.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: An agent's reply to an instruction is recorded and readable for 100% of turns that
  complete, regardless of how long the turn took or what the issuing connection did.
- **SC-002**: An operator who waits and an operator who walks away end with identical records for the
  same instruction. Verified by comparing both.
- **SC-003**: An operator can determine what became of any instruction they issued, from a connection
  that did not issue it, without reading a file on the broker's machine.
- **SC-004**: Issuing an instruction from the console returns a page in under two seconds, whatever
  the turn goes on to do.
- **SC-005**: No instruction remains *still running* longer than the longest turn a machine will
  permit, plus the sweep interval.
- **SC-006**: No agent-facing surface exposes any instruction operation. Checked mechanically, not
  by inspection.
- **SC-007**: Both operator surfaces expose the readback. Checked by the existing drift check, which
  fails the build otherwise.

## Assumptions

- **The five-minute wait and the twenty-minute turn are both current facts of this build**, read from
  its source. The specification names neither, which is why they were free to drift.
- **`instruct` keeps blocking.** It is a versioned contract with existing callers, and a short turn
  answered in one call is genuinely better than one answered in two. What changes is that its
  giving up is reported honestly rather than as a failure.
- **The reply is not capped by this feature.** The instruction text carries the same cap a message
  does; a reply arrives from the operator's own agent, and imposing a limit on what an operator may
  read back from their own agent is a separate decision with its own argument.
- **A sweep is cheap and infrequent.** It is a backstop for a machine that vanished without
  disconnecting, not the primary mechanism, which is the disconnect itself.
- **Retention on instructions is new.** Nothing prunes them today. Bringing them under the existing
  age cap is the smallest change that closes an unbounded table.

## Out of scope

- **Streaming a turn's progress.** An operator sees an instruction as running or settled, not what
  the agent is doing while it runs. Anything finer is a different feature and a much larger one.
- **Cancelling an instruction mid-turn.** Nothing in the protocol can stop a turn once a machine has
  begun it, and inventing that here would be a second design.
- **Notifying anybody that an instruction settled.** Push to a person is a real question and belongs
  with SPEC.md §17 question 1's evidence, not in a defect fix.
- **Any change to what a peer agent can see or do.** Untouched, deliberately.
