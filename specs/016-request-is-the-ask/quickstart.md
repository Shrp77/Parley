# Quickstart: A Request is the ask

Six steps. Each is something a person or an agent does, with what must be true afterwards. Walk it against a
throwaway project and a store that already holds 0.6 traffic — the second half of step 6 cannot be checked on an
empty one.

**Setting up**: a scratch manifest naming one project, two repositories enrolled as `billing` and `orders`, and a
broker from a build of this branch. `parley --version` reports protocol 0.7 and schema 11.

## 1. A person asks, in two fields

In `parley terminal`, take a seat, then press `k` in the conversations pane.

- The form has two fields: the conversation name (pre-filled) and the ask.
- Send "Which file validates the licence key, and what does it check?".
- **Then**: the exchange is open, the conversation holds one message, and the transcript shows the ask with nothing
  above it but the header.

## 2. The agent answers the exchange whole

As `billing`, collect the inbox and answer with `parley_result`: an outcome of `answered`, the text, and no item id.

- **Then**: the exchange is closed, the transcript shows the answer, and `parley results` counts one `answered`.
- **And**: nothing in the tool's arguments named an item, a kind or a finish line.

## 3. An unclear ask is settled by asking

As `orders`, send `billing` the ask "Tidy the invoice tests." — which says nothing about how far it goes.

- As `billing`, send a Clarify: "All of them, or only the ones touching tax?"
- **Then**: the exchange is still open, the turn is with `orders`, and the clarification is in the transcript.
- As `orders`, clarify back, and let `billing` close it.
- **Then**: one exchange, one Result, and the scope was settled in the conversation rather than in a form.

## 4. The old shape is refused

Send a Request whose payload carries `intent`, `items` and `done_when` (over the wire, past the adapter).

- **Then**: storage refuses it, the refusal names the ask, and nothing is stored — no half-open exchange.
- Send one with an empty `ask`.
- **Then**: refused for the same reason.

## 5. An agent built against 0.6 is refused

Attach with an adapter announcing protocol 0.6.

- **Then**: the attach is refused, naming the version mismatch, and the agent is not registered.

## 6. A 0.6 conversation still reads

Open a conversation stored before the migration.

- **Then**: in the terminal, the web console and `parley export`, its Requests still show their intent, every item
  with its kind, and their `done_when`; its Results still show one line per item.
- If one of its exchanges is still open, answer it.
- **Then**: the Result is accepted, answering the ids the Request carried, and closes it.

## What would make this a failure

- A composer asking for anything beyond the conversation and the ask.
- A stored 0.6 message rendering with a field missing, or a field the sender never wrote.
- A 0.6 exchange left unanswerable after the upgrade.
- The word `done_when`, `intent`, `kind` or `gate` appearing in any tool description, placeholder, instruction or
  document that describes how to send a Request — as opposed to the migration, SPEC.md §18's history, and the record
  of this change.
