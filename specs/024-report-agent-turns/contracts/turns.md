# Contract: turn reporting (024)

Additive. Nothing about addressing, admission or delivery changes (FR-014).

## Nodes service — what a node tells the broker

```proto
// Added to JoinRequest: this node reports its turns. A node that does not set it leaves its agents' turn standing
// "unknown" rather than "stopped" (research R5).
bool reports_turns = 4;

// New.
rpc ReportTurn(TurnReport) returns (Empty);

message TurnReport {
  string machine = 1;                 // must equal the joined node's machine (R10)
  string agent = 2;                   // must be an agent on that machine
  oneof phase {
    TurnStarted started = 3;
    TurnEnded ended = 4;
  }
}

message TurnStarted {
  string started_utc = 1;            // RFC 3339, UTC, as every time on this wire
}

message TurnEnded {
  string started_utc = 1;                      // pairs it with its start
  int64 elapsed_ms = 2;
  bool clean = 3;                              // completed, not an error
  bool timed_out = 4;
  bool process_died = 5;
  int32 permission_denials = 6;
  double cost_usd = 7;
  int32 model_turns = 8;
}
```

Broker behaviour:

- A start sets the agent's running turn; an end records an `invocations` row (waiting counts filled by the broker) and
  clears the running turn if it is not newer than the end's start.
- Reports for an agent not on the reporting machine, from a node not joined, or for an agent the broker does not know,
  are ignored and nothing is stored. So are reports for the person or Parley, which no node hosts, a start more than a
  day from the broker's clock, and an end claiming a turn longer than a day (all three found by the combined review gate).
- A node's stream ending clears every running turn it reported.
- A failed report never fails the turn: the node logs and carries on.

## Operators service — what a surface reads

`DescribeRegistry`'s agent gains:

```proto
bool turns_reported = 12;
string turn_running_since_utc = 13;   // empty when no turn is running
string last_turn_ended_utc = 14;      // empty before the first reported turn
bool last_turn_clean = 15;
int32 last_turn_permission_denials = 16;
```

`DescribeFlags` gains `FlagKind.PERMISSION_REFUSED`.

## Agents service — unchanged

`ListAgents` (the roster an agent reads) gains **nothing**. A test asserts no field about turns appears on any message
the `Agents` service returns.

## Surfaces — the words, one implementation (`Parley.Client`)

| State | Room / participant row |
|---|---|
| Working | `◆ <name> — working 4m` |
| Waiting on X | `● <name> — waiting on <X>` |
| Stopped | `■ <name> — stopped 2h48m` and, when N > 0, `— N permissions refused` |
| Unknown | `<mark from presence> <name>` with no working or stopped claim |

The legend gains `■ stopped, still owes work`.
