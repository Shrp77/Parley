# Quickstart: walking Parley as facilitator

A walk against tools packed and installed into a scratch path, with a root, a store and a port of their own, as
019's and 020's walks were. Clear every `PARLEY_` variable first (020's walk records why: the live catalogue is
otherwise enrolled into).

## 1. A new agent is asked, with nobody asking

1. Name a project in `parley` and leave Auto Accept as it starts (on).
2. Enrol one scripted agent that answers any Request with a fixed, multi-line ownership answer.
3. With no surface open, wait for the agent to register.

**Expect**: within one sweep the agent receives one Request from Parley; its answer is accepted with its line
breaks collapsed; `registry()` shows the line accepted with source `automatic`; the agent is refreshed once it is
idle, and afterwards peers read the line.

## 2. Nothing is asked twice

1. Restart the agent. Restart the broker.

**Expect**: no second question in either case.

## 3. An agent that was running before

1. Stop the broker. Remove the agent's accepted line from the store (the state an upgraded store is in). Start the
   broker with the agent still running.

**Expect**: it is asked shortly after the broker starts, without the agent restarting.

## 4. Auto Accept off

1. Turn Auto Accept off from the terminal, then from the web UI, and back.
2. Ask the agent again.

**Expect**: the answer waits; nothing is written; accepting it from the review records the agent's words accepted
by a person (`proposed`). Ask again and discard the answer: the agent is not asked again on the next tick.

## 5. An agent that asks back

1. A scripted agent that replies with a Clarify.

**Expect**: the question shows as asked back and is flagged; replying from the review sends the reply, and the
transcript shows it as a person's words sent through Parley.

## 6. The facilitator cannot be claimed

1. Start an adapter with `PARLEY_AGENT=parley-facilitator`. Take a seat as `parley-facilitator`. Send a message
   with attribution `parley:facilitator` from an adapter. Send a Request addressed to the facilitator.
2. Enrol a folder named `Parley`.

**Expect**: the four in step 1 refused; the folder enrolled as the ordinary agent `parley`. `agents()` from any
agent does not list Parley.

## 7. An agent on another machine

1. Register an agent whose machine half is not this one.

**Expect**: it is asked, its answer waits whatever Auto Accept says, and the review says why.
