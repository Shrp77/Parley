# Contract: every refusal

Each row is a state this feature must refuse, the wording it is refused in, and the requirement or
recorded defect that puts it here. **A refusal names what replaced it or what to do instead** — the
check twelve tests in 019 were pinned to, and the reason a removal is cheap and a replacement is not.

## Asking

> **Superseded in part.** Since 0.35.0 (022) the ask is Parley's, not the person's: the facilitator asks
> each attached agent once, and a person who wants it asked again uses `ask_again`, which Parley sends.
> SPEC.md §10.2 is the authority for asking now. Two rows below are marked where they stopped being true.

| State | Answer |
|---|---|
| The agent is not registered | `Nothing has registered under 'orders@test', so nothing can be addressed to it. §14 counts an unregistered name unroutable, so this is not a quiet agent — it is an unaddressable one. Start it from the agents pane and it registers as it attaches.` |
| The agent is retired | `'orders@test' is retired, so it receives nothing. Reinstate it first if it should answer.` |
| The agent is registered but nothing is attached | *Planned:* `'orders@test' is registered and nothing is connected to it. Asking will start it, which costs a turn. Ask anyway?` — **not a refusal**; FR-005 covers unreachable, and a startable agent is reachable. **Superseded (0.35.0).** SPEC.md §10.2: Parley asks only an agent that is attached, and *"never starts an agent in order to ask it"*; an agent not attached is asked when it next registers or reattaches. Only `ask_again` asks outside that, because a person *"chooses to spend its turn"*: the question is sent, and the agent is woken the way any Request's recipient is |
| The broker cannot be asked | `Could not ask the broker whether 'orders@test' can be reached, so this is not being sent. That is not the same as the agent being absent.` — 0.20.1's rule: a guard that fails open says so |
| The person holds no seat | *Planned:* `Asking is sending a message, and a message is sent by somebody. Take a seat and this is sent as that name.` — offered, never hidden (FR/edge case, and 0.28.2's finding that hiding the action was the defect). **Gone.** Since 0.35.0 the ask is Parley's and needs no seat (§10.2 `ask_again`: *"Needs no seat, because the facilitator sends it"*), and since 0.36.0 (023) the person always has a seat, so there is no state left to refuse |
| The agent has not answered yet | `'orders@test' has not answered yet.` — **never** *declined*. AC US1.5, and 008's rule: a timeout says "I do not know", never "it did not happen" |

## Accepting

| State | Answer |
|---|---|
| The statement carries a character the registry refuses | `That statement cannot be recorded: it contains a character that would let a roster entry break its own line, hide itself, or reorder what follows. Every agent reads that list. The character is at position 34.` — FR-022, FR-025. **Refused where the operator sees it and can act; never silently trimmed** |
| The statement is empty after trimming | `An empty line is not a statement of ownership. Decline instead, and the existing line stays as it is.` — FR-016 is the honest route to "no line" |
| The statement is longer than a bound the deployment reinstated | `That statement is 412 characters and this deployment caps them at 300. Shorten it, or raise the cap.` — FR-024, FR-025. Names the number in force, never the sentinel that means none, on 0.25.8's precedent |
| **The agent's repository is not on this machine** | `'orders@server-b' runs on server-b and its launch configuration is a file there. This machine cannot write it. Accept it from a surface running on server-b.` — `research.md` R3, and the rule is the same for the terminal and the web UI |
| The workspace the registry names is not there any more | `The registry says 'orders@test' runs in C:\work\orders, and there is no repository at that path. Nothing has been written. Re-enrol it if it moved.` — FR-021 |
| The launch configuration names a different agent than expected | `C:\work\orders\.mcp.json configures 'billing@test' under that key, not 'orders@test'. Nothing has been written.` — **005's gate finding, reached from the other side**: a withdrawal once deleted a launch configuration belonging to a different agent, because the workspace came from the registry and may be long out of date. The name is read back before anything is written |
| The launch configuration cannot be written | `Could not write C:\work\orders\.mcp.json: access is denied. Nothing has been changed.` — FR-021, naming the file, and **nothing partly written**: composed, then committed by rename, which is the rule 019 had to restore after a draft lost it |
| The agent was retired between the ask and the acceptance | `'orders@test' was retired while this was being discussed. The conversation is still readable; the line has not been written.` — FR/edge case. The answer survives; the write does not |
| Two acceptances race into one launch configuration | The file is written under the same exclusive hold `Parley.Setup` already takes, and the loser is told: `Somebody else wrote this repository's launch configuration while this was open. Read it again before accepting.` — FR/edge case |

## What is never refused

Written down because each is a thing a reader might expect to be refused, and refusing it would be
wrong:

- **A statement that overlaps another agent's.** FR-030 and Principle II. Both claims are shown, neither
  is refused, and nothing arbitrates. US4.3 says so in as many words.
- **A gap nothing claims.** Shown, not refused. There is no requirement that a deployment be covered.
- **An answer that is not a statement of ownership.** A question, an apology, a refusal to answer — all
  are legitimate messages. A person reads what came back.
- **An agent with no line at all.** FR-031. It is listed without one, as it is today.
- **A second ask to an agent already asked.** Ordinary concurrent exchanges, which the protocol already
  bounds. What is refused is two accepted lines racing into one file, above.

## What must not be said

- Never **`reachable`** about a broker's port — 019's FR-023 forbids the word, and `ReachabilityIsTwoFactsTests`
  holds it. Whether an agent answered and who can address it are two facts.
- Never the integer that spells *no cap*. `Caps in force: none`, on 0.25.8's precedent: printing
  `2147483647` tells somebody a bound was set at a value nobody chose.
- Never `seen` for an agent's answer. It was **collected** or it was not — 0.25.3's rule; the broker
  handing a message over is not evidence of what the model did with it.
- Never a count of characters a person wrote, attributed to the agent. `research.md` R4.
