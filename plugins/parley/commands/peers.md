---
description: Write or refresh the Peers section of this repository's standing instructions from the live Parley roster.
disable-model-invocation: true
---

# Name this repository's peers

The Parley server introduces itself when it connects: it tells this agent that peers exist in
other repositories, that inventing their behaviour is the mistake to avoid, that the inbox is
pull rather than push, and what a question has to carry. What it cannot know is **which** peers
this repository has and **which** boundaries it does not own, because those are facts about one
project and they change.

That part is this file's job.

## 1. Read the roster

Call `parley_agents`. It returns every registered agent, whether it is reachable, and - for
each one whose launch configuration says so - an `owns` line describing what that agent is
responsible for. If the tool is not available, the server is not connected: run
`/parley:setup` first, or restart if it has just been configured.

## 2. Establish what each peer owns

**Use the `owns` line the roster gives you.** It was written by whoever configured that
repository, which makes it the closest thing to a first-hand statement you can get without
asking a person. Quote it or shorten it; do not rewrite what it claims.

For a peer with no `owns` line, ask the user. **Do not invent one.** Guessing at another
repository's responsibilities in the very file that exists to stop this agent guessing at
another repository is the worst available outcome - it reads as settled fact for as long as
the file lives. If the user does not know either, the honest entry is the peer's name and
"boundary not yet agreed", which still tells this agent that somebody is there to ask.

If several peers have no line, that is worth saying out loud: whoever configured them can add
`PARLEY_OWNS` once each and every agent in the project sees it, rather than every repository
keeping its own description of everybody else. That is the whole reason the field exists.

## 3. Write the block

Put it in this repository's standing instructions - `CLAUDE.md` at the repository root, or
`AGENTS.md` if that is what the project uses. Replace an existing `## Peers` section rather
than appending a second one. Leave the rest of the file untouched.

```markdown
## Peers

Other repositories have their own agents, reachable through Parley. They cannot see this
repository and this repository cannot see theirs.

- `billing` owns invoicing and tax. This repository computes a net order total and does not
  apply VAT.
- `inventory` owns stock levels and reservations. This repository does not decide whether an
  order can be fulfilled.

**Do not guess at what these repositories do, and do not edit them. Ask.** A question must say
what you will do with each possible answer.
```

Keep it to a line per peer. This block is in context for every turn of every session, so it is
charged against the same budget Parley exists to protect - and a paragraph about a peer is
exactly the shared-prose store the protocol replaces.

Write no paths. Which directory a peer works in is deliberately withheld from the agent-facing
roster, and a path in this file hands it back.
