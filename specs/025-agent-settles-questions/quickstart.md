# Quickstart walk: The person talks; the agent decides what it settled

Walked against tools packed and installed into a scratch path, with a root, a store and a port of their own and every
`PARLEY_` variable cleared, as 019–024's walks are (`walk.ps1` beside this file). Agents are scripted `parley-mcp`
clients over their own stdio, except step 6.

## 1. A line to an agent with questions waiting is a new request

Seat the person in project `alpha`. Have `orders` send the person three requests in conversation `release`. Send a line
from the person to `orders` through `SeatVoice.SendAsync` with the draft the box would build.

**Expect**: a fourth exchange, requested by the person, owed by `orders`; all three of `orders`' requests still open and
owed by the person; `orders` given a turn.

## 2. The agent is told what it could settle

Drain `orders`' inbox through `parley_inbox`.

**Expect**: the person's request, with `your_open_questions_to_them` listing the three, oldest first, each with its id,
when it was asked and the start of what `orders` said.

## 3. Settling closes exactly what was named

`orders` answers the person's request with `parley_result`, `settles: [<second>]`.

**Expect**: the second request closed, outcome `settled`, `settled_by` the person's exchange; the first and third still
open; the person's waiting count for `orders` down from 3 to 2 on `Export` and on `DescribeWaiting`.

## 4. A bad name refuses the whole reply

`orders` answers another person's request with `settles` naming: an exchange between `orders` and `billing`; one already
closed; one in another conversation.

**Expect**: refused with `CANNOT_SETTLE` and each id with its problem; nothing stored — the person's request still open,
no Result message written.

## 5. The person's own request comes back to the agent

The person asks `billing` something; `billing` asks back (Clarify), so the person owes the move. The person then sends
`billing` a new line; `billing` replies naming the first exchange in `settles`.

**Expect**: the first exchange open and owed by `billing` again, depth unchanged, no message added to it; `billing` can
then send its Result there.

## 6. A real agent settles what it should (SC-007)

With this repository's own agent registered on the live deployment (the owner's call to run it), have it ask the person
two things, then send it a line that answers one of them. **Expect** it to name that one in `settles` and leave the
other. Record what it actually did in the ledger, whichever way it went.

## 7. The surfaces

Terminal (driven): in step 3's conversation, the room's *waiting on you* group has one row for `orders` with `2`; Enter
opens two questions oldest first; choosing the second shows that message in the transcript. Web: the conversation page
lists `orders` with 2 and two links to the messages' anchors.

## 8. The person is not open outside Parley

Read the registry with the person seated. **Expect**: the person's row and the facilitator's row have `open_outside`
false.
