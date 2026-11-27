# Lens 1: correctness and concurrency

Part of [the review gate](README.md).

## What is medium or above

- A wrong result, a lost or doubled message, a record that says what did not happen, or state left wrong for longer
  than the next act repairs.
- A race that a probe can make happen, even rarely, when its effect is any of the above.
- A test that passes for the wrong reason, or a fix that leaves the same class of fault open nearby.

Low: a wrong word, a self-healing glitch measured in microseconds, a cost only in bookkeeping, a gap that needs two
independent faults at once.

## How to probe

- **Make it fail.** Loop the race many times (hundreds of rounds), pipeline it, or release both sides on one barrier.
  A race that never shows in a loop is low and says how many rounds were tried.
- **Poll, never sleep a fixed time.** A slow runner breaks fixed waits; wait on the state itself, with a deadline.
- **Terminal tests**: a key whose work opens a view must be pressed with `PressThenSettleAsync`, never `Press` then a
  wait, because `Press` draws while the work still changes the views ("Collection was modified" in the line canvas).
- **Compare by value with care.** A dictionary removal that should take only the entry read is
  `TryRemove(KeyValuePair)`, not `TryRemove(key)`.
- **Same commit, different CI result is a flake**, but a flake has twice been a real bug here: re-run it and find
  the cause before calling it noise.

## Settled by the owner

- **Nothing times out.** Work that stops is flagged to the person; a conversation ends by consensus or by a person.
- **The person may be the agent a roadmap item is for; Parley may not.** Starting a person's own item ticks nobody.
- The lows triaged in the ledger's 2026-10-29 entry as moot or by design stay that way.
- **Work a session collected and left unanswered is given back when that session ends only by feature 027.** Today it
  comes back only when no other session of the agent is attached (on main since #27). SPEC §6 promises more, and
  `specs/027-session-owned-delivery/` is the fix; until it lands this is known, not a finding (owner, 2026-10-30).
