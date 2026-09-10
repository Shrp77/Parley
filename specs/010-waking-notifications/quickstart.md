# Quickstart: verifying waking notifications on a live deployment

What cannot be proved in the suite: that a real agent, in a real repository, starts because
somebody told it something.

**Read the upgrade section first.** This release breaks the wire, so a partial upgrade leaves a
deployment where nothing attaches.

## Upgrading

**The broker and every agent move together.** `ProtocolVersion` goes to `0.4`, and an adapter
speaking `0.3` is refused at attach — deliberately, so the mismatch is reported rather than
discovered as questions that are never answered.

1. Install the tools: `.\install.ps1`. It asks before stopping the broker and names the
   conversations with an exchange still open.
2. **Every enrolled repository's adapter is the same `parley-mcp` binary from the same install**, so
   step 1 upgrades them. What it does not do is restart sessions already attached — those hold a
   `0.3` connection until they end.
3. Confirm: `parley autostart status` names every project, its store and its catalogue.

**If something is still on 0.3** the broker's log says so by name at the refused attach. That line
is the intended diagnosis; an agent that has silently stopped answering is not.

## Verifying the wake — the reported case

The one that produced this feature.

1. Pick a catalogued agent with no session open. `parley autostart status` names what this machine
   can start.
2. In `parley top`: take your seat, focus the conversations list, press `t`, choose the agent, and
   send anything.
3. **Expected**: the sentence says the agent is being started — not "Sent." The broker's log shows
   `<agent>: started (… told it something).` The agent's process appears. It drains.

**Before this change** the sentence was "Sent." and nothing else happened at all, which is the
whole defect.

## Verifying the bound — the part that must not be taken on trust

The coalescing is what makes the wake shippable, so it is worth seeing rather than assuming.

1. With the same agent idle, send **ten** notifications as fast as the surface allows.
2. **Expected**: one `started` line in the broker's log, one turn in `parley invocations` (or
   whatever the tasks settle as the reader), and all ten messages collected by that turn.
3. **Not expected**: ten start lines, or one start line and nine turns. Either is the bug this
   feature is bounded against, and either should fail the suite first — if it reaches here, a test
   is missing rather than merely a bound.

Then the other direction, which is the failure valve:

4. Send a notification to an agent this machine **cannot** start — one registered on another
   machine, or a role with no launch command.
5. **Expected**: the sentence says the message is waiting and nothing here will start the
   recipient. The message is stored. It is **not** reported as a failure.

## Verifying All agents

1. In the composer, choose **All agents** instead of a peer.
2. **Expected before sending**: the surface names how many agents this will reach.
3. **Expected after**: counts — how many were told, how many were started — and any recipient
   nothing could start named rather than folded into a number.
4. **Check nobody's seat received one**, including your own. Take a second seat in another window
   if there is only one.

## What this cannot verify here

- **macOS.** CI is the only place it runs and jobs have been billing-refused. Unverified, and said
  so rather than assumed.
- **Two machines.** Every check above is one machine, so `UNREACHABLE` is exercised by an
  uncatalogued role rather than by a genuinely remote agent. The remote case differs in the broker
  having no node at all for that machine, which the suite covers and this does not.
