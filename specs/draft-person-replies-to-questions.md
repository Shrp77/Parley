# Draft: a person replies to a question on its exchange

**Status:** draft for the owner's decision. `SPEC.md` is not changed. No protocol field, enum or
schema moves with this change; every operation it needs already exists.

## Why

SPEC §5.6 says everything a person writes to an agent is a Request, so the console never has to
guess which question the words answer. The cost showed in conversation `rm1-3`. The agent asked
back on the person's own request (exchange 49). The person answered it. The answer was recorded as
a new Request (exchange 50), and exchange 49 stayed open, "waiting on you", through three more
rounds.

§5.2 and §5.6 also disagree: §5.2 says a person's reply "may come as a new Request"; §5.6 says it
"is" one. The Inbox already replies inside the exchange (`ClarifyAsync`), so the code takes the
§5.2 reading in one place and the §5.6 reading in another.

## The change

### §5.2, last paragraph. Replace with:

**Where the requester is the person, the reply comes one of two ways.** If the person chose to
answer that question, the reply is a Clarify on the exchange, which hands the turn back to the
agent (§5.6). If the person wrote something new, it is a Request, and the agent decides what that
settled (§5.6).

### §5.6, first paragraph. Replace with:

**Everything a person writes to an agent is a Request, except a reply the person gives to a
question.** A question is an agent's Clarify to the person that is still waiting on them. A
surface offers a reply on each such question, and the person choosing it is what says which
question the words answer. A surface never infers it from the words or from what is open: the
agent may have asked something days ago while the person moved on, and a surface that guessed
could file a person's words as the answer to an unrelated, older question.

A reply to a question is a Clarify from the person on that question's exchange. The turn goes back
to the agent, no new exchange opens, and depth does not move (§5.2). Anything typed without
choosing a question is a Request, as before.

### §5.6, new paragraph after "The agent decides what that settled." Add:

**A reply does not close the question; the agent does.** After a reply on its exchange the agent
decides whether it was enough. If it was, the agent sends the Result. If it was not, the agent
sends another Clarify. The broker closes nothing on the agent's behalf (principle 2.5), and no
surface does either. `settles` keeps its meaning for words a person sent as a new Request that
answered an open question anyway.

## What it touches

- **Web console:** done in 0.42.62 on the conversation page, as "Reply to this" on a waiting
  question. It is code first, ahead of this text, until the owner decides.
- **Terminal:** `DriftTests` asserts both screens bind every `BrokerClient` operation, and the
  terminal's reply to a question needs the same entry. Owed.
- **Tests:** `Parley.Spec.Tests` checks section references and rule numbering; none changes.
- **Plugin prose:** the agent-facing text that says a person's line is always a Request needs the
  exception, and should say what to do when a person replies on an exchange.

## Open question: the agent's own list of pending questions

The owner proposed that an agent tell Parley it has a question, Parley number it (Q1, Q2, …), and
the agent confirm when it is answered, which removes it from what is pending.

This is already close to what exists. The question is the Clarify, its number is the exchange
number, and "the agent agrees it is answered" is `settles` or the Result. What is missing is
narrower:

1. **The answer does not travel with the hand-back.** When `settles` hands exchange 49 back, the
   agent collects the original Request again and sees only its first words, without the person's
   later answer. In `rm1-3` it asked the same question a third time. Delivering the person's
   answer with the handed-back Request would address that directly. It is a guess at the cause;
   the conversation shows the agent re-asking but not why.
2. **An agent cannot withdraw its own question** without sending a new message.

A full question registry would add two agent operations (§10.1), a table, a proto service change,
new boundary tests and plugin prose. Recommended order: the §5.6 amendment above, then (1), then a
registry only if those prove not enough.
