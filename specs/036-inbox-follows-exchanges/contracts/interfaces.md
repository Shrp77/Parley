# Contracts: The Inbox follows an exchange after it is answered

## 1. Wire: `Operators.LocateExchange`

```proto
// Which conversation an exchange is in now (section 5.0: a move changes it). Operator-only: an agent has no reason
// to find exchanges by number, and the agent surface does not grow (section 10.1).
rpc LocateExchange(LocateExchangeRequest) returns (LocateExchangeReply);

message LocateExchangeRequest {
  int64 exchange_id = 1;
}

message LocateExchangeReply {
  string conversation_id = 1;   // empty when there is no such exchange in this store, or its conversation was destroyed
}
```

- Read only. Never delivers, closes or records anything.
- `BrokerClient.LocateExchangeAsync(long, CancellationToken) → string?` (null for empty).
- `IParleyBrokerClient` and `OperatorConsole` gain the same read.

## 2. Reference grammar (`ExchangeReferences.Find(string) → IReadOnlyList<ExchangeReference>`)

Ignoring letter case:

```text
reference  := "exchange" space "#" number
            | "exchanges" space "#" number (separator "#" number)*
separator  := optional-space ("," | "and" | "or" | ", and" | ", or") optional-space
number     := [1-9][0-9]{0,17}
```

The match starts and ends at a word boundary. Each `#number` in a match is one reference.

| Text | References |
|---|---|
| `Thanks for the answers on exchange #351.` | 351 |
| `exchanges #349, #350 and #351` | 349, 350, 351 |
| `Exchanges #12 or #13` | 12, 13 |
| `on 351` / `#351` / `exchange 351` / `exchange #0351` | none |
| `` `exchange #351` `` (code, on the web) | none |

## 3. Web: `GET /Exchanges/Panel?project=P&id=N`

- A fragment, no layout: the exchange's agents, its state, each message through `_MessageBody`, and a link
  `/Conversations/Detail?project=P&id=C#mM`.
- Unknown project: 404. No such exchange: a fragment saying "There is no exchange #N in P."
- Reads only `LocateExchange` and `Export`.

## 4. Web: Inbox views

- `GET /Inbox` (Waiting on you), `GET /Inbox?view=flight`, `GET /Inbox?view=closed&older=K` (K = 0 for the window, 1 for
  the window before it, and so on). Filters (`q`, `scope`) are kept across views.
- `POST /Inbox?handler=FollowUp` with `For` (project), `Agent`, `Conversation`, `Words`: sends a Request.
- `GET /Inbox?handler=Digest` also changes when an exchange the person is part of closes, so every view follows changes.

## 5. Web: Settings

- `POST /Settings?handler=Inbox` with `ClosedDays` (1 to 365). Refused outside the range with the range named; the file
  is unchanged.

## 6. Machine file: `<root>/settings.json`

```json
{
  "inbox": { "closedDays": 7 }
}
```

## 7. Terminal

- Conversations list and room: a new command *Inbox* (Alt plus a free letter) opens `InboxView`.
- `InboxView`: commands for the three views, *Open conversation* (Enter), *Follow up*, *Open exchange*.
- Room: a command *Open exchange #N*, live when the transcript cursor's message holds a reference, opens `ExchangeView`.
- Every new dialog derives from `TableDialog<T>`, has a `KeyContext`, and is listed in `CommandTables`.

## 8. Agent instructions

`ParleyInstructions.Text` gains:

> - When you mention a numbered exchange, write exchange #351, or exchanges #349, #350 and #351, so a person reading can
>   open it from there.
