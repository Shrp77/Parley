# Contract: the one new operation

> **RENAMED WHILE BUILDING IT, 2026-09-26: the operation is `record_ownership`, not
> `accept_ownership`.** `AcceptOwnershipAsync` sits one reading away from the agent-facing
> `IAcceptsOperatorInstructions.AcceptAsync`, and §10.2 opens by warning about exactly that — two
> operations across that boundary whose names invite confusion, which is why `force_conclude` is not
> `conclude` and `registry` is not `agents`. The guards pass either way (`ContractSeparationTests`
> compares names exactly), so this is the convention rather than the build.
>
> **And the new name is the more accurate one**, which is what settled it: the person did the
> accepting. An operation called `accept_ownership` names the wrong actor for something that only
> writes down a decision somebody else made — and it does not even write `owns`. Every occurrence
> below should be read as `record_ownership` / `RecordOwnershipAsync`.

§10.2 gains **one** operator operation. Nothing is added to §10.1, and no agent-facing operation is
touched — FR-029 and Principle III.

## Where it lives, and why that is not a formality

| Side | This operation |
|---|---|
| `Parley.Core` | on `IParleyBrokerClient` (§10.2's contract), **not** on `IAcceptsOperatorInstructions` |
| In process | on `OperatorConsole`, **not** on `Broker`. `SurfaceBoundaryTests` enumerates the broker's public surface and fails when it grows, which makes this a decision about which side it belongs on rather than a question of which class was nearer |
| On the wire | on the `Operators` service, **not** `Agents`. An adapter binds `Agents` and therefore has nothing to bind by accident |

That split is made twice on purpose, and CLAUDE.md records why the type split alone is thin: it depends
on whoever writes the adapter reading carefully.

## `Operators.AcceptOwnership`

```proto
message AcceptOwnershipRequest {
  string agent            = 1;  // the whole name, role@machine
  string statement        = 2;  // what the person agreed to
  Source source           = 3;  // proposed | edited | composed
  int32  proposed_length  = 4;  // the AGENT'S message length; 0 with composed
  string conversation_id  = 5;  // the discussion; empty with composed
}

message AcceptOwnershipReply {
  bool   accepted         = 1;
  string problem          = 2;  // why not, in the operator's words
  string live_statement   = 3;  // what peers read RIGHT NOW, for the pending comparison
  bool   pending          = 4;  // live_statement differs from statement
}
```

### What it does

Records the acceptance in `agent_ownership_accepted`: the statement, which of the three ways it was
arrived at, how long the agent's own proposal was, and the conversation that produced it.

### What it does not do, and this is the part that matters

**It does not write `agents.owns`.** FR-018 keeps the registry filled from the launch environment on
attach and nothing else, so this operation records a *decision* and the launch configuration remains the
only source of the line peers read. A broker able to write that field would be a route around Principle
III that no adapter had to cooperate with.

**It does not write the launch configuration either.** That is `Parley.Setup`, on the machine holding the
repository, and it is a separate act by the surface — which is why `research.md` R3's machine check is
the surface's and not the broker's. The broker does not know where a repository is; it knows what an
agent said about itself, which 005's gate finding established may be long out of date.

**So an acceptance is two acts that can half-fail, and the order is chosen for which half is
recoverable.** The file is written **first** and the record **second**: a file written with no record
leaves the line live and its provenance unrecorded, which a later ask repairs; a record with no file
would claim a person accepted a line that peers will never read, and nothing repairs a wrong record.
The surface reports which halves succeeded.

## What every surface must bind

`DriftTests` compares every operation on `BrokerClient` against both surface trees and allows no
exemption, so the terminal and the web UI both bind this. Per the owner's decision of 2026-09-26 both
also write the launch configuration, and both apply the machine check identically.

## Reads this feature needs and does not add

Each of these already exists and is already bound by both surfaces:

| Need | Existing read |
|---|---|
| The line an agent currently holds (FR-014) | the roster, which carries `owns` |
| The other agents' lines (FR-017, US4) | the same roster, filtered to the project |
| Finding the discussion (FR-011) | `ListConversations` and `Export`, whose identifiers 003 specified |
| Whether the line is pending (FR-020) | the roster's `owns` against `agent_ownership_accepted`, returned together |
| Restarting the agent (FR-019) | `Operators.Start`, which 006 established may launch a catalogued role |

**One operation, five existing reads.** That ratio is the evidence that FR-002's "no new operation for
the ask" was achievable rather than aspirational.
