# Contract: SPEC.md changes

The rule is stated once and the old wording is removed (FR-009).

## §4.0 — *Attaching and detaching are the stream*

Keep "there is no `Detach` and there must not be one". Add one sentence after it: `HandBack` is not a
detach. It returns work; presence is still the stream alone, and an adapter that never calls it loses
nothing.

## §6 — *A receiver that restarts does not lose what it owes*

Replace the paragraph with:

> Work collected by a session that ends before sending its Result is not lost. How it comes back
> depends on how the session ended (§14): a session that hands its work back releases it to the
> receiver's next collection; a process that reconnects reclaims its own; and work held by a session
> that simply stopped goes to the next session that opened after the work was collected. In each case
> it comes ahead of anything new (§7.3), and a session still open keeps its work.

## §7.3 — no change of meaning

"including those delivered to an earlier session of the same agent that ended before sending a
Result" stays; §14 now says which later session.

## §9 — schema

- Add `sessions` and its four triggers (see data-model.md).
- `messages` gains `delivered_session`, `delivered_horizon` and their two checks.
- Remove `agents.session_ref` if tasks confirm nothing reads it.
- The `agents.connected` comment: replace "the next attach releases work somebody is doing" reasoning
  with: the count is derived from `sessions` by trigger; work is released by session, not by count.

## §10.1

- `inbox()`: collected from an attached session, whose identifier the broker issued when it attached;
  a collection names its session, and one that names none is refused.
- Add `hand_back()`: an adapter shutting down normally returns what its session holds.

## §14

- *Re-delivery is not re-sending*: unchanged.
- Add, after the liveness table, a subsection *Sessions and the work they hold*, with the three-row
  table from spec.md (*Why this exists*) and the three rules: hand back, reclaim, newer session. State
  the accepted residual (a newer window collecting during another window's outage) and the accepted
  limit (a crash beside an older window waits for the stall flag), each in one sentence.

## §18

Entry for protocol 0.9 (or the next number if 026 lands first): sessions are named and ordered; a
delivery records its session; `HandBack` and `previous_session` added; give-back moves from attach to
hand-back, reclaim and collection; `exchanges_redelivered` removed.
