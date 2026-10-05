# Contract: what the agent, the terminal and the web UI show and do

## The agent (`parley-mcp`)

**`parley_inbox`** — beside a request from the person:

```jsonc
{
  "exchange_id": 112, "type": "request", "from": "human-user@alpha", "conversation_id": "release",
  "envelope": "…",
  "your_open_questions_to_them": [
    { "exchange_id": 65, "asked_utc": "…", "you_asked": "Which invoice schema should the export target?" },
    { "exchange_id": 89, "asked_utc": "…", "you_asked": "Should a refund reopen the order, or start a new one?" }
  ],
  "more_open_questions": 0,
  "settling": "If their words answer or make moot any of these, name them in settles when you reply. Leave the rest open."
}
```

**`parley_result` and `parley_clarify`** gain `settles` (optional, a list of exchange ids). Success adds
`"settled": [65]` (and `"turned_back": [..]` for the person's own requests handed back). A refusal names each id and
its problem in a sentence:

> 89: the person does not owe the next move on it. Nothing was sent; send it again without 89.

**Standing instructions** gain the paragraph in research R11.

## The terminal

**The box**: every recipient's act is a request. The preview says who the line goes to, nothing about answering.

**The room, *waiting on you* group**: one row per agent, `<name>  <count>`. The name follows the room's existing rule
(role alone unless two share it).

**Enter on that row** opens a list titled `Waiting on you from <name>`:

```text
 Oct 02 14:41  Which invoice schema should the export target? Version 11 drops…
 Oct 03 13:16  Billing and inventory both read the price from the order line, s…
 Oct 03 16:22  Should a refund reopen the order, or start a new one? Reopening…
```

Oldest first, cut to the width with `…`. Choosing one closes the list and moves the transcript to that message, with
two rows above it. A question settled since the list opened says *"That question has been settled since."* and does not
move the transcript.

**Under an agent's reply that settled questions** (FR-021), from the record:

```text
  Settled: "Which invoice schema should the export target?", "Should a refund reopen…"
```

**A settled question in the transcript**: its exchange's state reads `settled by <agent> in exchange <n>` where the
other states read `answered`, `blocked` and so on.

## The web UI

**Conversation page**: a *Waiting on you* section above the transcript when anything is, one line per agent with the
count, each question below it as a link to the message's anchor. Same agents and counts as the terminal.

**Settled questions** read as in the terminal.

**Inbox page**: unchanged (an explicit answer to a request the person picked).
