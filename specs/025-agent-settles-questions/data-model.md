# Data Model: The person talks; the agent decides what it settled

## Exchange (changed)

Schema 1, edited in place (unreleased; a store from before needs a reset).

| Column | Change | Rule |
|---|---|---|
| `outcome` | admits `settled` | `CHECK (outcome IN ('complete', 'blocked', 'closed', 'settled'))` |
| `settled_by` | **new**, `INTEGER REFERENCES exchanges(id)` | `CHECK ((outcome = 'settled') = (settled_by IS NOT NULL))`; `CHECK (settled_by IS NULL OR settled_by <> id)` |

**Trigger `tr_settled_by_its_requester`** — before an update that sets `outcome = 'settled'`, refuse unless all hold:

- the settling exchange (`settled_by`) is in the same conversation;
- its requester is this exchange's receiver, and its receiver is this exchange's requester;
- this exchange's receiver is the person (adapter `parley-console`).

The last also refuses settling the person's own request: its receiver is an agent.

**State transitions**

```text
OPEN, owed_by = person, requester = agent   --settle(by agent, settling = X)-->   CLOSED, outcome = settled, settled_by = X
OPEN, owed_by = person, requester = person  --turn back(by agent)-->              OPEN, owed_by = agent   (no message, depth unchanged)
```

Every other transition is unchanged. `closed_utc` is set on settling, as on every close.

## Exchange outcome (domain, Parley.Core)

`ExchangeOutcome` gains `Settled = 3`. `Exchange` gains `SettledBy` (nullable exchange id) and two transitions:

- `Settle(AgentName from, long settling, DateTimeOffset now)` — refused unless open, `from` is the requester, and the
  turn is the receiver's.
- `TurnBack(AgentName from)` — refused unless open, `from` is the receiver, and the turn is the requester's.

Whether the other party is the person, and the conversation and pairing of the settling exchange, are checked by the
broker, which can see both exchanges; storage checks them again (above).

## Settleable question (delivered to the agent)

Not stored. Computed when a Request from the person is drained (`Broker.DrainInboxAsync`).

| Field | Meaning | Bound |
|---|---|---|
| exchange id | the question's exchange | — |
| opened | when the agent asked | — |
| asked | the start of the last thing the agent said in that exchange | 160 characters, cut at a character boundary with `…` |
| left out | how many more there were | at most 20 entries listed |

## Waiting-on-you entry (surfaces)

Not stored. Derived from the conversation export and the wait-for graph (`Pairing`), the same way on both surfaces.

| Field | Meaning |
|---|---|
| peer | the agent |
| questions | every open exchange in the conversation between the peer and the person on which the person owes the next move, oldest first: exchange id, opened, the message to show (the latest thing the peer said in it), the start of its text |

The count shown is `questions.Count`.
