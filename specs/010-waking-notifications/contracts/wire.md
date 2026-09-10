# Wire contract: waking notifications

What moves in `proto/parley.proto`, and what it costs.

## The version

`ProtocolVersion.Current`: **`0.3` → `0.4`**.

`AgentsService` refuses any protocol difference while the major is zero, so this takes every
un-upgraded adapter off the deployment at its next attach. That is §18's intent — "§4.0's `Attach`
refuses the pairing rather than discovering it later" — not a side effect to be worked around.

**The bump is caused by the behaviour, not by the field.** Adding a field to a reply is
protobuf-additive and would interoperate on its own. A broker that starts an agent where it
previously did not is observable by a second implementation, which is §18's test, and a protocol
with no patch level has no slot for a compatible change.

## `NotifyReply`

```proto
message NotifyReply {
  bool ok = 1;
  NotifyRejection refusal = 2;
  bool deduplicated = 3;
  WakeOutcome wake = 4;   // new
}
```

Field 4 is new; 1 to 3 are untouched. An older client reading a newer reply ignores it; a newer
client reading an older reply reads `WAKE_OUTCOME_UNSPECIFIED`. Neither pairing occurs in practice,
because the version check refuses it first — but the field is numbered as though it might, because
a wire contract that relies on a check elsewhere is one field-reuse away from a silent
misinterpretation.

## `WakeOutcome`

```proto
enum WakeOutcome {
  WAKE_OUTCOME_UNSPECIFIED = 0;
  WAKE_OUTCOME_STARTED = 1;          // a start was asked of the recipient's node
  WAKE_OUTCOME_ALREADY_RUNNING = 2;  // attached; a turn was asked for
  WAKE_OUTCOME_ALREADY_COMING = 3;   // attention was already pending; nothing further asked
  WAKE_OUTCOME_UNREACHABLE = 4;      // no node for that machine, or it cannot start the role
}
```

**An enum rather than a sentence**, on the constitution's principle IV: a field that can hold a
paragraph eventually holds one, and everything crossing between agents is charged against the
receiving agent's working memory. The wire carries the fact; the words belong to the surface.

**`UNREACHABLE` is not a refusal.** `ok` stays true and the message is stored. FR-004 and FR-013:
§17 question 1 requires that whatever bounds this "must not make an event undeliverable", and a
recipient nothing can start is a fact about the deployment rather than a rejected send. A surface
that rendered this as a failure would tell somebody their contract change had not been sent when it
had.

## What does not change

- **No new RPC.** The All-agents broadcast is N ordinary `Notify` calls from the surface. §5.5's
  precedent, and its argument applies more strongly here — see `research.md` R6.
- **`NotifyRequest`** is untouched.
- **`NotifyRejection`** is untouched. The wake outcome is not a rejection and shares nothing with
  one.
- **No operator operation.** Everything here is on `Agents`. `Operators` gains nothing, which
  `SurfaceBoundaryTests` will hold to.
- **No schema.** Nothing new is stored.

## §18 entry

SPEC.md gains, above `### 0.3 — the message cap`:

> ### 0.4 — a notification wakes its recipient
>
> **A `notify` now causes its recipient to be started, as a question does.** 0.3 and earlier
> delivered it only to an agent that was already attached, so a notification to a closed session was
> stored and read whenever somebody next opened the repository — which for a repository nobody opens
> is never. §5.4 carries the rule and §17 question 15 carries the reversal.
>
> **Bounded by coalescing rather than by a cap.** Attention is asked for once per recipient until
> that recipient drains, so a hundred notifications cost one turn — which is the correct number,
> because a drain collects everything pending. `NotifyReply` gains `wake`, saying what happened to
> the recipient, because a sender that was told only "stored" could not tell delivery from silence.
