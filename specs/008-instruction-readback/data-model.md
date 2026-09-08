# Data model: the instruction record

**Nothing here is new storage.** The table exists and carries every field the readback returns. This
document says what the fields mean, which states are reachable, and which transitions are legal —
because the defect being fixed is entirely about a state that was reachable and unreadable, and a
transition that was taken on false grounds.

## The record

`instructions`, from SPEC.md §9.2. Unchanged by this feature.

| Field | Meaning | Notes |
|---|---|---|
| `id` | Monotonic identifier | Returned by `instruct` from now on (FR-012), and the readback's narrowing key |
| `agent` | Who it was for | Foreign key to the registry; cascades on purge |
| `text` | The operator's own words | Capped at the same 8,000 as a message; the cap is in the schema |
| `outcome` | What became of it | One of five, below |
| `reply` | What the agent said | Null unless settled; the schema refuses a reply on a pending row |
| `issued_utc` | When the operator asked | **The age retention is measured from** — see below |
| `settled_utc` | When it stopped being pending | Null exactly when the outcome is pending |

Two CHECKs already carry the invariants and neither changes:

```
CHECK ((outcome = 'pending') = (settled_utc IS NULL))
CHECK (outcome <> 'pending' OR reply IS NULL)
```

The first is why *pending* is a real state rather than a transient; the second is why a running
instruction cannot appear to carry an answer.

## The five outcomes

| Outcome | Means | Who writes it |
|---|---|---|
| `pending` | Given to a machine; the turn is running and no answer exists yet | The broker, when it records the instruction, before anything is delivered |
| `taken` | The agent was given the turn and acted | The node's report |
| `declined` | The agent was given the turn and would not | The node's report |
| `undeliverable` | No machine connected; nothing was given a turn at all | The broker, immediately, before any waiting |
| `failed` | The turn was attempted and did not complete | The node's report; a node disconnecting; the sweep |

**What no longer writes an outcome: the broker giving up waiting.** That was the defect. Ceasing to
wait is a fact about the broker, not about the turn, and `failed` is defined as a claim about the
turn.

## State transitions

```
                    (recorded)
                        │
                        ▼
                    ┌───────┐
   no node ────────►│pending│──────► taken       node's report
   connected        └───────┘──────► declined    node's report
        │               │  │
        │               │  └──────► failed       node's report
        ▼               │                        │ node disconnects
  undeliverable         │                        │ sweep, past TurnBudget
   (immediate)          │
                        └── the broker giving up: NO TRANSITION (this feature)
```

**Every transition out of `pending` is terminal**, and that is enforced by the update's
`WHERE outcome = 'pending'` rather than by any caller remembering it. First-come wins. That is why
FR-016 needs no new mechanism: a sweep cannot alter a settled record however carelessly it is
written.

**`pending` is the only non-terminal state**, so "nothing stays unsettled" (US4) reduces to
"nothing stays pending", which is why exactly two mechanisms are needed and not more.

## Reachability, before and after

| State | Reachable before | Readable before | After |
|---|---|---|---|
| `pending` | Yes, always — every record starts here | **No** | Readable, and returned by `instruct` on give-up |
| `taken` with a reply | Yes | Only while the issuing call was held open | Readable at any time, by anyone who can reach the broker |
| `failed` on a live turn | **Yes, wrongly** — the broker's five-minute wait | Yes, and it was a lie | **Unreachable** |
| `failed` on a dead turn | Only if the node reported it | Yes | Also from disconnect and sweep |

The third row is the feature in one line: a state that was reachable, wrong, and destroyed the
truth arriving behind it.

## Retention

Aged on `issued_utc`, under the same cap as message traffic.

**Not on `settled_utc`, and the reason is a trap worth naming.** A pending row has no settle instant
— the CHECK guarantees it. A retention rule measured from settlement would therefore never reach a
row that nothing ever settled, which is precisely the set most in need of a bound. An unbounded
table can hide inside a retention policy that looks complete.

It follows that a settled instruction is never pruned before an unsettled one of the same age. That
is a property worth a test rather than an inference, because the opposite would let retention
destroy an answer nobody had read while keeping a row that says nothing.

## What the readback returns

Every field above except nothing — including `text` (FR-011). It is the operator's own words to
their own agent, on a surface no agent can reach, and §10.2 already discloses far more to the same
caller.

Ordered newest first, bounded by the broker, with an opaque continuation token.
