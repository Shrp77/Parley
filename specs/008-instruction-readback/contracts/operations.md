# Contract: the readback, and what changes around it

## The wire

### New — `Operators.Instructions`

On `Operators` only. `Agents` gains nothing, and that is checked rather than intended: the existing
wire-separation test enumerates the operator client's methods and asserts none appears on the agent
client.

```protobuf
message InstructionsRequest {
  string agent = 1;            // optional; empty means every agent
  int64 instruction_id = 2;    // optional; > 0 narrows to exactly this one
  int32 page_size = 3;         // a request, not an instruction; the broker bounds it
  string page_token = 4;       // opaque; from a previous reply
}

message InstructionRecord {
  int64 id = 1;
  string agent = 2;
  string text = 3;
  InstructionOutcome outcome = 4;
  string reply = 5;            // empty unless settled
  string issued_utc = 6;
  string settled_utc = 7;      // empty while pending
}

message InstructionsReply {
  repeated InstructionRecord instructions = 1;
  string next_page_token = 2;  // empty when there is no next page
}
```

**Why one operation and not two.** "Get one" and "list some" differ only by a filter. Two operations
would be two paths to the same rows that can disagree about what a row looks like — and they would
disagree eventually, because only one of them would be the one anybody edited.

**Why the token is opaque** even though instructions have a monotonic identifier and could carry a
plain cursor: `ListConversations` already records the reason — a token whose shape a client parsed
is a shape the broker could not change. Instructions need less than conversations do (which need an
instant *and* an identifier to break ties on equal instants), and inventing a second, more
transparent convention for the simpler case buys nothing and costs a rule.

**Why `page_size` is a request.** §10.2 makes rule 8.5's argument one level up: an unbounded
operator query is the mistake the message cap prevents, and a caller's number is a caller's number.
The broker clamps, exactly as `ListConversations` does.

**Refusals.** An `agent` that is not a well-formed name is refused with `InvalidArgument`, not
ignored. Dropping an unparseable filter answers a narrow question with a wide result and the caller
cannot tell the difference: an empty list invites a second look, a full one reads as the answer.

### Changed — `Operators.Instruct`

```protobuf
message InstructReply {
  InstructionOutcome outcome = 1;
  string reply = 2;
  int64 instruction_id = 3;   // NEW
}
```

**Additive only.** The field number is new, so old clients ignore it and new clients reading an old
broker see zero — which is honest, since an old broker has nothing to read back anyway.

**`INSTRUCTION_OUTCOME_PENDING` becomes reachable.** The value has always been in the enum and was
never produced. It is now what `instruct` returns when the broker's wait elapses with the turn still
running.

**What `instruct` no longer does**: settle the record because it stopped waiting.

## The client

`IParleyBrokerClient` and `BrokerClient` gain one method:

```csharp
Task<InstructionPage> InstructionsAsync(
    AgentName? agent = null,
    long? instructionId = null,
    int pageSize = 0,
    string? pageToken = null,
    CancellationToken cancellationToken = default);
```

`InstructionRecord` and `InstructionPage` are new records in `Parley.Core`, beside the existing
`InstructionResult`.

`InstructionResult` gains the identifier so a caller told *pending* knows where to come back to.

## The store

`InstructionRepository` gains:

| Method | Purpose |
|---|---|
| `ListAsync` | The readback: optional agent, optional identifier, bounded page, newest first |
| `SweepAsync` | Settles every pending instruction issued before an instant as failed |
| `SettleForMachineAsync` | Settles every pending instruction for agents on one machine as failed |

`GetAsync` stays. Narrowing by identifier is a genuine single-row read and expressing it as a
one-element list would be worse for both callers.

`Pruner` gains its own delete statement for instructions — **not** a clause on the messages one,
because the message delete carries three floors about exchanges, deduplication and undelivered
inboxes, none of which mean anything here. Sharing the statement would carry predicates that are
always true and invite a later reader to think they matter.

## The command line

```
parley instructions [<agent>] [--limit <n>]
```

Prints identifier, agent, outcome, when issued, when settled, and the agent's reply. The same shape
as `parley open` and `parley waiting`, and what somebody diagnosing a deployment over SSH reaches
for.

**It prints the instruction text and the reply, unlike `parley open`** which deliberately prints not
one word of any payload. The difference is whose words they are: `open` lists conversations between
agents, and an operator reading peer traffic through a listing is the disclosure §10.1 withholds.
An instruction is the operator's own words to their own agent, and its reply is that agent
answering the operator directly.

## Not yet bound by either surface

*(Empty. `parley instructions` binds the readback in step 1, so `DriftTests` is satisfied by a real
surface rather than by an exemption. Step 2 adds the console and the terminal.)*

## What is unchanged and checked to be

| Thing | Why it must not move |
|---|---|
| `Agents` service | An agent gaining any instruction operation breaches §10.1. SC-006. |
| `Parley.Mcp` | Does not reference the broker and must not learn to. |
| First-come settlement | A late report overwriting a settled record would change an operator's history after they had read it. |
| §11.5's obligation | An adapter must still report what became of an instruction and return whatever the agent said. Silence is still not an outcome. |
| The 8,000-character cap on instruction text | Schema-enforced; this feature does not move it and does not add a second number for replies. |
