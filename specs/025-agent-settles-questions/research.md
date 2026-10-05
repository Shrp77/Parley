# Research: The person talks; the agent decides what it settled

Each decision below was taken by reading the code it changes, not by reasoning about it. File and line references are
to `019-layout` at `7b9cf96`.

## R1. Where the guess is made, and that it is the only place

**Decision**: The guess is `Composing.Plan` (`src/Parley.Client/Seating/Composing.cs`), fed by `Pairing.From`
(`src/Parley.Client/Seating/Pairing.cs:88`). `Plan` turns each recipient's pair state into an act: `Answer` where the peer
asked and the person owes it, `Clarify` where the person asked and the peer asked back, `Ask` otherwise. `Pairing.From`
keeps **one** exchange per peer, picked with `FirstOrDefault` over the wait-for graph's edges — whose order is the
store's, not anybody's choice. On the live store on 2026-10-05 the edges came back 65, 78, 89, 88.

`Plan` now gives every recipient `Act.Ask`. `Pairing.From` stops choosing an exchange for the box: it keeps every owed
exchange per peer, which the room needs (R6) and the box no longer reads.

**Rationale**: The box is the only path that chooses on the person's behalf. The other paths name the exchange because
the person named it:

- the web UI's Inbox (`Pages/Inbox.cshtml.cs`, `OnPostResultAsync` and `OnPostClarifyAsync`) answers the request the
  person picked on that page;
- the terminal's delivery dialogs (`Shell.cs` near 4619 and 4969) pick a question first and then take the words.

Those are the person's choice, not a guess, so the owner's rule does not remove them (spec, Assumptions).

**Alternatives considered**: Choosing the newest owed exchange, or the one under the transcript cursor — offered to the
owner and declined: *"Let the agent determine what the message relates to."*

## R2. The web UI's box already sends a Request

**Decision**: No change to what the web conversation page sends. `Detail.cshtml.cs` sends `SeatVoice.RequestAsync` for
every target already. The web change is the *waiting on you* list (R7).

**Rationale**: Read, not assumed: the page never derived an act. Only the terminal box did, through `Composing`.

## R3. Settling is a new outcome on the exchange, not a message

**Decision**: An exchange the agent settles is closed with a new rolled-up outcome, `settled`, and a new column,
`exchanges.settled_by`, naming the exchange whose request settled it. **No message is written on the settled
exchange.** What the agent said is its Result or Clarify on the person's request, and that is where the transcript shows
it.

**Rationale**: SPEC.md §5.3 gives a Result to the receiver alone, and a Result is the receiver's words about the work.
A settled question has no receiver's words: the person never answered it there. Writing a message on it, under either
party's name, would put words in a transcript that nobody said there — the thing §13 and principle 2.10 exist to stop.
The outcome and the citation are the honest record: *closed, by the agent that asked, because of that exchange*.

`exchanges.outcome` is the roll-up §9.2 keeps after retention prunes text (§9.4), so `settled` survives pruning as the
spec's edge case requires, and `settled_by` survives with it.

**Alternatives considered**:

- **A Result written by the broker for the person** (as a person's close writes `closed`). Rejected: the person did not
  close it and did not answer it, and the record would say they did.
- **A message of a new type, `settle`, from the agent on the settled exchange.** Rejected: a fourth message type is a
  bigger protocol change than the act needs, and the agent's reasoning already lives in its reply to the person.

## R4. Turning a clarification back is the second half, and needs no new outcome

**Decision**: Where the person asked the agent and the agent asked back, the exchange is open with `owed_by` the person.
Naming it in `settles` sets `owed_by` back to the agent — what the person's own reply there (a Clarify, §5.2) would
have done — and changes nothing else: no message, no depth change (§5.2: depth increments only when the receiver sends).
The agent then answers it with an ordinary Result.

**Rationale**: The person's line is always a request now (R1), so the person never sends that reply. Without this the
person's own request would be stranded with the person owing a move they can no longer make from the box. The agent can
only hand the turn to *itself* this way, so it gains work, never authority.

## R5. Validation happens in one transaction with the reply, and the whole reply is refused on any bad name

**Decision**: `Broker.SayAsync` (`src/Parley.Broker/Broker.cs:457`) already reads the exchange, applies the transition
and appends the message in one `WriteScope`. Settling joins that scope: each named exchange is read, checked and moved
in the same transaction, and any refusal returns before the message is appended, so nothing is stored. The refusal is a
new `TransitionError`, `CannotSettle`, carrying each refused exchange and a reason.

The checks, in the order a refusal names them:

1. The reply is a Result or Clarify **from the receiver of an exchange the person requested** (the person's line). Any
   other reply carrying `settles` is refused whole.
2. Each named exchange is in the **same conversation**.
3. It is **open**.
4. It is **between the same two parties**: this agent and the person.
5. **The person owes the next move** on it (`owed_by` is the person).

A name repeated in one reply is settled once (spec, edge cases). Two replies racing to settle one exchange are
serialised by the write lock; the second finds it closed and is refused whole.

**Rationale**: Refusing the whole reply is the protocol's habit (§7.3: a refusal is actionable, and nothing half-done is
stored) and the edge case in the spec. Settling inside the scope is what makes "first closes it, second is refused" true
rather than likely.

## R6. Storage holds the rule, as principle V asks

**Decision**: In schema 1 (edited in place; unreleased):

- `exchanges.outcome` admits `settled`.
- `exchanges.settled_by INTEGER REFERENCES exchanges(id)`, with `CHECK ((outcome = 'settled') = (settled_by IS NOT
  NULL))` and `CHECK (settled_by IS NULL OR settled_by <> id)`.
- A trigger `tr_settled_by_its_requester`, before an update that sets `outcome = 'settled'`, refuses unless the
  settling exchange is in the same conversation, its requester is this exchange's receiver, its receiver is this
  exchange's requester, and this exchange's receiver is the person (the row whose adapter is `parley-console`, which
  storage already ties to the reserved role in both directions since 0.38.3). The same trigger refuses a settled
  exchange whose requester is the person — the person's own request is never `settled`; it is turned back (R4).

**Rationale**: Every other close is held in storage (`tr_result_is_well_formed`, the outcome CHECKs). A code path that
could write `settled` past the broker's checks is exactly what principle V says storage must refuse. The person is
recognised by adapter because that is how storage already recognises the person.

**Alternatives considered**: A separate `settlements` table. Rejected: one row per exchange, at most one settler, read
whenever the exchange is read — it is a column of the exchange.

## R7. The room's list, and the web UI's

**Decision (terminal)**: `Shell.Outstanding` (`Shell.cs:4793`) makes one `OutstandingItem` per peer from the single
exchange `Pairing` kept, with the text `"<peer> — waiting for your reply"`. It becomes one row per peer with a count
(`"<peer>  3"`), built from every exchange the person owes that peer in this conversation. Enter on the row opens a
`Prompting.Choose` list: one entry per question, oldest first, `HH:mm  <the start of what was asked>`. Choosing one moves
the transcript to that message with the mechanism the room already uses (`TranscriptRegion`'s "The room's waiting on
you list activates this", near line 594), which already keeps two rows of context above. A question settled since the
list was drawn is found missing from the transcript's owners and says so rather than moving (spec US3 scenario 4).

The person's own clarification owed (R4) counts as a question waiting on them: it is.

**Decision (web)**: The conversation page gains a *Waiting on you* section above the transcript, grouped by agent with
the count, each question a link to the message's existing anchor. Derived from the same `Pairing` output, so the
counts agree (SC-005).

**Rationale**: The owner asked for it in so many words. One derivation for both surfaces is what makes SC-005 true by
construction.

## R8. What the agent is told, and how much it costs

**Decision**: A `DeliveredMessage` that is a Request **from the person** carries `settleable`: up to **20** of the
agent's exchanges with the person in that conversation where the person owes the next move, oldest first, each with its
exchange id, when it was opened, and the first **160** characters of the last thing the agent said in it. When there are
more, a count of how many were left out. `parley_inbox` shows them beside the message as `your_open_questions_to_them`.

**Rationale**: Principle IV: a bound is part of the design of any new text that crosses. Twenty entries at about two
hundred characters each is about four thousand, half a message's cap, charged only on a person's request to an agent
that has asked them things. The text is the agent's own words, returned to it, so it is not a peer's text and needs no
envelope — but it is still bounded.

## R9. The registry's open_outside for the person

**Decision**: `BrokerRuntime.OpenOutside(agent)` decides from attachments that were not started by Parley. The person's
attachment (a surface) is never started by Parley and so always reads open outside. The registry's fill
(`DescribeRegistry`) skips the person and the facilitator: open outside applies only to an agent a node could start
(§11.7).

## R10. Protocol 0.8, and nothing older is kept

**Decision**: `ProtocolVersion.Current` moves from `0.7` to `0.8`. §18 gains the entry. A 0.7 adapter is refused at
attach as every older one is.

**Rationale**: §18's test is whether a second implementation could observe the change. A 0.7 adapter would not send
`settles` and would not understand an outcome of `settled`, and a person's line now arrives as a Request where a 0.7
surface sent a Result. The owner: no compatibility before release.

## R11. What the agent is told to do with it

**Decision**: `ParleyInstructions` and the descriptions of `parley_result` and `parley_clarify` gain a short paragraph:
a request from the person may answer questions you asked them earlier; the inbox lists those; name in `settles` only
the ones the person's words answered or made moot, and say which in your reply; leave the rest open.

**Rationale**: A model reads the tool description on every call (`ParleyTools` remarks). SC-007 is measured on a real
agent because only a real model shows whether these words are enough.
