# Contracts

## Operators service

`rpc DescribeMessageLengths(Empty) returns (MessageLengthsReply)`. §10.2 `message_lengths()`. Counts and lengths only.

## Agent tools

A successful `parley_request`, `parley_clarify` or `parley_result` whose payload passed 16,000 characters also has:

```json
"long_message": "That message is 40000 characters, past the soft limit of 16000. It was delivered whole."
```

A payload over 512,000 is refused `toolong`, as today, with the new number.

No agent operation reads the statistics.
