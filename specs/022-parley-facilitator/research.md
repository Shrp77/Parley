# Research: Parley as facilitator

Every decision below was made from what the code and the documents actually say, read by four research passes
on 2026-09-28. Citations are to the tree as it stood then (`019-layout`, uncommitted 0.34.3).

## What reading found that the spec did not know

These changed the design. Each is a finding, not a preference.

1. **The constitution forbids amending a principle inside a feature.** Governance (constitution L223-229): "A
   principle changes in its own amendment, never inside a feature's implementation." 022 touches Principle II
   ("no orchestrator… the one asymmetry is the operator, who is a person and not an agent") and Principle III
   ("identity is fixed by the launch, never by a call"). So the amendment is its own change, made **before** any
   implementation — 015's 2.0.0 is the precedent, and `/speckit-analyze` has already once found changing the
   constitution inside a feature's tasks a blocking conflict.
2. **SPEC.md says outright the broker is not a participant.** §10.1 (L2225-2227): "A fourth [message type]
   carrying the broker's questions would make the broker a participant that others address, **which it is
   not**." §5 (L400): "**The broker writes no message on anyone's behalf,** with one exception" — the `closed`
   result. §9.3 (L1932-1937): "`closed` is the broker's, and the broker writes nothing else." Principle 2.10
   (L90-101) names only two starters of a conversation, a person and an agent holding no request. All four are
   amended by 022, and SPEC.md is amended first (Principle I).
3. **§17 question 10 was never amended by 020.** It still says the line is capped at 100 and "not
   self-declared", and its only amendment requires "a recorded positive act, not a default… A drafted line that
   nobody has affirmed must not reach a launch configuration" (L3589-3592). 020's FR-027 was not carried out,
   while §18 (L3990) claims q10 "carries the argument and the cost". 022 amends q10 for 020 and for itself
   together, and says so.
4. **§18 misnames 020's operation**: `accept_ownership (§10.2)` at L3992, where §10.2 has `record_ownership`.
   Fixed in the same edit. No test caught it because the name precedes the section reference.
5. **Every sender is a registry row.** `messages.from_agent`, `to_agent`, `exchanges.requester`, `receiver` and
   `owed_by`, and `conversation_participants.agent`, are all `REFERENCES agents(name)` (001_initial.sql
   :433-435, :469-470, :372). A name must match `^[a-z][a-z0-9-]{0,31}@[a-z0-9][a-z0-9.-]{0,63}$`, so it cannot
   contain `:`. Schema note 002 records refusing to relax that key for the operator.
6. **`Broker.RequestAsync` refuses any `parley:` attribution, including the broker's** (`Broker.cs:122-141`,
   :214). The broker's own `closed` results are written by bypassing `Broker` (`ConversationRepository.cs:500-506`).
7. **The broker cannot tell whether an agent is mid-turn, and never stops one.** Turn state lives only in the
   node's `ResidentAgent._turnLock` (private). The `NodeCommand` oneof has `StartAgent`, `StopAgent`, `GiveTurn`,
   `DeliverInstruction`, and nothing broker-side ever constructs `StopAgent` (proto :490-505; `NodesService.cs`
   :233-346).
8. **A participant that never agrees holds a conversation open for ever.** `TryConcludeAsync` blocks while any
   polled, non-retired participant has not voted done (`ConversationRepository.cs:361-404`).
9. **Most real answers would fail validation.** An ownership line refuses line and paragraph separators and
   control and format characters (`OwnershipStatement.Forbidden`, :195-199), and a model answers in paragraphs.
   Auto Accept with no rule for this would almost never accept anything.
10. **020's ask text makes a promise auto-accept breaks.** `OwnershipAsk.Text` ends: "A person will read your
    answer, may disagree with it, and may argue about it with you before anything is written. Nothing you say
    here changes any configuration by itself." (:78-79).
11. **020 recognises an ownership conversation by its identifier's prefix alone**, and its remarks
    (`OwnershipAsk.cs:122-126`) accept that an agent could open one itself because a person reads before
    anything is accepted. With auto-accept that argument is gone.
12. **Layering fixes where the work can run.** `OwnershipLine` is in `Parley.Setup`; `Parley.Setup` may not reach
    `Parley.Broker` or a store (GraphTests :34); `Parley.Server` references only Broker and Transport. Only
    `Parley.Cli`, the serve host, references both Setup and Server. `SurfaceBoundaryTests` fixes `Broker`'s
    public surface and forbids `OperatorConsole` any Request.
13. **`project.json` cannot hold the switch.** Only `ProjectCommand` writes it, the web UI never may (its naming
    page records the one-writer rule), and principle 2.9 needs the setting on both surfaces.
    `SameDeployment` also ignores any field but name, store and endpoint, so a change would not reach a running
    host without a second read path.
14. **A likely existing defect, recorded for the review gate**: `agent_ownership_accepted.conversation_id`
    references `conversations(id)` without a cascade, and neither conversation destroy nor agent purge clears it.
    Destroying an `owns-` conversation that produced an accepted line may fail at commit. No test covers it.
15. **CLAUDE.md L46 is stale** ("written only by Parley.Cli" — the terminal writes `project.json` through
    `ProjectCommand` too). Corrected in passing.

## Decisions

### R1 — The facilitator is a registry row with a reserved role

**Decision**: one row per project store named `parley-facilitator@<broker machine>`, adapter
`parley-facilitator`, tier `pull`, written by the broker itself when it opens the store and found by its adapter
rather than its name. The role `parley-facilitator` is reserved: storage refuses a row whose role is
`parley-facilitator` unless its adapter is `parley-facilitator`, and refuses that adapter under any other
role (a trigger pair, Principle V). Attach and seat both refuse the role before storage does, with a sentence.
Its own messages carry the reserved attribution `parley:facilitator`; replies a person wrote carry
`parley:person` (R6).

**Rationale**: every sender must be a row (finding 5), and the one namespace that already cannot be claimed is
`parley:` (§11.3 L2569). A name cannot hold `:`, so the reservation moves to the role, where storage can enforce
it — enforcement at the adapter alone is what §11.3 settled for attributions only because a schema check was
impossible there; here it is possible, so it is taken.

**Why not `parley`** (found by `/speckit-analyze`, 2026-09-28): this repository registers as its own agent
under `parley`, and enrolment turns a folder named `Parley` into that role (`EnrollCommand.cs:325`). Reserving it
would refuse the repository that defines the protocol, or put the facilitator's row where a real agent's is. Every
legal role is some folder's name, so no choice makes a clash impossible; `parley-facilitator` makes it unlikely,
and enrolment refuses the reserved role with a sentence and offers another. Every surface shows it as **Parley**.

**Alternatives rejected**: *relaxing the sender keys* (schema note 002 refused exactly this for the operator: it
admits every unregistered sender); *a non-row sender table beside `messages`* (every read path — export,
transcript, inbox, flags — would need a second source, and the operator's `instructions` table shows that cost);
*a name outside the grammar* (the grammar is enforced in storage and on the wire; carving an exception is a
bigger hole than reserving a role).

**§12.3 is amended, not bypassed**: it says a registration is "a claim that an agent exists and has spoken, and
only the agent can make it". The facilitator is not an agent, and the row is the broker's statement that its own
participant exists — the same kind of fact as the `closed` result being the broker's.

### R2 — The facilitator is hidden from every roster and listed apart

**Decision**: `agents()` (§10.1) never returns it; `registry()` (§10.2) returns it flagged as the facilitator so
surfaces show it as **Parley**, never among agents or seats. Retirement, purge and every operator operation on
agents refuse it.

**Rationale**: `ListRoutableAsync` filters only retirement (finding, `AgentRepository.cs:237-259`), so without
this every agent's roster would list Parley as a peer to route work to.

### R3 — Where it runs: three layers, each doing only what it may

**Decision**:
- **`Parley.Broker`** gains a `Facilitator` type, beside `Broker` and `OperatorConsole` rather than on either:
  store-level operations to find who needs asking, open a question, read what came back, record a settled
  question, and vote its conversation done. It writes through the repositories as the `closed` path does
  (finding 6), with its own attribution.
- **`Parley.Server`**'s `BrokerRuntime` runs it under the store gate and wakes the receiver exactly as
  `AgentsService.Request` does (`NudgeAsync` + `RequestAttentionForMail`).
- **`Parley.Cli`**'s serve host adds one per-project loop beside the sweep (`ServeCommand.cs:205-216`,
  `hosted.Tasks.Add(ContainAsync(...))`), which is the only place that can both drive the runtime and write
  `.mcp.json` through `OwnershipLine` (finding 12).

- **`Parley.Core`** holds the two question texts, because the broker sends them and cannot reference
  `Parley.Client`; the surfaces read them from there (found by `/speckit-analyze`).

**Rationale**: the layering is enforced by tests and is the reason `Parley.Mcp` carries no operator code. A new
type on the broker side keeps `SurfaceBoundaryTests` meaningful — it is added to that test as a third surface
with its own fixed list, not slipped onto `Broker`.

**Alternatives rejected**: *putting it on `Broker`* (it would give every attached agent's connection a
facilitator method); *putting it in the terminal* (D1: always on); *a new project* (nothing it needs is missing
from the three that exist).

### R4 — When it asks: on the sweep, not on an event

**Decision**: every facilitation tick (the serve sweep interval, 5 s by default) the loop asks every agent that
is attached now, is not retired, is not a seat or the facilitator, has no accepted line, and **has never been
asked**. A question in any state, *discarded* included, stops automatic asking; only `ask_again` asks again
(FR-008). An earlier draft excepted *discarded*, which re-asked a discarded answer on the next tick —
`/speckit-analyze`'s H1. That one rule is FR-006's three moments — registration, reattachment and broker start
— because each of them is "attached now and never asked".

**Rationale**: there is no attach event to hook (`BrokerRuntime` has no observer; the only point is inside
`AgentsService.Attach` after the write, per session). A poll over state is also what makes a broker restart
harmless: nothing is remembered in memory that the store does not also say.

**The cost of the poll** is one indexed query per tick per project. Asking is one Request per agent, once.

### R5 — Questions are rows of their own

**Decision**: a new table `ownership_questions` (data-model.md) records each question: the agent, its
conversation and exchange, its state (`asked`, `asked back`, `answered`, `waiting`, `accepted`, `discarded`),
the answer as the agent wrote it, and why it is waiting when it is. Only the facilitator's own questions are
rows here, so an agent opening an `owns-` conversation of its own is simply not a question (finding 11).

**Rationale**: "waiting", "asked back" and "discarded" are states nothing else records, and the review needs
them. The exchange alone cannot say an answer is waiting for a person.

**Schema edit in place**: 001_initial.sql is the only migration and nothing has been released (its header and
CLAUDE.md record the collapse to schema 1). The table and triggers are added to 001, as 020 did.

### R6 — A reply a person writes is sent by the facilitator and attributed to the person

**Decision**: when an agent asks back, the question goes to `asked back`, the exchange is flagged as any stalled
exchange is (§6), and the ownership review offers a reply. The reply is sent on the facilitator's exchange (only
the requester may answer a Clarify), with attribution `parley:person` — never `parley:facilitator` — and the
transcript renders it as a person's words.

**Rationale**: §10.2's `instruct` sets the precedent in so many words: "A log whose worth is that it says who
said what stops having any the moment a person's words appear under an agent's name." The same holds for the
facilitator's name.

### R7 — Answers are flattened, never edited

**Decision**: before an answer is judged, runs of whitespace — including line and paragraph separators and tabs
— become one space, and the ends are trimmed. Nothing else changes. If what is left is a valid ownership line it
may be accepted automatically; if not (a format or control character, an empty answer, a bound a deployment has
set), it is held as `waiting` with the reason. The recorded `proposed_length` is the agent's raw message length.

The question itself changes with the setting: with Auto Accept on, it tells the agent that its answer becomes its
line as written and asks for one paragraph; with it off it keeps 020's wording, which is still true then.

**Rationale**: finding 9. Collapsing whitespace changes no word the agent chose, so the words are still the
agent's and `automatic` is truthful; anything that would change a word is a person's decision.

### R8 — A new provenance route: `automatic`

**Decision**: `agent_ownership_accepted.source` admits `automatic` beside `proposed`, `edited` and `composed`,
with the same cross-checks as `proposed` (a conversation is required; a length is recorded). The registry now
returns the source with the accepted line so a surface can show provenance (it is stored today and never read
back).

### R9 — Restarting after the first auto-accepted line is the node's to time

**Decision**: a new node command, `RefreshAgent`, asks the node to restart one agent it started once that agent
has no turn in progress; the node waits on the turn lock (a `WhenIdleAsync` on `ResidentAgent`), then shuts it
down and starts it with a freshly read launch configuration. The broker sends it only when the agent owes no open
exchange and has no attention pending, only for an agent's first automatic line, and only once. An agent the node
did not start (an editor window) is never refreshed; its line stays pending until it restarts on its own.

**Rationale**: finding 7 — only the node knows a turn is in progress, and SPEC §11.1's list of what ends a
resident agent ("Three things end a resident agent, and nothing else may", L2495-2505) gains a fourth, argued
there: the node told to refresh an agent whose first ownership line was just accepted, at a moment it has learned
almost nothing.

### R10 — The Auto Accept switch lives in the project's store

**Decision**: a single-row table `facilitation` holds `auto_accept` (default 1). Two new operator operations read
and set it; the loop reads it each tick.

**Rationale**: finding 13. The store is the project's own, so this is still "per project" as clarified, and it is
the only place both surfaces can reach and the broker can read without a second file watcher.

### R11 — Operator operations added to §10.2

**Decision**: `facilitation()` (read the switch and every question with its state), `set_auto_accept(on)`,
`ask_again(agent)`, `reply_to_asked_back(agent, text)` and `discard_answer(agent)`. Accepting a waiting answer is
020's `record_ownership` unchanged, preceded by the surface writing the file as it does today. None is an agent
operation, and none contains "own" in an Agents-service name (`AgentsCannotSetTheirOwnLineTests`).

### R12 — Protocol version

**Decision**: recorded as a further `#### Amended` entry under 0.7 in §18 — spec FR-021 as corrected. 0.7 has
never been released.

### R13 — A facilitator exchange never chooses a conversation

**Decision**: the broker assigns a Request whose sender names no conversation to the conversation the sender
already holds a request in (014). Exchanges the facilitator opened are left out of that choice.

**Rationale**: found by `/speckit-analyze`, 2026-09-28. Without this, an open ownership question — and one in
`asked_back` can stay open indefinitely — pulls the agent's own work into its `owns-` conversation, or forces it
to name a conversation it would not otherwise have to. That is the facilitator steering work, which FR-003 and
Principle II forbid.

**Also found there**: nothing stopped an agent addressing the facilitator by name, since leaving it off the
roster does not stop an agent naming it. `Broker` admission refuses a Request to it and a Clarify on an exchange
it did not open (T053).

## Constitution

**Amendment to 4.0.0, MAJOR, before implementation**, because it changes what two principles permit:

- **Principle II** gains a second asymmetry beside the operator: the facilitator, which is neither agent nor
  person, may ask and has no authority — and the amendment says what it may never do.
- **Principle III** gains the one identity not fixed by a launch: the facilitator's, fixed by the broker for its
  own store and unclaimable by any launch or call.
- **Principle IV** records Auto Accept as a further reduction in the ownership control, with what replaces it.

This is task T001 and blocks everything after it.
