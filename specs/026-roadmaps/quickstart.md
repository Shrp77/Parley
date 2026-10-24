# Quickstart: walking roadmaps by hand

Walked like 019 to 025 are: tools packed and installed into a scratch path, a broker on its own root and port, with every `PARLEY_` variable cleared first (018's incident: an ambient `PARLEY_NODE_CONFIG` wrote junk into a live catalogue). Not to be run against the live deployment.

## 1. Room lists everyone (story 5)

1. A project with four agents attached. Two exchange messages in one conversation.
2. Open the conversation in the terminal and on the web. **Expect** four agents, two under *In this conversation* with the participant mark, two under *Elsewhere in the project*, the legend present.
3. Tick one of the two non-participants, type a line, send. **Expect** the line reaches them and they now sit under *In this conversation*.
4. Retire an agent that took part. **Expect** it stays listed, marked retired, and cannot be ticked. Retire one that did not. **Expect** it disappears.
5. Narrow the terminal to 40 columns. **Expect** no row cut off; marks and names intact.

## 2. A list becomes a roadmap (story 1)

1. As an agent, call `parley_propose_roadmap` with three items, one naming an intended agent and one depending on item 1.
2. **Expect** nothing in any transcript; the project shows one proposal waiting on the person, on both surfaces and `parley roadmap proposals`.
3. Reword item 2, remove item 3, confirm. **Expect** a roadmap with two items, origin `edited`, the proposer named, the proposal no longer waiting, a notice in the proposer's inbox saying confirmed.
4. Propose again and decline. **Expect** no roadmap, a *declined* notice, and no wake of the agent.
5. As an agent, try anything that changes the roadmap. **Expect** there is nothing to call; the structural test is the proof.
6. Two proposals against one roadmap; confirm one. **Expect** the other reads superseded.

## 3. State and next (story 2)

1. Roadmap with four items. Start item 1 and let the agent answer `done`. Start item 2 and have the agent answer `blocked` with blocker `permission`. Leave item 3 unstarted. Have an agent ask the person a question inside item 4's conversation.
2. **Expect** done, blocked (naming permission), not started, waiting on the person, and *next* naming item 3 once items it needs are done.
3. Stop the broker's read halfway (point a surface at a dead endpoint). **Expect** *could not be read* and never *not started*.

## 4. Instruct and navigate (stories 3 and 4)

1. From item 3 press the start key. **Expect** its conversation opens with the item's words in the box and only the item's own agent ticked, and nothing is sent. Add a sentence and send.
2. **Expect** a conversation `rm<n>-<m>` now exists, the agent holds a request with the item's text, the item reads in progress, the transcript shows the person's words.
3. Start item 3 again. **Expect** the same conversation opens with what was said in it, and nothing is sent.
4. From that conversation ask for its roadmap. **Expect** the roadmap opens on item 3. Open an ordinary conversation. **Expect** it names no item and offers no way to a roadmap.
5. As an agent, try to open `rm<n>-<m>` for an unstarted item, by a request and by placing a file. **Expect** both refused, saying the conversation is not one the agent can start.
6. Edit the roadmap and remove item 3. **Expect** its conversation remains, marked as formerly belonging to it.
7. Start an item whose text is over 8,000 characters. **Expect** refusal naming the cap and nothing sent.
8. Restart the broker. **Expect** roadmaps intact.
