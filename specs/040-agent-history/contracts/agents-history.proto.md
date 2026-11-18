# Contract: the Agents service gains two rpcs

```proto
service Agents {
  // Section 10.1. One exchange of this project, by number, as a record and not as mail. Changes nothing a participant can
  // observe: nothing is marked delivered and nobody is woken. Recorded to the person (section 5.8).
  rpc ReadExchange(ReadExchangeRequest) returns (ReadExchangeReply);

  // Section 10.1. Messages of this project matching words, with a snippet each, newest first. Never a whole message.
  rpc SearchHistory(SearchHistoryRequest) returns (SearchHistoryReply);
}

message ReadExchangeRequest {
  int64 exchange_id = 1;
  int64 after = 2;                 // 0 for the start; else the `next_after` of an earlier reply
}

message HistoryMessage {
  int64 id = 1;
  int64 exchange_id = 2;
  MessageType type = 3;
  string from = 4;
  string to = 5;
  string conversation = 6;
  string created_utc = 7;
  string envelope = 8;             // the only form the words travel in: TrustEnvelope.WrapHistory
}

message ReadExchangeReply {
  enum Found { FOUND = 0; NO_SUCH_EXCHANGE = 1; RATE_LIMITED = 2; }
  Found found = 1;
  int64 exchange_id = 2;
  string conversation = 3;
  string state = 4;                // open | closed, and how it closed
  repeated HistoryMessage messages = 5;
  int64 next_after = 6;            // pass as `after` for the rest; 0 when there is none
  int32 messages_remaining = 7;
  int32 retry_after_seconds = 8;   // RATE_LIMITED
}

message SearchHistoryRequest {
  string words = 1;                // 1..200 characters; whitespace splits it; every word must occur, case-insensitively
  string agent = 2;                // optional: from or to
  string conversation = 3;         // optional
  MessageType type = 4;            // optional; MESSAGE_TYPE_UNSPECIFIED means any
  string since_utc = 5;            // optional
  string until_utc = 6;            // optional
  int64 continue_after = 7;        // 0 for the first page; else a reply's `continue_after`
}

message HistoryMatch {
  int64 exchange_id = 1;
  int64 message_id = 2;
  string conversation = 3;
  string from = 4;
  string to = 5;
  MessageType type = 6;
  string created_utc = 7;
  string snippet = 8;              // at most 240 characters, control characters neutralised
}

message SearchHistoryReply {
  bool refused = 1;                // empty or over-long words, or RATE_LIMITED
  string refusal = 2;
  int32 retry_after_seconds = 3;
  repeated HistoryMatch matches = 4;   // at most 20
  int32 not_shown = 5;
  int64 continue_after = 6;        // 0 when there is no more
}
```

Neither request carries a sender, a project or a recipient. Replies carry no turn information and no field that addresses the caller.

Operators gain no rpc. The existing conversation read returns, with each exchange, its history reads: `repeated HistoryReadRow reads`
(reader, kind, exchange_id, words, matches, created_utc), so the person sees them where they already read the exchange.
