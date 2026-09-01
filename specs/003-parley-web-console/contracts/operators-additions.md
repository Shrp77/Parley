# Contract: two additions to `service Operators`

**Feature**: [spec.md](../spec.md) | **Plan**: [plan.md](../plan.md) | **Date**: 2026-08-30

These are the two operations the console needs and the operator surface does not have. They are
recorded here as a proposal against `SPEC.md` §10.2. **The specification change lands first**;
this file is the argument for it, not a substitute.

Both are reads of state the store already holds. Neither adds a message type, a state
transition, or an invariant. Both go on `Operators` and on `OperatorConsole` — never on `Agents`,
never on `Broker` — because listing every conversation in a deployment is precisely the
disclosure §10.1 withholds from an agent, and §5.0 grants to a person.

## 1. `ListConversations`

Answers: *which discussions exist, and which of them do I want?*

```proto
message ListConversationsRequest {
  // Optional. Restricts to conversations this agent is party to.
  string participant = 1;
  // Optional ISO-8601 bounds on last activity.
  string since_utc = 2;
  string until_utc = 3;
  // Paging. The server bounds page_size regardless of what is asked for.
  int32 page_size = 4;
  string page_token = 5;
}

message ListConversationsReply {
  repeated ConversationSummary conversations = 1;
  string next_page_token = 2;
}

message ConversationSummary {
  string conversation_id = 1;
  repeated string participants = 2;   // derived from the exchanges, not declared
  int32 message_count = 3;
  string first_activity_utc = 4;
  string last_activity_utc = 5;
  bool has_open_exchange = 6;
}
```

**Why it must exist.** `Export` takes a `conversation_id` and nothing produces one. The
identifier is opaque and chosen by an agent, so a person does not know it. Without this
operation the console's primary user story is unreachable, and the operator's §5.0 entitlement to
"see the whole shape of a discussion" has no way to be exercised except by reading the database.

**Why paged, from the first version.** The message log grows for as long as the deployment runs.
An unbounded listing is the same mistake the message cap exists to prevent, one level up, and a
bound added later is a bound nobody applies. The server enforces its own ceiling on `page_size`
rather than trusting the caller's number.

**Ordering**: most recent activity first. That is the order an operator opening the console
wants, and it makes the first page the useful one.

**Participants are derived.** A conversation has no membership list; an agent is a participant
because it is party to an exchange carrying the identifier. The summary reports what the
exchanges show and asserts nothing about agreement — an identifier remains a claim by the sender.

## 2. Exchange state on an export

Answers: *did this question ever get answered?*

Two shapes are possible and the choice belongs to whoever edits §10.2.

**Option A — extend the export reply.**

```proto
message ExportReply {
  repeated Message messages = 1;
  repeated ExchangeState exchanges = 2;   // added
}

message ExchangeState {
  int64 exchange_id = 1;
  ExchangeStatus status = 2;              // the §6 state machine
  string opened_utc = 3;
  string closed_utc = 4;
}
```

**Option B — a separate `DescribeExchanges(conversation_id)`.**

**Recommendation: Option A.** The console never wants one without the other, and a second call
introduces a window in which the two disagree — the messages from one instant and the states from
the next. It also keeps the existing field number and adds beside it, so an older client that
knows nothing of field 2 continues to work unchanged.

**Why inference is not acceptable.** Deriving state from the messages present cannot distinguish
an orphaned exchange from an open one, and cannot see a ghost at all — a ghost closes the
exchange and the notice goes to the asker. An operator opens a conversation because something
went wrong; rendering an abandoned question identically to an answered one is the most misleading
thing this console could do.

## What does not change

- No addition to `service Agents`. An agent's view is unchanged in every respect.
- No new stored state, no schema migration, no new constraint. Both operations read what §9.2
  already records.
- No credential, workspace path, or launch command crosses in either direction that did not
  already.
- `SurfaceBoundaryTests` should not move: nothing is added to `Broker`.

## Order of work

1. `SPEC.md` §10.2 describes both operations. `Parley.Spec.Tests` must still pass.
2. `proto/parley.proto` gains the messages and the RPCs.
3. `OperatorConsole` gains the two methods; `Parley.Store` gains the queries behind them.
4. `OperatorsService` binds them.
5. Only then does `Parley.Web` consume them.

A pull request that reverses steps 1 and 2 is the defect principle I exists to prevent.
