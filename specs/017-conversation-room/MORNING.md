# The conversation room: what is built, what is tested, what is not

Written overnight, 2026-09-21/22, on the instruction *"Can you drive planning, tasking and implementation so I
can see it / test it tomorrow morning?"* — so this is the handover, and the part worth reading first is
**[Decisions taken without asking](#decisions-taken-without-asking)** and
**[What is not built](#what-is-not-built)**.

Everything below is on branch `015-file-store`, at **0.30.0**, installed with `-Force`.

## How to see it

```
parley-terminal
```

Walk in: a project, then a conversation. The conversation view is now three panes rather than two:

```
┌ Room ──────────────────────┬ Transcript ────────────────────────────────────────────┐
│ in this conversation       │  billing@here -> arvind@here (Request)  collected 14:03 │
│   billing@here — waiting…  │    is the pricing table still authoritative?           │
│   orders@here — owes you   │                                                        │
│                            │  …                                                     │
│ waiting on you             │                                                        │
│   billing@here — answer o… ├ Say something ─────────────────────────────────────────┤
│                            │ to: billing@here, orders@here                          │
│                            │   billing@here  answer — closes the exchange (F2: ask…)│
│                            │   orders@here   clarifies your open request            │
│                            │ 1 closed · 1 handed back                               │
│                            │ ▸ and what about refunds?                              │
└────────────────────────────┴────────────────────────────────────────────────────────┘
```

Tab to the **Say something** pane and type. Everything else follows from that.

| you press | what happens |
|---|---|
| any character | goes in the box — including `?` and `x`, which are action keys everywhere else |
| Enter | sends what is written, exactly as the preview above it said |
| `/` | opens the command list, narrowing as you type; each row says what the command is for |
| `@` | opens the participant list, plus `all` |
| Tab, list showing | completes the selection |
| Tab, no list | next pane, as everywhere else on this surface |
| up / down, list showing | move the selection; the box keeps focus, so Enter still sends |
| F2 | answer, or ask back instead — where that choice applies |
| Escape | back out of the conversation |

## What the box does, which is the whole of the feature

One box, and the protocol has four acts. **Which act you are performing is derived per recipient from who owes
whom** and never chosen first:

| the peer… | what typing is | the exchange after |
|---|---|---|
| asked you, and you hold it | a **Result** (default) | closed |
| asked you, and you hold it | a **Clarify** (F2) | open, turn back to them |
| is waiting on your ask | a **Clarify** | open, turn with them |
| has nothing open with you | a **Request** | new, they owe a Result |

So one typed line to three agents may be a Result to one, a Clarify to another and a Request to the third, and
each is right for its own pair. The preview names every recipient, what each will get, and the cost in
exchanges — `3 exchanges opened, each owing a result` — so §5.5's plural ask is read before Enter rather than
found in `parley flags` the next day.

**The one case that is not derivable is never guessed.** Holding a request, what you type may be the answer
(which closes it) or a question you need settled first (which hands the turn back). A wrong guess closes work
that was never done. Both are offered, the default is stated, and F2 takes the other.

## Addressing

- Typing goes to **every participant** of the conversation by default.
- `@` and a completion **narrows** to that agent. The first `@` replaces; further ones add. `@billing` then
  `@inventory` addresses those two.
- `@all` addresses every participant again — never every agent in the project.
- `/add @someone` **widens** to an agent that is not in the conversation yet. It gets a Request, which is how
  an agent joins a conversation at all: membership is derived (§5.0), so an agent is a participant by being
  party to an exchange.
- A completed agent **leaves the text and becomes a recipient** — a chip, not words. Nothing is parsed out of
  prose at send time, so a semicolon in an ask is just a semicolon, and no routing directive ever reaches a
  payload that §13's envelope tells the receiver to ignore.

`/something` that is not a command is **text**, sent as written. A near miss is named above the box before
Enter — *"this is text, not a command — did you mean /add?"* — because that is the only defence against a typo,
and `/etc/hosts` is a path decided without reference to what any command is called.

## What is tested

Every suite from its own host executable (`dotnet test` reports "Zero tests ran" on this machine):

| suite | tests | failed |
|---|---|---|
| Parley.Core.Tests | 276 | 0 |
| Parley.Store.Tests | 631 | 0 |
| Parley.Broker.Tests | 223 | 0 |
| Parley.Server.Tests | 303 | 0 |
| Parley.Client.Tests | 340 | 0 |
| Parley.Terminal.Tests | see below | — |
| Parley.Web.Tests | 194 | 0 |
| Parley.WebUi.Tests | 8 | 0 |
| Parley.Spec.Tests | 49 | 0 |
| Parley.Mcp.Tests | 197 | 0 |
| Parley.Cli.Tests | 883 | 0 |

New for 017: `PairingTests` (9), `ComposingTests` (18), `CompletionTests` (20), `CommandTableTests` (18),
`RoomTests` (10), `LineTests` (7), `CompletingTests` (10). The last is driven over a real socket, because every
question in it is about a key arriving.

## What building it found

Three defects that only existed once the box was on screen, and the first is why the other two had never been
met.

1. **The line pane had no size at all.** The arrangement handles one pane, stacked panes, and two side by side;
   this view has three, and the third was never placed. The region existed, was visible, held text, and drew
   nothing — while `LineTests` set its text in memory, read it back, and passed. **A property the suite holds to
   and the screen never shows**, which is the third time this repository has recorded that exact shape.
2. **Every printable key belonged to an action, including inside the box.** Typing `?` opened the actions menu
   and `x` cleared the message line. A focused box wins every character now.
3. **The answer-or-ask-back alternative was advertised on Tab**, which this surface consumes for pane
   navigation — a key named on screen and doing something else. It is F2 now.

Plus: a closed completion list was handed a selection of row zero, which the toolkit refuses outright (nothing
selected is null, not nought), and it threw on the first keystroke of every new test; and a frame is only true
after a layout pass, so reading the screen is not enough to measure one.

## Decisions taken without asking

Each of these is yours to reverse; they are listed because none was in the specification.

1. **Tab's meaning is split by what is on screen.** It completes an open list — you asked for that by name —
   and otherwise moves to the next pane, which is what it means in every other pane. The alternative was a Tab
   trap in the box.
2. **The answer-or-ask-back flip is F2**, with a second route under `a` in the Room column. A letter was not
   available: every letter in that pane belongs to the text. F8 is the precedent for a chord.
3. **A completed agent is removed from the text.** The specification said "a chip, not text" and this surface
   has no chips to draw, so the resolved recipient lives in the draft and the preview's `to:` line is what
   displays it. The alternative — leaving `@name` in the words — would put routing in a payload.
4. **`/add` is taken out of the box together with its agent**, so the box always holds prose. A command still
   there at Enter is one nothing consumed, and it is run or refused rather than sent.
5. **The first `@` replaces and later ones add.** Your own example (`@billing, @inventory; I want this`) has no
   other reading, but nothing states it, so it is stated here.
6. **Narrowing and added agents survive the three-second refresh.** Otherwise a draft narrowed to one agent
   would widen itself back to the room while you were still typing to them.
7. **Escape does not dismiss the completion list; it backs out of the conversation.** The list is derived from
   the text and never remembered (so deleting back into a match reopens it), and a dismissal would be the one
   piece of remembered state that design rejects. This is the decision I am least sure of — dismissing a
   popup with Escape is a strong habit.
8. **`SeatVoice` records a Result's outcome as `answered`.** One outcome per Result under protocol 0.7, and
   this surface does not ask which. `done` and `acknowledged` are not reachable from the box.
9. **The room's column is 34 columns, fixed rather than a share.** It holds names and short clauses whatever
   the terminal is.

## What is not built

- **Phase 5 entirely** (T028–T032): selecting something in "waiting on you" does not yet scroll the transcript
  to that message. The list is there and correct; activating a row does nothing. That needs the message
  identifier carried on each transcript line, which is a change in `Reading.cs` and `TranscriptRegion`.
- **The review gate for 017 has not been started.** Nine clean runs are owed. The code is green and unreviewed,
  which is exactly the state CLAUDE.md warns against calling done: *"A build that passes is not a review."*
- **The web console does not match.** Scoped out deliberately; `DriftTests` holds the two surfaces to parity on
  operations rather than on layout.
- **Nothing is pushed.** `015-file-store` has no PR, and it now carries 0.27.0 through 0.30.0.
- **`/add` is the only command.** The table exists so the next one is a row.

## The one thing to try first

Open a conversation where an agent is waiting on you, type an answer, and read the preview before you press
Enter. That block — who gets it, what each gets, what it costs — is the whole argument for one box instead of
four keys, and it is the part I cannot test on your behalf.
