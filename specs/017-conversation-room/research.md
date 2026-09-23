# Research: A conversation is a room

Ten decisions. Each was reached by reading what exists rather than by choosing what would be pleasant, and the
ones that changed the design are marked.

## R1 — Where the pair state comes from, and it needs no wire change

**Decision**: join two operator reads the surface already makes. `Export` returns the conversation's messages and
an `ExchangeSnapshot` per exchange — identifier, state, opened, closed, outcome. `DescribeWaiting` returns the
wait-for graph as edges of `waiter`, `waited_on`, `exchange_id`. Filter the edges to the exchange identifiers the
export listed, and the pair state for every open exchange in this conversation falls out:

| edge | meaning | act when typing to them |
|---|---|---|
| `waiter` is me, `waited_on` is them | I asked and am waiting | **Clarify** |
| `waiter` is them, `waited_on` is me | they asked and I hold it | **Result**, or a Clarify back (FR-008) |
| no edge between us | nothing open | **Request** |

**Why it was in doubt.** The first attempt read `ExchangeSnapshot` alone and found it carries no parties at all —
no requester, no receiver, no `owed_by`. Deriving them from the messages is possible (the first message of an
exchange is its Request, so its sender is the requester) but it would reconstruct the broker's turn logic in a
client, which is the second-opinion problem this repository rejects everywhere else: the disagreement always
favours the copy that lets something through.

**Alternatives rejected**: adding `requester`, `receiver` and `owed_by` to `ExchangeSnapshot` — a wire change, and
§18 would have to decide whether a field a newer client reads and an older broker leaves empty is observable by a
second implementation. Not worth spending a protocol version on a field the wait-for graph already carries.

**What it costs**: one extra read per conversation open and per follow tick. `DescribeWaiting` is
deployment-wide, so it is filtered locally; the terminal is the operator surface and §10.2 already grants it that
view.

## R2 — The derivation lives in `Parley.Client`, not in the terminal

**Decision**: a type in `Parley.Client` takes the export, the waiting edges and the seat, and answers what typing
to each recipient would do. The terminal renders it; the console will call the same thing.

**Why**: SC-002 says every message is recorded as the act the preview named. Two surfaces deriving that
separately is how they come to disagree about it, which is the exact fault `Parley.Client` was created for — 004's
plan records that two surfaces implementing seating and transcripts separately "would disagree about who was in a
conversation, which SC-003 forbids and which this repository shipped twice in one week".

## R3 — Commands are declarations, and they live beside the derivation

**Decision**: one record per command in `Parley.Client` — identifier, what parameters it takes and of what kind,
whether it changes the draft or acts, and the door it must pass through if it is destructive (FR-027). The input
line parses, completes, refuses and lists from the declarations (FR-017).

**Why here**: the console gets commands too, and a command table built twice is two tables that diverge. The
*rendering* of the completion list is Terminal.Gui's and stays in the terminal.

## R4 — The completion list is one mechanism over two candidate sets

**Decision**: the filtering and the state — what is open, what is selected, what matches — is a type in
`Parley.Client` over a candidate set. `/` supplies the commands, `@` supplies the agents (FR-037). Only the
drawing is the terminal's.

**Why**: FR-037 asks for one mechanism, and the two sigils differ only in what they offer. Built twice they would
diverge in the same input line, which is the worst place for two behaviours.

## R5 — The list is derived from the text, never held as state

**Decision**: what the list shows is a function of the draft text and the candidate set. There is no "completing"
flag.

**Why**: FR-034, and it is the only way deleting back into a match can bring the list back. The same discipline
the waiting count follows — 0.29.1's `SeatHold` remark records a cached count that only ever climbed because a
notice arrives when something lands and never when it is collected. A remembered "dismissed" flag would be that
fault in a new place.

## R6 — Prefix decides the list; an exact identifier decides the send

**Decision**: two different comparisons, kept apart. The list narrows on prefix; what makes a draft a command is
an exact match of the text between `/` and the first space (FR-035).

**Why**: conflating them means `/ad` is a command because the list is offering `add`, which is the typo that
FR-021 then sends to everybody. Keeping them apart is what makes FR-033's near-miss note expressible at all.

## R7 — Scrolling to a message needs an anchor the transcript already almost has

**Decision**: `Reading.Lines` gains the message identifier on each line it produces, and `TranscriptRegion` gains
a way to put a given identifier on screen with its neighbours visible.

**Why**: FR-005. The lines already carry a `LineKind` and a colour slot, so the shape exists; what is missing is
which message a line came from. Without it the outstanding list can only scroll by counting, which breaks the
moment wrapping changes.

## R8 — The unseated operator is offered a seat, following 0.28.2

**Decision**: with no seat the input line is present and says a request is sent as somebody, asks for a name, and
opens the draft once seated — exactly what 0.28.2 chose for the conversations menu.

**Why**: that entry records the owner reaching for Instruct because starting a conversation was hidden without a
seat, and the fix was to offer it either way rather than hide it. Hiding the line here would rebuild the same
trap. FR-016 keeps it safe: nothing sends under a seat nobody holds.

## R9 — Sending is N independent acts, and partial failure is reported per recipient

**Decision**: one draft to N recipients is N sends, each reported. No attempt to make them atomic.

**Why**: §5.5 already defines the plural ask as N ordinary exchanges, and `SeatVoice.RequestEveryoneAsync` already
reports per recipient and groups refusals by reason. Atomicity would be a new protocol concept, and the honest
report is what 0.25.1 built when a broadcast partly failed.

## R10 — Which of the 723 terminal tests are guards and which described the old design

**Decision**: read every test that drives the conversation view and sort it into one of three piles before
changing any of them.

- **Guards to carry through the new route.** Anything asserting a property rather than a layout: the column-zero
  rule that stops a payload forging a header (`ColumnZeroTests`), the escaping of agent-written text
  (`VerbatimTests`), the seat checks at the send (`SeatUnderneathTests`), the size cap at the boundary
  (`MessageCapTests`), the refill race (`RefillRaceTests`), underscores surviving in labels, the client
  separation, and `SurfaceTests`' one-file-per-destructive-operation rule.
- **Descriptions of the design being replaced.** Tests naming the four participation keys as the way to act, or
  asserting the conversation view has exactly one pane. These are rewritten to the new route or deleted with the
  reason recorded — 006 did exactly this when it replaced 005's four regions, and its note is the precedent:
  `All_four_regions_are_on_screen_at_once` "was the second kind and was replaced rather than carried".
- **Tests of things this feature does not touch** — projects, naming, enrolment, files, withdrawal. Untouched.

**Why it is a research decision and not a task**: getting this wrong in either direction is expensive. Carrying a
description forward means fighting a test that describes a design nobody wants; deleting a guard means losing a
property that was found the hard way, and several of these were found by probe rather than by inspection.

## R11 — The room is built around the existing transcript, not in place of it

**Decision**: the Conversation view gains two regions — the narrow column and the input line — around the
`TranscriptRegion` that is already there. The transcript itself is not rewritten. The four participation keys stay
reachable while the line is proven, and are retired in their own change once it is.

**Why, and this reverses the spec's word "replaces"** (FR-038 is unaffected; the views above a conversation were
never in question). Forty-nine test files in `tests/Parley.Terminal.Tests` reference the conversation view.
Reading them, almost all are about the *transcript* rather than about the view's arrangement: the column-zero rule
that stops a payload forging a header, agent-written text escaped on the way to the screen, wrapping, reflow,
copying, the refill race, colour surviving being stripped. Every one of those is a property found the hard way —
several by probe rather than by inspection — and a rewrite that reimplemented the transcript would put all of them
at risk at once to deliver a layout change.

Keeping the transcript means those guards keep passing **by construction rather than by care**, which is the only
form of preservation worth anything at this scale. What has to change is the handful of tests that assert the
arrangement — the layout ladder, the narrow-terminal rule, resize, and which panes exist — and those are the ones
that genuinely describe the design being replaced.

**What it costs**: two routes to the same acts for one release, which FR-040 already governs — they must perform
the same act behind the same door. That is a real cost and it is bounded: the keys go when the line has been
used.

**The alternative rejected**: replacing the view wholesale in one change. It would have been a multi-hour rewrite
whose largest risk, per this plan's own risk table, was quietly losing a guard — and it would have delivered the
same feature.
