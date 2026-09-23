# Data model: A conversation is a room

Nothing is stored. Every type here is derived from what a read already returns, and lives only as long as the view
is open — which is the point: a draft held anywhere durable would be a second place a person's unsent words exist.

## Pair state

For one conversation and one seated person, the state between them and each other participant. Derived by joining
`Export`'s exchange snapshots with `DescribeWaiting`'s edges on the exchange identifier (research R1).

| field | meaning |
|---|---|
| `Peer` | the other participant |
| `Open` | the open exchange between them, or none |
| `Owed` | who owes the next move: this person, the peer, or nobody |

Three states matter and a fourth is the absence of the others:

- **`Owed` is this person** — the peer asked, and this person holds it.
- **`Owed` is the peer** — this person asked, and is waiting.
- **no open exchange** — nothing between them.

An exchange open between two *other* participants produces no pair state for this person, which is correct: §5.0
gives no way to join somebody else's exchange, so typing to them opens a new one.

## Act

What typing to one recipient would do. Derived from the pair state, never chosen by the person except in the one
case the derivation cannot reach.

| pair state | act | what it does | reversible |
|---|---|---|---|
| peer asked, this person holds it | `Answer` | a Result; closes the exchange | **no** |
| peer asked, this person holds it | `AskBack` | a Clarify; hands the turn back | yes |
| this person asked, waiting | `Clarify` | a Clarify on that exchange | yes |
| nothing open | `Ask` | a Request; opens one, and the peer will owe a Result | opens an obligation |

`Answer` and `AskBack` are the same pair state and the person chooses (FR-008). The default is stated on screen
and is `Answer`, because holding a Request and typing is much more often an answer than a question — but it is
*said*, never assumed, and one keystroke takes the other.

## Draft

What is written and not yet sent.

| field | meaning |
|---|---|
| `Text` | what the person typed, exactly |
| `Recipients` | resolved participants; every participant of the conversation unless narrowed |
| `Acts` | one act per recipient, derived |
| `Choice` | for recipients in the answer-or-ask-back state, which of the two |
| `Completion` | the list, when the text has an open sigil (derived, never held — R5) |

**The draft never carries addressing.** `Recipients` is a list of resolved agents; `Text` is what reaches a
payload, and FR-015 forbids the two being mixed. That is why `@` resolves into `Recipients` rather than leaving
text to be parsed at send time.

## Cost

What the preview says, so that FR-009 is a property of one type rather than a sentence assembled per screen.

| field | meaning |
|---|---|
| `NewExchanges` | how many Requests this send would open |
| `Closing` | how many exchanges it would close |
| `TurnsHandedBack` | how many Clarifies it would send |

`NewExchanges` is the number that matters: each is an obligation a peer owes a Result on, and §5.5 makes a plural
ask exactly this arithmetic.

## Command declaration

One per command. The input line parses, completes, refuses and lists from these (FR-017), so a command added later
is a declaration rather than another branch.

| field | meaning |
|---|---|
| `Id` | the identifier typed after `/`, with no `/` of its own |
| `Takes` | its parameters in order, each with a kind and whether it is required |
| `Kind` | `Draft` — changes what is about to be sent — or `Acting` — does something |
| `Door` | for a destructive command, what must be described and typed back before it runs (FR-027) |
| `Says` | what it does, shown in the completion list |

**A `Draft` command is not sendable alone** (FR-024). `/add` is the first of them, and FR-025 records why: an
agent joins a conversation by being party to an exchange, so adding with nothing to say would have to invent a
Request.

**A destructive command carries its door in the declaration**, so the door is part of what the command *is*. That
is what lets the guarantee stay checkable as operations move into commands (FR-028): the check reads declarations
rather than trusting each handler to remember.

### Parameter kinds

| kind | resolved by | example |
|---|---|---|
| `Agent` | the same completion the `@` sigil uses | `/add @billing` |
| `Text` | taken as written | a reason |

`Agent` parameters are resolved agents and not spellings (FR-018), for the same reason recipients are.

## Completion

The narrowing list, over either candidate set.

| field | meaning |
|---|---|
| `Sigil` | `/` for commands, `@` for agents |
| `Typed` | what follows the sigil so far |
| `Matches` | candidates whose identifier begins with `Typed`, in a stable order |
| `Selected` | which match is highlighted |

**Derived from the draft text, never held** (R5, FR-034): there is no flag saying somebody was completing
something, which is the only way deleting back into a match can reopen the list. `Matches` empty means the list is
closed and the draft is text.

**Prefix fills `Matches`; an exact identifier decides the send** (FR-035). Those are two comparisons and the model
keeps them apart deliberately: `/ad` has `Matches` of `[add]` and is still text, because nothing completed it.

## Outstanding item

What the narrow column lists, and what selecting one scrolls to.

| field | meaning |
|---|---|
| `Peer` | who is waiting, or who asked |
| `Owes` | what is owed: a Result, a reply to a Clarify, or a vote on closing |
| `At` | the message to put on screen when this is activated (R7) |

Built from the same pair state the acts come from, plus the closure polls the seat holds — so the list and the
line can never disagree about what is outstanding.
