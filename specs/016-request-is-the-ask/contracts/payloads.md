# Contract: the payloads, 0.6 beside 0.7

The wire messages do not change shape. `OpenRequest` already carries the payload as one JSON document
(`proto/parley.proto:120-129`); what changes is the document, and the comment beside the field, which today reads
"intent, items, done_when — section 5.1".

## Request

**0.6, as stored and still read:**

```jsonc
{
  "type": "request",
  "to": "billing",
  "intent": "Adding tax-inclusive pricing, so invoices show exactly what the order charged.",
  "items": [
    { "id": "r1", "kind": "work", "text": "Use the price the order supplies." },
    { "id": "r2", "kind": "question", "gate": { "on": "r1", "equals": "done" },
      "text": "Which invoice version is first to use it?" }
  ],
  "done_when": "Invoices after the change show the order's price, and r2 names the version."
}
```

**0.7, written from now on:**

```jsonc
{
  "type": "request",
  "to": "billing",
  "ask": "Stop recomputing the line-item price at invoice time — use the price the order supplies — and tell me which invoice version is the first to do it."
}
```

- `ask` is text, non-empty, and within rule 8.4's cap. Storage refuses anything else.
- The conversation is not in the payload: it travels in `conversation_id` and is decided by §5.0, unchanged.
- A Request to a bare role still opens one exchange per registered agent under it (§5.5), each carrying this ask.

## Result

**0.6, as stored and still read:** one entry per item, each naming the item's id.

**0.7, written from now on:**

```jsonc
{
  "type": "result",
  "exchange_id": 47,
  "outcomes": [
    { "id": "request", "outcome": "answered", "sources": ["inventory"],
      "text": "Invoice schema 12. Inventory confirmed it reports the same price from schema 12 on." }
  ]
}
```

- Exactly one outcome, whose `id` is `request` — the spelling the broker already requires of a Request with no
  readable items, and the one the broker writes when a person closes a conversation.
- `outcome` is one of `done`, `answered`, `acknowledged`, `blocked`, `closed`; `closed` stays the broker's alone.
- `blocked` carries `blocker` (`missing_information`, `conflict`, `permission`) and the trail §5.3 already requires.
- `sources` names registered participants, and storage still enforces it.

**Answering a 0.6 Request that is still open:** its ids are still read, and a Result must still answer each of them.
Nothing times out (§14), so such an exchange can outlive the upgrade; `PayloadShape.ItemIds` is what keeps it
answerable.

## Clarify

Unchanged in every respect, and now the documented way a receiver settles an ask it cannot scope:

```jsonc
{ "type": "clarify", "exchange_id": 47, "text": "Do you want the change on invoices already issued, or only new ones?" }
```

## What a second implementation will observe

- A Request carries `ask` and no `intent`, `items` or `done_when`; one carrying them is refused by storage.
- A Result carries exactly one outcome named `request` for any Request written under 0.7.
- The protocol version announced at attach is `0.7`, and a 0.6 adapter is refused (§18).
- Everything else — assignment, waking, closure and consensus, clarification depth, files — is as it was in 0.6.
