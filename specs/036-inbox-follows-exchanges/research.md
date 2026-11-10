# Research: The Inbox follows an exchange after it is answered

Each decision below was settled by reading what the broker and the surfaces already have (2026-11-06).

## R1. Which view an exchange is in

- **Decision**: decided by the broker's wait-for graph (`DescribeWaiting`) and the exchange's state, read the same way on
  both surfaces by one shared piece of code in Parley.Client:
  - *Waiting on you*: open, with an edge where the person is waited on and the waiter is somebody else. This is today's
    rule, `WaitingOnThePerson.OnMe`, unchanged.
  - *In flight*: open, with an edge where the person is the waiter, and no edge on the same exchange where the person is
    waited on.
  - *Closed*: closed, the person sent or received a message on it, and its `closed_utc` is within the window.
- **Rationale**: the graph is the broker's own record of who owes whom, so a row can be in only one view (FR-004), and
  it moves exactly when the turn moves. No surface infers it from what it last showed.
- **Alternatives considered**: keeping a per-console list of exchanges the person answered. Rejected: it is gone at a
  restart, differs between surfaces, and is the stored state FR-008 forbids.

## R2. The status chip (corrects the spec's chip names)

The broker does not keep what a wake said when a message was sent. It reports each agent's present state in the
registry (`RegistryRow`) and stuck exchanges in the flags report (`DescribeFlags`). So the chip is read from those, and
the spec's *Being started* becomes *Not running*: nothing records that a start is under way.

- **Decision**: one chip for the agent, from its registry row, in this order of precedence:

  | Registry says | Chip | Words beside it |
  |---|---|---|
  | `open_outside` | Open outside Parley | reads it when prompted there |
  | presence `STRANDED` | Cannot be started now | nothing happens until somebody starts it |
  | presence `STARTABLE` | Not running | a node can start it; Parley starts it when a message waits |
  | presence `RUNNING`, `turns_reported` false | Running | its turns are not reported |
  | `turn_running_since_utc` set | Working | a turn has run since HH:MM |
  | presence `RUNNING`, no turn running | Idle | no turn is running, and it has not replied |
  | anything else | Unknown | |

  And beside it, at most one flag on the exchange from `DescribeFlags`: `NO_PROGRESS` reads *Stalled*,
  `NOT_COLLECTED` reads *Not collected yet*, `PERMISSION_REFUSED` reads *Stopped: a permission was refused*, `RETIRED`
  reads *Retired*.
- **Rationale**: every word is something the broker reported (FR-006). "Working" means a turn is running, never that the
  agent is working on the person's message.
- **Alternatives considered**: storing the wake outcome on the exchange. Rejected: a schema change for one word that is
  out of date seconds later; the registry is already live.

## R3. Reading what closed

- **Decision**: `ListConversations(participant = person, since = window start, include_archived = true)`, then `Export`
  each conversation listed, and keep closed exchanges the person sent or received a message on whose `closed_utc` is
  within the window. The outcome comes from the exchange snapshot (`COMPLETE`, `BLOCKED`, `CLOSED`, `SETTLED`) and, for
  the words of each item, from the Result message on it (Done, Answered, Received, Blocked with its kind). "Closed by the
  person" is a `CLOSED` outcome whose closing message is the person's.
- "Show older" moves the window back by its own length: `?closed=2` covers days 7 to 14 by default, and so on.
- An exchange whose messages retention removed still has its snapshot (opened, closed, outcome), so it is listed with no
  quotation.
- **Rationale**: no new operation or storage (FR-008). Every one of the person's conversations is exported: an exchange can close inside the window with no message of its
  own in its conversation (settled by a Request elsewhere, or moved after settling), so a filter by last message lost it
  (review, lens 1 round 17).
- **Alternatives considered**: a new "closed exchanges for an agent" operator read. Rejected for now: the export path is
  correct and its cost is bounded by conversations active in the window. Revisit if a deployment shows it slow.

## R4. Finding an exchange by its number (for references)

- **Decision**: a new operator read, `Operators.LocateExchange(exchange_id) → conversation_id` (empty when there is no
  such exchange, or its conversation was destroyed). The surface then exports that conversation and draws the one
  exchange. It is on `Operators`, never `Agents`: an agent has no business reading exchanges by number (§10.1).
- **Rationale**: exchange numbers are unique per store, but no operation maps one to its conversation, and a reference
  often names an exchange in another conversation. Exporting every conversation to find one is wrong at any size.
- **Alternatives considered**: searching the exports the page already read. Rejected: it misses exactly the
  cross-conversation case the owner raised.
- `DriftTests` requires both screens to bind every `BrokerClient` operation: the web reference panel and the terminal's
  open-an-exchange command both call it.

## R5. The reference grammar

- **Decision**: one recogniser in Parley.Client (`ExchangeReferences`), shared by both surfaces, matching, ignoring case:
  - `exchange #N`
  - `exchanges #N1, #N2 and #N3` (any number of `#N`, separated by commas, "and" or "or", with optional spaces;
    a list may end with ", and" or ", or")

  where N is 1 to 18 digits with no leading zero, bounded by a non-word character on both sides. Each `#N` is its own
  reference. Nothing else is a reference: no bare numbers, no `#N` alone.
- On the web, it runs over the Markdown tree after `MarkdownText` has made the tree safe, only on plain text nodes, so
  code spans, code blocks and the text inside links are left as written (FR-020).
- **Rationale**: one grammar, written once, is the only way the two surfaces and the agent instructions agree. The fixed
  word "exchange" before the `#` is what keeps ordinary numbers out.

## R6. The reference panel on the web

- **Decision**: a reference renders as `<button type="button" class="exchange-ref" data-exchange="N"
  aria-expanded="false">#N</button>` inside the text. The message's container carries `data-project`. The layout's one
  script, on click, fetches `/Exchanges/Panel?project=P&id=N` and puts the returned fragment in a panel inserted right
  after the block (paragraph or list item) the button is in. A second click removes it. It works the same on the Inbox and
  the conversation page because both draw messages through `_MessageBody`.
- `/Exchanges/Panel` is a GET-only page with no layout that calls only `LocateExchange` and `Export`, so reading it never
  delivers or changes anything (FR-021). It draws each message with `_MessageBody`, nested references included, so a
  panel can open another.
- The page answers only same-origin fetches, like every page, and cannot be framed.
- **Rationale**: no new component for drawing messages (the owner's rule: one message view).

## R7. The terminal

- **Decision**:
  - A new dialog, the **Inbox** (`InboxView`, a `TableDialog`), opened from the conversations list and the room with a
    new Alt key, listing the three views with the same chips, one view at a time, switched with its own keys. Enter opens
    the row's conversation; a Follow up command opens the shared text dialog and sends a Request to the row's agent in its
    conversation.
  - **References**: when the transcript cursor is on a line whose message contains a reference, a room command *Open
    exchange #N* is live and named on the status line; with several on the line, it offers a choice. It opens an
    **Exchange** dialog (`ExchangeView`, a `TableDialog`) drawing that exchange's messages, every line made inert.
  - Its keys go in the command tables, every new dialog is listed in `CommandTables` and has a `KeyContext`, as the key
    rules require.
- The status line naming the command is how a reference is "marked" (FR-025): the transcript draws text one colour per
  line and has no inline styling to underline part of one.
- **Rationale**: the same views, rows and words come from the shared Parley.Client code, so the screens cannot drift.

## R8. Where the window is kept

- **Decision**: `<root>/settings.json`, beside `ports.json` and the projects, where `<root>` is the deployment root both
  surfaces already know (`BrokerDirectory.ManifestPath`). The shape is `{ "inbox": { "closedDays": 7 } }`. Read with
  `StrictJson`. Written atomically by rename, keeping unknown keys, as project files are. The reader and the writer live
  in Parley.Configuration (`ParleySettings`), which reaches no store and no transport.
- A missing file, a missing key or an out-of-range value reads as 7. The settings page refuses an out-of-range value
  and leaves the file as it was.
- **Rationale**: the terminal must read it (FR-012). The web's theme and density stay in the browser: they are about one
  browser, and the terminal has neither.

## R9. Follow-up

- **Decision**: `SeatVoice.RequestOpeningAsync(agent, words, conversation)`, as the conversation page sends, from the
  shared composer. Not offered on a facilitator row. A refused send keeps the words (FR-011, acceptance scenario 3).

## R10. Parley's own words and the agent instructions

- **Decision**: `StillOpen.Ask` writes `- exchange #N: quote`. `ParleyInstructions.Text` gains one bullet: "When you
  mention a numbered exchange, write exchange #351, or exchanges #349, #350 and #351. People reading can open it from
  there." The plugin README says the same. SPEC.md §5.6 states the form as a display convention, and the broker never
  reads it.
- Instructions are sent once per agent session, so the bullet is kept short; it is the only cost every agent pays.
