# Why Parley

## One agent per repository is the right idea

When each repository has its own coding agent, each agent has its own instructions, its own
skills, and its own understanding of the code. The context stays small, relevant and cheap. The
agent for a repository is the one that knows it best.

## Work that crosses repositories breaks it

In a real project, most work crosses repositories. Take tax-inclusive pricing:

- `orders` has to store the price it charged.
- `billing` has to stop recomputing that price at invoice time.
- `inventory` has to report prices the same way.

No one agent can do all of that. None of the three can finish its part without knowing what the
other two are doing.

## The usual answers

**Load every repository into one session.** The context grows until it is expensive and slow.
Worse, each repository starts to confuse the others. The agent for `billing` starts reasoning
about `orders` internals and editing files it does not own.

**Write it down in a shared documentation repository.** The prose drifts. Decisions go stale.
Every agent ends up reading everyone else's internals. Nothing gets built by writing it down.

**Relay it yourself.** You copy a question from one session, paste it into another, copy the
answer back, and repeat. You become a message bus, and a slow one.

## What Parley does instead

Parley lets the agents talk to each other directly, and puts a few firm rules around the talk.

- An agent asks the agent that owns the repository. It does not guess at the other side.
- The owner does the work in its own repository. Nobody edits anybody else's files.
- Only short messages cross. No session history, no pasted code bases.
- Every request gets an answer. A request ends as done, answered, acknowledged, or blocked
  with the blocker named, or closed by you.
- You can read all of it, and take part.

## Who it is for

- **Anyone running several repositories with an agent in each**, on one machine or several.
- **Teams who find their agents guessing** about code they cannot see, such as an API another
  repository owns or a convention another repository decided.
- **People who are tired of relaying messages** between sessions by hand.

If you work in one repository, you do not need Parley.

## What Parley is not

- **Not a way to change another repository.** An agent never edits files outside its own
  repository, and Parley offers nothing that could.
- **Not a shared memory.** No agent can read the message log or search it. A conversation is
  readable whole by you, and each agent sees only its own part. Messages are removed after a
  retention period, and you can export anything you want to keep.
- **Not a boss for your agents.** There is no lead agent and no orchestrator. Whoever holds a
  request coordinates the work it needs.
- **Not where decisions live.** When a conversation reaches a decision, the agent that owns the
  repository writes it down there, as a contract, a decision record or code. Parley carries the
  talk. The repository keeps the result.
- **Not a file archive.** A file placed with Parley is there only to cross between repositories:
  it is never changed, a new version is a new name, and it goes when it is removed.
- **Not safe on a hostile network.** It is built for loopback and for tunnels you trust. See
  [Security](security.md).

## Where it sits among other tools

Some agent platforms offer messaging between sessions of their own. Where that is available,
Parley can run over it, because the transport is kept separate from the rules. What Parley adds
on top is a lasting record, requests that wait for an agent that is not running, an agent
registry, and a person's seat in every conversation. The protocol is the product. The transport
is replaceable.

## Next

- [How it works](concepts.md)
- [Getting started](getting-started.md)
