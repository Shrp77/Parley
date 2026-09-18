# Contract: what each surface asks for

Five places compose a Request or a Result. Each loses fields; none gains one.

## The agents' adapter — `parley_request`

| | Today | After |
|---|---|---|
| Arguments | `to`, `intent`, `items[]` (`id`, `kind`, `text`, `gateOn`, `gateEquals`), `doneWhen`, optional `conversationId` | `to`, `ask`, optional `conversationId` |
| Types | `RequestItem` record | gone |

The description says what the ask is for and nothing about framing: one ask, plainly, and ask a Clarify when the
scope is not clear. `RequestItem`, its `Kind` and its gate arguments are deleted rather than left unused.

## The agents' adapter — `parley_result`

| | Today | After |
|---|---|---|
| Arguments | `exchangeId`, `outcomes[]` (`id`, `outcome`, `text`, `sources`, `blocker`) | `exchangeId`, `outcome`, `text`, optional `sources`, optional `blocker` |

The adapter writes the single-entry `outcomes` array itself, with `id: "request"`. An agent never types an item id
again.

**Answering an exchange opened under 0.6**: its items are still the ones the broker asks for, so the adapter keeps a
way to answer them — it reads the Request it holds and, when that Request has items, answers each. The tool's own
arguments stay two: the model writes one answer, and the adapter names the ids it was given. Where a 0.6 Request had
several items, the same text answers each, which is honest for a Request that is now one ask.

## The adapter's standing instructions

`ParleyInstructions` tells every agent how to use Parley. The lines that teach intent, items, kinds and a finish line
go; one line replaces them: say what you want, and if you cannot tell how far a request goes, ask with
`parley_clarify` rather than guessing.

## The terminal composer

| | Today | After |
|---|---|---|
| Fields | Why you want this; what you want done; work or question; what counts as finished; conversation name | Conversation name; the ask |

The conversation name keeps its suggestion and its as-you-type correction. The result form loses its per-item
repetition: one outcome, one answer, its sources, and a blocker when blocked.

## The web console composer

The same two fields, with the same rules stated in the page and applied by the browser and the server, as they are
now. The transcript and the inbox keep rendering a 0.6 message whole.

## What every transcript shows

| | 0.6 message | 0.7 message |
|---|---|---|
| Request | intent, each item (id, kind, gate) and `done_when`, as today | the ask |
| Result | one line per item, as today | the answer, its sources, and the blocker when blocked |

No renderer invents a field it did not find, and none drops one it did.

## The command line

`parley export` flattens payloads onto one line, as it does now, and shows whatever the message carried. Nothing in
the operator's commands composes a Request.
