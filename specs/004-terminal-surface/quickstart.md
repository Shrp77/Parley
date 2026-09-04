# Quickstart — the journey this feature is finished when it works

A person on a machine with a broker running and at least one project named.

## 1. Open it

```
parley top
```

It lists the projects this machine knows. Nothing is typed; one is chosen. If the manifest names
none, the broker every other command would reach is offered under a name that says it was not
configured.

If the terminal is not interactive — output redirected, or run from a scheduled task — it refuses
with a sentence naming what to run instead, rather than drawing to a stream nobody is reading.

## 2. See who is there

The chosen project shows its agents: name, presence, tier, and the workspace each owns. An agent
that is registered but not running reads as startable rather than absent, because those are
different facts.

If that project's broker is not answering, it says so, and every other project stays usable.

## 3. Read a conversation

The conversations in the project are listed; one opens as a transcript. Each message names who
sent it, who it was for, and who else could see it. **None of that is carried by colour** — the
colour helps an eye follow one agent down a long thread and never says anything the words do not.

## 4. Take a seat

The person joins under their own name. It is refused if the registry holds that name for
anything that is not a seat, and refused outright if the registry could not be read — a guard
that passed hardest when the deployment was least known would be worth nothing.

They now appear in that project as present, exactly as they would from the web console. Being
seated in both at once is fine; it is two sessions for one agent, which §14 allows.

## 5. Answer something

What is waiting is shown: debts first, per §7.3. Opening a question shows its intent, its
sub-questions and the consequences the asker gave. Answering it resolves each sub-question and
the asking agent receives the answer.

Draining an inbox is a state change, not a look — it marks messages delivered and starts the
working window. The surface says so rather than letting a person discover it.

## 6. Start one

A peer is chosen from the project's agents. Intent, sub-questions and consequences are collected
before anything is sent. Too long, or consequences too thin, is refused **here** with the limit
stated — not by the store after a round trip. If the broker refuses anyway, its own words are
shown, including whatever remedy it named.

## 7. Watch, and hand off

A Logs view follows what the broker is writing, filtered. And one action opens the hosted web
console in a browser, for when a bigger screen is the better reader.

## 8. Leave

Quitting releases the seat. So does interrupting. A person who has gone home stops being present,
because a seat that outlived them is exactly the failure this repository has already shipped
once.

---

## Done when

- A question asked by a peer agent can be answered from the terminal, and the peer receives it.
- The same conversation read here and in the web console never disagrees about who took part or
  who could see what.
- Every one of the web console's own tests still passes, unchanged, against the relocated code.
