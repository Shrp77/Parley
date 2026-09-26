# Contract: the ask

What a person's surface sends when they ask an agent what it owns. **One implementation**, read by the
terminal and the web UI, so that an answer given to one surface would have been given to the other —
the drift this repository has a test for.

## What it is, in protocol terms

A **Request**, sent by the person from their seat, opening one ordinary exchange in one conversation.
Protocol 0.7: a Request *is* the conversation it belongs to and the ask, with no items and no finish
line, so there is nothing here to compose beyond the sentence itself.

- It is **not** a new operation. FR-002.
- It is **one exchange per agent**, N of them for a bulk ask, on §5.5's terms. FR-006.
- It carries **no marker of any kind**. The conversation's identifier is what a surface recognises;
  the payload is prose and nothing parses it. `research.md` R2.

## The conversation identifier

```
owns-<role>-<yyyyMMdd-HHmmss>
```

The role rather than the whole agent name, because `@` is outside `ConversationName`'s character set.
The timestamp because asking twice must not collide, and because the later discussion is the one that
produced the line somebody is reading.

**The identifier is derived and never typed.** A person naming this conversation themselves would be a
person choosing whether their surface can find it afterwards.

## The text

Held in one place and asserted by a test, because FR-003 is a promise about what the agent was told and
a promise nobody checks is prose.

```
What do you own in this deployment, and what do you not?

I am asking because your answer becomes one line in the registry. Every agent in this
deployment reads that line whenever anybody lists the roster, and it costs each of them
context every time -- so it is read far more often than it is written. It is what a peer
uses to decide whether to send a piece of work to you or to somebody else. Nothing is
routed by it automatically and nothing is refused because of it; it is advice to whoever
is choosing who to ask.

Please answer in two parts:

1. What you own -- the areas of work that are yours, in as few words as carry the meaning.
2. What you do not own, especially anything a peer might reasonably assume is yours. If
   you believe another agent owns it, name the agent.

The second part is the half nobody else can supply. A repository can be read to find out
what it contains; it cannot be read to find out what it has decided not to own, and a
person guessing is no better placed than a tool guessing.

A person will read your answer, may disagree with it, and may argue about it with you
before anything is written. Nothing you say here changes any configuration by itself.
```

**Every paragraph is answering a requirement**, and none of it is padding:

| Passage | Requirement |
|---|---|
| "one line in the registry … reads that line whenever anybody lists the roster … costs each of them context" | FR-003 |
| "Nothing is routed by it automatically and nothing is refused because of it" | FR-030, so the agent does not over-claim to win work |
| "in as few words as carry the meaning" | Principle IV, which is the one thing left standing after R1 removed the number |
| the two numbered parts | FR-004 |
| "If you believe another agent owns it, name the agent" | FR-004, and US4's second scenario |
| "A person will read your answer, may disagree" | FR-012, and it is the truth: an answer arriving changes nothing |
| "Nothing you say here changes any configuration by itself" | Principle III, said to the agent so it does not report having set something |

**Rule 8.1 is satisfied by the shape rather than by the length.** It asks for one thing — a description
of a boundary, in two halves that are one question — and it says what it is for. It is long for an ask
and it is not two asks.

## What it must fit inside

The whole Request is charged against rule 8.4's cap of 8,000 characters. The text above is about 1,400,
which leaves the cap's remainder for nothing, because there is nothing else in the message. A test
derives the headroom rather than asserting a literal, for the reason `MessageCapTests` already does.

## What comes back

An ordinary Result, or a Clarify if the agent has a question (FR-009, §5.2). Neither is special-cased:

- **The proposal** a surface shows is the **latest** thing the agent said in that exchange, which is
  0.30.1's rule and is correct for the same reason — an agent that revises after a disagreement has
  said two things, and the one to accept is the later.
- An answer that is a question, an apology or a refusal is shown as it came (FR/edge case). The surface
  does not try to decide whether prose is a statement of ownership; a person reads it.
- An agent that says it does not know has answered. Recorded as an answer, nothing written.

## Asking more than one

FR-007's statement is shown **before the first Request is sent**, and answered:

```
This asks 19 agents what they own. Each one costs that agent a turn.
They are asked separately, and answers arrive as they come.
```

Then every ask is sent, and only then is anything waited on — 0.25.1's rule, for its own reason:
thirty seconds each over nineteen agents is nine and a half minutes on a serialised queue, where one
wait for the set is one window however many there are. An agent that cannot be asked is named and the
rest proceed.
