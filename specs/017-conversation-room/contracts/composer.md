# Contract: what the input line does

One box, four acts, and a table that decides between them. This is the contract both surfaces implement — the
terminal now, the console after — so that SC-002 is a property of one derivation rather than a coincidence between
two.

## The derivation

For each recipient, given the pair state between them and the seated person:

| the peer… | act | on the wire | the exchange after |
|---|---|---|---|
| asked, and this person holds it | `Answer` (default) | Result | **closed** |
| asked, and this person holds it | `AskBack` (chosen) | Clarify | open, turn with the peer |
| is waiting on this person's ask | `Clarify` | Clarify | open, turn with the peer |
| has nothing open with this person | `Ask` | Request | **new**, peer owes a Result |

The only choice a person makes is between the first two, and only where that state holds. Everything else is the
state's own answer.

## What the preview says, before Enter

```
  to: billing, orders, inventory
> and refunds?
  → billing    answer, closes the exchange       [Tab: ask back instead]
  → orders     clarifies your open request
  → inventory  new request — it will owe a result
  2 exchanges stay open · 1 closes · 1 opens
```

Three rules about that block:

1. **It names every recipient.** A count alone would hide the case where one line does three different things,
   which is the case most worth seeing.
2. **It states the cost in exchanges, not in messages.** §5.5 makes a plural ask N exchanges; the number of
   obligations is what a person is actually deciding about.
3. **It names the key that takes the other choice** where there is one, and nowhere else. An action offered where
   it does not apply is the same fault as a key advertised and unbound.

## Enter, Tab, up and down

Enter performs the primary action of the active control (FR-031). Concretely:

| active | Enter | Tab | up / down |
|---|---|---|---|
| the box, no list | sends | takes the answer-or-ask-back alternative where one is offered | nothing this feature claims |
| the box, list showing | **sends** | completes the selection | move into the list |
| the list | activates the selection | completes the selection | move the selection |

The box staying active while the list shows is what makes Enter predictable (FR-032): what Enter does is answered
by what is active, never by what is on screen, and moving between them is deliberate.

## Refusals, and what survives them

| refused because | said | the draft |
|---|---|---|
| over rule 8.4's cap | the limit, and by how much | **kept** (FR-011) |
| the seat is no longer held | that the seat went, and to take one | kept |
| the exchange closed under the draft | which recipient, and that its act is no longer available | kept |
| a recipient has been retired or purged | which one | kept, that recipient dropped |
| nothing was typed | nothing sent, nothing said loudly | kept |

Never: a refusal that discards what somebody wrote. 0.25.1 records the cap being enforced only after the form
closed, which lost the message — that is the failure this table exists to prevent.

## Sending

N recipients is N sends (§5.5, research R9). Each is reported; failures are grouped by reason and named, as
`SeatVoice.RequestEveryoneAsync` already does. No attempt at atomicity: partial success is the honest outcome and
the report says which recipients got it.

**Nothing addressed reaches a payload.** The recipients are resolved before the send and the text is what was
typed, with no `@`, no name list and no routing of any kind (FR-015). A payload that carried routing would be
asking an agent to obey something §13's envelope tells it to treat as inert data.

## With no seat

The line is present and says what it needs: that a request is sent as somebody, and that a name is wanted. Taking
one opens the draft where it was. This follows 0.28.2, which found the owner reaching for Instruct because
starting a conversation was hidden without a seat, and fixed it by offering either way rather than hiding.

FR-016 is what keeps that safe: the seat is re-checked at the send, not captured when the line was focused — the
rule 0.18.9 established after finding three call sites that had captured it when a form opened.
