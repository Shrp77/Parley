# Research: Waking notifications

Decisions, and which of them came from reading the code rather than reasoning about it. The ones
marked **read** would have been got wrong by thinking carefully, and one of them was.

---

## R1 — The bound must count turns, not processes *(read)*

**Decision**: the coalescing marker exists to prevent duplicate *turns*. Preventing duplicate
processes is already done elsewhere and is not the cost.

**Read rather than reasoned.** `NodeCommand.StartAsync`:

```
if (residents.TryGetValue(agent.Value, out var existing) && existing.IsAlive)
{
    // Already here. The broker checks before asking, but the check and the command are
    // not one operation -- so this is the race, and losing it must not produce a second
    // process for an agent that needed one.
    DispatchTurn(existing, output, cancellationToken);
    return;
}
```

The node refuses a second process and **gives a turn instead**. So sixty start requests against a
live resident are sixty turns, and a design that bounded processes would have reported success
while bounding nothing.

**This corrected the specification.** Its first draft keyed the marker on the recipient *attaching*.
That fails on the same evidence: the first notification starts the agent, it attaches within a
second or two, and the remaining fifty-nine find it attached and each ask for a turn. The marker is
keyed on the **drain** instead — see R2.

**Alternative rejected**: putting the bound in the node. It cannot be there. The node does not know
whether a command originated in a notification or a question, and must not learn: that would put a
protocol decision inside the thing that launches processes, which is the boundary
`src/Parley.Broker` exists to keep.

---

## R2 — The marker is cleared by a drain, not by an attach

**Decision**: pending attention is satisfied when the recipient drains its inbox.

**Rationale**: §4.2 already draws this distinction — waking an adapter is not the same as the model
taking a turn — and the whole justification for the bound is question 1's observation that `inbox`
"collects everything pending". The drain *is* the event that makes further wakes pointless. Nothing
else is.

It also handles the case an attach-keyed marker gets dangerously wrong: an agent that attaches and
dies before draining has read nothing, and the marker must not have been cleared by its arrival.

`AgentsService.DrainInbox` is the single place a collection is observable, which is what makes this
one line rather than a rule spread across the server.

---

## R3 — Per recipient, across all senders

**Decision**: the marker is keyed on the recipient alone.

**Rationale**: the recipient is the party that pays. A per-pair key leaves an agent wakeable once
per peer, so a deployment of twenty agents can hand one recipient twenty turns for twenty
notifications that a single turn would have collected together. It also has no advantage: the
broadcast case (one sender, N recipients) costs N turns under either key, because the recipients
differ, which is what was asked for.

**Alternative rejected**: per (sender, recipient). Bounds the one shape that is least interesting —
a single peer looping — and misses the shape that scales with deployment size.

---

## R4 — The ceiling is two minutes, and provisional

**Decision**: a pending marker expires after **two minutes**, as a named constant on
`BrokerRuntime` with the reasoning beside it.

**Rationale**: it is a failure valve, not the mechanism. It exists only for the wake that never
produces a drain — no node, a launch that fails, a process that dies on start-up. Its value must
comfortably exceed cold-start-to-drain on a slow machine: process launch, an MCP server connecting,
an adapter attaching, a model taking its first turn.

**The error is asymmetric, so it errs long.** Too long delays a retry of something that has already
failed once. Too short reintroduces exactly the duplicate turns the feature exists to prevent, on
the machines slowest to start — the ones least able to afford them.

**Provisional on the record, like the message cap.** §17 question 16 set rule 8.5's number from
`parley refusals` rather than from judgement, and CLAUDE.md records that as the first value here set
from evidence. The same route exists for this: `invocations` already records when starts happened
and how long turns took, so the distribution of start-to-first-drain is measurable on a real
deployment. It moves on that, not because a case felt slow.

**Not a schema fact, and the contrast is the argument.** §13.1's sixty-second window is in the
schema because it decides the meaning of buckets already stored, so changing it changes what stored
rows mean and should cost a migration. This one governs no stored row; changing it invalidates
nothing that exists. A migration would be ceremony, and the constitution's principle V is about
invariants application code can route around — this is not one.

---

## R5 — Every wake sets the marker; only a notification consults it

**Decision**: `Ask`, `Clarify` and `Answer` keep exactly today's behaviour and additionally mark
attention pending. `Notify` is the only caller that declines to ask when the marker is set.

**Rationale**: §17 question 15 states what bounds turns today — "the volume is held by §7.1's
per-pair question cap" — so questions are already bounded and need no second control. Suppressing a
question's turn would be a behaviour change nobody asked for, on the one path that carries a debt
and a deadline.

The asymmetry matters in one direction and is the point: a notification arriving while a question's
turn is already coming buys nothing, because that turn will drain it. Without this, the common case
of "ask a peer, then tell it something" costs two turns for one reading.

---

## R6 — The broadcast is not a protocol operation *(read)*

**Decision**: All agents sends N ordinary notifications from the surface. No wire operation, no
broker operation, no message type.

**Read rather than reasoned**: `proto/parley.proto` has `AskRole` and no `NotifyRole`, and §5.5
explains why the one that exists is shaped the way it is —

> Not a new kind of message and not a new state machine: N Questions, N exchanges, each admitted,
> clarified, answered, orphaned and ghosted exactly as it would have been on its own.

and gives the restraint's reason: a one-to-many exchange "would need its own admission rules, its
own cap arithmetic, its own definition of concluded". **Every one of those costs is absent here**,
because a notification opens no exchange, takes no slot and creates no debt. So the argument for
restraint is strictly stronger for Notify than for the case that already took it.

**Alternative rejected**: a `NotifyRole` or `NotifyAll` on the wire. It would add a second fan-out
mechanism to a protocol that already has one and does not need it, and would put the roster
resolution — which is a surface's question about which rows it is showing — inside the broker.

---

## R7 — What the sender is told is a four-way outcome, and it is not a failure

**Decision**: `NotifyReply` gains an enum: started, already running, already coming, or nothing here
can start it. The message is stored on every one of them.

**Rationale**: the reported defect had two halves and only one was the wake. The surface said
"Sent.", which was true, and the person reasonably concluded a peer was reading it. §17 question 1
is explicit that a bound "must not make an event undeliverable", so the outcome is information about
the recipient rather than a rejection — FR-013.

An enum rather than a sentence, because of the constitution's principle IV: a new field that can
hold a paragraph will eventually hold one. The words are the surface's; the wire carries the fact.

---

## R8 — Protocol 0.4, and the deployment cost is the plan's headline *(read)*

**Decision**: `ProtocolVersion.Current` moves from `0.3` to `0.4`; package version to `0.23.0`.

**Read rather than reasoned**: `AgentsService` refuses any protocol difference while the major is
zero, and §18 says two implementations whose minors differ "do not interoperate, and §4.0's
`Attach` refuses the pairing rather than discovering it later". So the bump is not bookkeeping — it
takes every un-upgraded agent off the deployment.

**Why it cannot be avoided.** §18's test for what moves the number is whether a second
implementation could observe the change. An agent that gets started where it previously was not is
observable. And §18 gives a protocol no patch level, deliberately: "either a change is visible
across the boundary or is not a protocol change at all". There is no compatible-change slot to use.

**What follows**: this release carries the wire change and nothing else that might need reverting,
on the reasoning CLAUDE.md records from 008 — a release that changes a versioned wire contract and
something an operator's scheduled task depends on cannot be reverted in halves.

---

## R9 — `notify_volume` does not change

**Decision**: no new table, no new column.

**Rationale**: `notify_volume` counts messages and the new cost is turns. `invocations` already
records every turn with its duration, its outcome and its cost. So the evidence §17 question 1 now
needs — what a notification costs the recipient — exists already, in the place that was built to
hold it. Adding a counter would be a second answer to a question already answered.

§17 question 1 gains a note saying where to look, because a reader of that question today would not
know the cost had changed.

---

## R10 — `parley fleet` is out of scope, and the finding is recorded rather than acted on

**Read**: `AgentFleet.TickAsync` is called from exactly one place, `parley fleet`, which is its own
long-running command. The autostart entry runs `serve`, and `parley node` never sweeps. So on a
deployment configured the documented way, **nothing runs the sweep** that would eventually pick up
an uncollected notification.

That is why the reported defect was permanent rather than merely slow: both doors were shut, one by
§17 question 15's decision and one because the sweeper is not part of the installed configuration.

This feature opens the first door. Whether the second should be opened — whether `parley fleet`
belongs in the autostart entry — is a separate decision about unattended operation and is not taken
here. Recorded in the spec's out-of-scope section so it is not lost.
