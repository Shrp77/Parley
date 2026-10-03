# Quickstart walk: The person always has a seat

Run against tools packed and installed into a scratch path, with a root, a store and a port of their own, and
every `PARLEY_` variable cleared first. 020's walk records why: a walk that inherits `PARLEY_NODE_CONFIG` writes
into the live deployment's catalogue. `walk.ps1` goes beside this file, as 019's and 022's do.

## 1. The seat exists before anybody looks

1. Name a project `alpha` and start the broker. Open no surface.
2. `parley registry --project alpha`.
   **Expect** a row `human-user@alpha`, adapter `parley-console`, shown as `away`, and the facilitator as
   `parley-facilitator@alpha`.
3. Restart the broker. **Expect** the same rows, not second ones.
4. Ask the broker to describe a machine called `alpha`. **Expect** that no such machine has been heard from.

## 2. Nothing can take the name

1. Enrol a repository as `human-user`. **Expect** a refusal naming the person.
2. Launch `parley-mcp` with `PARLEY_AGENT=human-user`. **Expect** the attach refused.
3. `parley retire human-user@alpha` and `parley purge human-user@alpha`. **Expect** both refused.

## 3. An agent asks the person while they are away

1. With no surface open, have a scripted agent send a Request to `human-user@alpha`.
   **Expect** `receiver_is` to say the person has it and will answer. **Expect** it not to say unreachable.
2. Health (`Alt+y` in the terminal) after the flag threshold — `parley flags` went in 019. **Expect** *waiting on you*, naming the agent, with no
   "ask the agent" remedy.

## 4. The person opens a surface and answers

1. Open `parley`. **Expect** no join step; the status line shows one message waiting.
2. Answer it from the transcript. **Expect** the agent's next collection to hold the Result, attributed to
   `human-user@alpha` with responder `human:human-user`.
3. `parley registry`. **Expect** `here`.

## 5. Away and back

1. Leave the terminal untouched for the idle window (set shorter through the test seam, not by waiting twenty
   minutes in a walk). **Expect** `away` in the registry and nothing on the terminal saying the seat was released.
2. Press a key. **Expect** `here` again within one refresh, and sending works with no join.

## 6. The web UI is the same person

1. Open the web UI while the terminal is open. **Expect** no Join link, the same waiting count, and a message sent
   from it attributed to the same `human-user@alpha`.

## 7. A real agent waits for the person

The one check a scripted agent cannot make (SC-006). Ask this repository's own agent a question it can only
answer with the person's decision. **Expect** it to ask `human-user` and wait, not report `blocked`. Record what
it actually did in the ledger, whichever way it went.
