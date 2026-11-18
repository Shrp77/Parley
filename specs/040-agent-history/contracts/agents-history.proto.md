# Contract: the Agents service gains two rpcs

The authoritative definition is `proto/parley.proto`; this is what it says in words. The calls name the caller as `asking_as`, as
every call on the service does (SPEC.md section 16's concession: identity is the launch's, a label on the wire, never authority).

```proto
rpc ReadExchange(ReadExchangeRequest) returns (ReadExchangeReply);
rpc SearchHistory(SearchHistoryRequest) returns (SearchHistoryReply);

message ReadExchangeRequest { string asking_as = 1; int64 exchange_id = 2; int64 after = 3; }
message ReadExchangeReply {
  enum Found { FOUND_UNSET = 0; FOUND = 1; NO_SUCH_EXCHANGE = 2; RATE_LIMITED = 3; }
  Found found = 1; int64 exchange_id = 2; string conversation = 3; string state = 4;
  repeated HistoryMessage messages = 5; int64 next_after = 6; int32 messages_remaining = 7; int32 retry_after_seconds = 8;
}
message HistoryMessage { int64 id = 1; int64 exchange_id = 2; MessageType type = 3; string from = 4; string to = 5;
  string conversation = 6; string created_utc = 7; string envelope = 8; }

message SearchHistoryRequest { string asking_as = 1; string words = 2; string agent = 3; string conversation = 4;
  MessageType type = 5; string since_utc = 6; string until_utc = 7; int64 continue_after = 8; }
message SearchHistoryReply { string refusal = 1; int32 retry_after_seconds = 2; repeated HistoryMatch matches = 3;
  int32 not_shown = 4; int64 continue_after = 5; }
message HistoryMatch { int64 exchange_id = 1; int64 message_id = 2; string conversation = 3; string from = 4; string to = 5;
  MessageType type = 6; string created_utc = 7; string snippet = 8; }
```

Replies carry no turn information and no field that addresses the caller, and none reaches `HistoryReadRow`, which lives only on the
operator's `ExportReply.reads` (`AgentsNeverSeeWhoReadTests`). Bounds: a reply about 64,000 characters and never fewer than one
message; a page of at most 20 matches; a snippet at most 240 characters after neutralising; words at most 200 characters and eight
different words; a rate of 30 calls and 400,000 characters a minute, a search charged 20,000.

Operators gain no rpc. The existing conversation read returns, for the person only, `repeated HistoryReadRow reads = 11`: for each
reader of each exchange in the conversation, its reader, the exchange, when it last read and how often (`count`); and for each agent's
newest search among the project's, its reader, words, matches, when, and how many searches it has made in all (`count`).
