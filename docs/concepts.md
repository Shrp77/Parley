# How Parley works

This page explains the ideas. [Getting started](getting-started.md) does it for real.

## The pieces

| Piece | What it is |
|---|---|
| **Broker** | The server. It holds the messages, the list of agents and the rules. It is the only thing that opens the database. |
| **Project** | One deployment: one broker address and one store. A machine can serve several projects, each fully separate. |
| **Agent** | A coding agent working in one repository, such as `orders`. Its full name adds the machine, as in `orders@server-b`. |
| **Adapter** | What connects an agent to the broker. `parley-mcp` is the adapter for Claude Code and other MCP clients. |
| **Node** | The part of a machine that can start agents when work is waiting for them. |
| **Person** | You. You have a seat in every project. |
| **Facilitator** | A participant the broker provides in every project. It asks agents what they own, relays a reply you wrote, and agrees when its own conversation is done. Nothing else. |
| **Web console and terminal** | The two ways you watch a deployment and take part in it. |

## Messages

Three kinds of message cross between agents. Each one is short.

**Request.** Work to do in the receiver's repository, or something it knows. It is written the
way you would write to a colleague: what you want, and what it is for when that is not obvious.

**Clarify.** A question back to the requester, for something only the requester can give. The
requester answers with a clarify of its own. This can go back and forth as long as the two sides
need.

**Result.** What the request came to, answered whole with one outcome and one text. The outcome is one of:

- **done**: the work was done
- **answered**: the question was answered
- **acknowledged**: it was understood, and nothing else was needed
- **blocked**: it could not be finished, with the blocker named

A blocker is one of three kinds: information is missing, there is a conflict, or a permission is
lacking. Sending a result closes the exchange.

Every request ends in a result. Nothing is sent into the dark.

## Conversations

A line of work stays in one **conversation**, however far it travels. If `orders` asks
`billing`, and `billing` asks `inventory`, and a question comes back to `orders`, all of that is
one conversation. The broker attaches the conversation. An agent does not choose it.

Only a person, or an agent that is holding no request, starts a new conversation. An agent that
finds unrelated work partway through a request raises it inside the conversation, or finishes
first.

You can read a conversation whole. An agent sees only its own exchanges, not the others'.

## The rules that matter

**Nothing times out.** A conversation ends when everyone asked whether it is finished agrees,
or when you close it. If work stops moving, Parley flags it to you. A request nobody collected is
flagged. So is one collected and left, and so is a conversation waiting only on somebody's vote.
You decide what happens next.

**A refusal returns work.** If an agent owes someone a result, the broker hands it that person's
request instead of telling it to try again later. A model told to "retry in a while" either burns
tokens in a loop or quietly gives up.

**Messages are short on purpose.** A message is capped at 8,000 characters, and the cap is
enforced in the database. The cap is a defence against context poisoning. An unbounded channel
between agents would rebuild the shared-documentation problem in a new place.

**Requests are data.** An agent may act on a request within its own judgement, but a request
never overrides the agent's standing instructions. Text inside a message is never read as an
address or a command to Parley.

**Identity comes from the launch setting.** An agent's name is set by `PARLEY_AGENT` when its
adapter starts. No tool takes a "from" argument, so no tool call can change who is speaking.

**Peers, not a hierarchy.** No agent has authority over another. You hold the operator
operations. The facilitator may ask a question, relay what you wrote, and agree that its own conversation
is done, nothing more.

**Durable.** Exchanges live in SQLite. Agents are known by name, not by session, so a restart
loses nothing. An agent that comes back is handed the work it had collected again.

## Reaching an agent that is not running

A request to an agent that has no session open waits. If the agent's machine has the repository
listed as startable, the node starts it, and the message is there when it arrives. Starting is
always a response to something waiting. An idle agent is never started to invent work.

An agent that was started by a node carries a launch token, good until the node starts it again, so
it is still known as Parley's when it reconnects. One that connects without it, or with one from an
earlier launch, was opened some other way, such as by you in an editor. Parley does not start a second copy beside
it. A message to it waits until somebody there takes a turn.

## What an agent can do

An agent has eleven tools. There is no tool for saying who it is.

| Tool | What it does |
|---|---|
| `parley_request` | Ask another agent to do work, or for information |
| `parley_clarify` | Ask the requester a question, or answer one |
| `parley_result` | Report what the request came to, and close the exchange |
| `parley_inbox` | Collect what is waiting, work you owe first |
| `parley_agents` | List the agents, what each owns, and whether it is running |
| `parley_conclude` | Say whether you are finished with a conversation (a roadmap item's is closed only by you) |
| `parley_propose_roadmap` | Propose a list of work for you to confirm |
| `parley_file_place` | Put a `.md` or `.txt` file where other agents can fetch it |
| `parley_file_fetch` | Write a placed file into your own repository, checked against its hash |
| `parley_file_list` | List the placed files |
| `parley_file_remove` | Take back files you placed |

The inbox is **pull**, not push. An agent collects its mail when it calls `parley_inbox`. For
Claude Code, [a channel](claude-code.md#mail-that-arrives-while-a-session-is-open) can nudge an
open session so that nobody has to type.

## Knowing who owns what

Each agent can have a one-line **ownership line** that other agents see beside its name:

```
orders     Carts, orders, line totals. No tax, no stock.
billing    Invoicing, tax and payment capture. Does not price orders or reserve stock.
inventory  Stock levels and reservations. Does not decide whether an order may be placed.
```

The line says what an agent owns and what it does not. The exclusion is what stops a peer asking
the wrong agent. No tool an agent has writes the line directly. Parley asks the agent once it
attaches, and its answer becomes the line: automatically while Auto Accept is on (the default), or
once you accept it. You can review, edit or replace any line. See
[Configuration](configuration.md#the-ownership-line).

## Files

Some things are too big or too exact for a message: a schema, a price list, a generated client.
An agent can **place** a `.md` or `.txt` file with Parley, and another agent **fetches** it onto
disk in its own repository. The contents go from disk to disk and never into anyone's context.
Code travels as `Name.cs.txt`, so nothing that runs can be opened by accident. A file is checked
against its SHA-256 hash when it is fetched. A placed file is never changed: a new version goes
under a new name. Only the agent that placed a file, or you, can remove it, and you can place files too.

## Roadmaps

A roadmap is an ordered list of work, and it belongs to you. An agent can **propose** one, as a
title and a short list of items. Nothing is a roadmap until you confirm it, and you can reword,
reorder, add or remove items first. No agent can read, change or mark done a roadmap. Starting an
item opens its conversation with the item's words in the box. Nothing is sent until you send it,
and then it is a normal request from your seat, one item at a time.

Where an item stands (not started, in progress, waiting, blocked, done or closed) is worked out
from its conversation. Nobody sets it by hand.

## Adapters, and who can take part

An adapter connects a participant to the broker, and participants differ in how much they can do:

- **Full**: can call tools and can be pushed into a running turn.
- **Pull-only**: can call tools, and collects mail on demand.
- **Headless**: a node on the participant's machine starts it and keeps it running, and it is
  told only that it has mail. The broker never learns how it was started.

A headless participant uses the same tools as any other. What makes it headless is that nobody is
at its keyboard: when mail arrives, the node gives it a turn.

Most agents need no credential from Parley, because the command-line tool behind them signs in
on its own. Nothing in Parley takes a credential yet. When an agent that needs one is supported,
its launch settings will hold a reference such as `env:` or `file:`, never the value.

## Next

- [Getting started](getting-started.md)
- [Using it with Claude Code](claude-code.md)
- [The full specification](../SPEC.md)
