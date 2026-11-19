# Contract: wire changes (protocol 0.9)

`proto/parley.proto`, service `Agents`. `Parley.Spec.Tests` holds this file against SPEC.md, so the
spec text changes in the same commit.

## `AttachRequest`

Gains:

```proto
  // The session this process had before its stream dropped, from that stream's Attached event.
  // Empty on a fresh start. Naming it hands that session's work to the new one without delivering
  // it again (section 14). Ignored if it names no session of this agent.
  string previous_session = <next free number>;
```

## `Attached` — the first event on an Attach stream

```proto
message Attached {
  int32 agent_marked_live = 1;

  // This session's identifier, issued by the broker when the stream opened. Sent back with every
  // collection, so a delivery records which session took it (section 14). Not a credential: it
  // names a session, and section 16 already concedes that anything reaching a broker can claim any
  // name.
  string session = 3;
}
```

`exchanges_redelivered = 2` is removed: nothing is given back on attach any more. Field number 2 is
reserved.

## `DrainInboxRequest`

```proto
message DrainInboxRequest {
  string agent = 1;
  int32 limit = 2;
  string session = 3;   // from this session's Attached event
}
```

## `HandBack` — new

```proto
  // This session will not finish what it holds: return it to the agent's next collection. Called by
  // an adapter shutting down normally. It says nothing about presence, which is the Attach stream
  // alone (section 4.0); an adapter that never calls it loses nothing (section 14).
  rpc HandBack(HandBackRequest) returns (Empty);

message HandBackRequest {
  string agent = 1;
  string session = 2;
}
```

Accepted for an open or a closed session of the caller. A second call does nothing.

## Refusals

| Call | Condition | Status | Message says |
|---|---|---|---|
| `DrainInbox` | `session` empty | `FailedPrecondition` | Collect from an attached session; the identifier is in the Attached event. |
| `DrainInbox` | `session` closed or unknown | `FailedPrecondition` | That session has ended; attach again. |
| `DrainInbox`, `HandBack` | `session` belongs to another agent | `PermissionDenied` | The session belongs to another agent. |
| `HandBack` | `session` empty or unknown | `FailedPrecondition` | No such session. |

`Attach` never refuses over `previous_session`: an unknown or foreign one is ignored and the session
opens fresh.

## Callers that change

- `parley-mcp` (`Parley.Mcp/BrokerLink.cs`): keeps the identifier from each `Attached` event; sends it
  as `previous_session` on every re-attach; calls `HandBack` in `DisposeAsync` before cancelling the
  stream. `ParleyTools` sends the current identifier on `DrainInbox`.
- The person's surfaces (`Parley.Client` `SeatHold`, `SeatVoice`, `SeatBook`): the same, with
  `HandBack` on a deliberate drop (the idle drop and closing the surface).

## Unchanged

- Every other `Agents` call is still run on any live session of the caller; only collection records a
  session, because only collection delivers. An answer is not tied to a session: whichever answer
  arrives first closes the exchange.
- `Operators` and `Nodes` are untouched. Agents replies still carry no turn information.
- `AgentsCannotSetTheirOwnLineTests` must still pass: `HandBack` sets nothing about the agent.
