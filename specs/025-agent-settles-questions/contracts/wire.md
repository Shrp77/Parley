# Contract: the wire (protocol 0.8)

Changes to `proto/parley.proto`. Nothing is kept for 0.7 (unreleased).

## Agents service

```proto
message ClarifyRequest {
  string from = 1;
  int64 exchange_id = 2;
  string responder_model = 3;
  string payload_json = 4;
  // Exchanges the person's request settled (section 5.6). Only on a Clarify or Result from the receiver of a
  // request the person opened; empty otherwise. At most 50; a repeated id is settled once.
  repeated int64 settles = 5;
}

message ResultRequest {
  string from = 1;
  int64 exchange_id = 2;
  string responder_model = 3;
  string payload_json = 4;
  repeated int64 settles = 5;           // as on ClarifyRequest
}

message SayReply {
  bool ok = 1;
  TransitionError refusal = 2;
  bool deduplicated = 3;
  int64 message_id = 4;
  repeated string expected_items = 5;
  // On CANNOT_SETTLE: each exchange that could not be settled, and why. Nothing was stored.
  repeated SettleRefusal cannot_settle = 6;
}

message SettleRefusal {
  int64 exchange_id = 1;
  SettleProblem problem = 2;
}

enum SettleProblem {
  SETTLE_PROBLEM_UNSPECIFIED = 0;
  SETTLE_PROBLEM_NOT_FROM_THE_PERSON = 1;    // the reply is not on a request the person opened with this agent
  SETTLE_PROBLEM_OTHER_CONVERSATION = 2;
  SETTLE_PROBLEM_NOT_OPEN = 3;               // closed, or no such exchange (indistinguishable on purpose, as elsewhere)
  SETTLE_PROBLEM_NOT_BETWEEN_YOU = 4;        // not between this agent and the person
  SETTLE_PROBLEM_NOT_THEIR_MOVE = 5;         // the person does not owe the next move on it
}

enum TransitionError {
  ...
  TRANSITION_ERROR_CANNOT_SETTLE = 9;
}

message DeliveredMessage {
  Message message = 1;
  string envelope = 2;
  // Only on a request from the person: the agent's own open questions to the person in this conversation, oldest
  // first, at most 20 (section 5.6).
  repeated SettleableQuestion settleable = 3;
  int32 settleable_left_out = 4;
}

message SettleableQuestion {
  int64 exchange_id = 1;
  string opened_utc = 2;
  string asked = 3;                         // the agent's own last words in it, at most 160 characters
}
```

## Operators service

```proto
enum ExchangeOutcome {
  ...
  EXCHANGE_OUTCOME_SETTLED = 4;
}

message ExchangeSnapshot {
  ...
  int64 settled_by = 6;                     // 0 unless the outcome is SETTLED
}
```

`RegistryRow.open_outside` is never set on the person's or the facilitator's row.

## Version

`ProtocolVersion.Current` = `0.8`. `Implements` answers it; an adapter announcing anything else is refused at attach.
