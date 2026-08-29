---
description: Configure several repositories as Parley agents in one pass, from a manifest the user fills in.
disable-model-invocation: true
---

# Set several repositories up as Parley agents

`/parley:setup` is for one repository and asks a person the things only a person knows. Running
it once per repository works, and stops working somewhere around the fourth: it costs a session,
a restart and a person's attention each time, and `/parley:write-peers` has to be run again in
every repository already done, because each new agent makes every block already written stale.

Onboarding *n* repositories one at a time does not cost *n* roster passes. The first repository's
block is rewritten *n* times. Registering everything first and reading the roster once at the end
costs *n*, and the blocks that come out are the only ones that were ever true.

This command does the part that batches. `parley enroll` writes launch configurations, node
entries and the placeholder frame of `/parley:setup` step 6 - none of which need the tools bound,
which is why they can all happen before any restart. What it does not do is the two things that
cannot be batched: the `owns` line, which has to come from a person, and `/parley:write-peers`,
which needs a live session per repository.

It is a verb on the `parley` command rather than a script beside this file, because .NET is the
only runtime a deployment is guaranteed to have - nothing is an agent without `parley-mcp` on
PATH, and that arrives through `dotnet tool install`. The `parley_waiting` hook is Python and is
written to fail silently when its interpreter is missing, which is survivable for a mail check
and not for a program that writes configuration into eleven repositories.

## 1. Check the server is installed

```
dotnet tool list --global
```

Look for `parley.mcp`. If it is missing, `/parley:setup` step 1 has the build-from-clone
instructions; this command does not install anything. A broker also has to be running before any
of these repositories can connect - `parley serve --db <path to the store>` - but that is needed
at restart time, not now.

**If this machine runs more than one Parley deployment**, `parley project list` says which, and
every command below takes `--project <name>` to say which one is being enrolled into. It matters
here more than anywhere else: consent to run unattended is recorded in a node catalogue, there is
one per project, and a run that writes to the wrong one records ten repositories as startable
that nothing will ever start - reporting success. With one project configured, or none, nothing
needs saying.

## 2. Scan for the repositories

```
parley enroll scan <directory holding the repos> parley-agents.json
```

It lists every git repository directly under that directory and **asks which ones to enroll** -
arrow keys to move, space to tick, `a` all, `n` none, enter to confirm, escape to back out.
Repositories that already have an `.mcp.json` are marked so, because leaving a working agent
unticked is how one quietly stops being offered.

**Enrolling is opt-in and nothing is ticked to begin with.** That is not caution for its own
sake: a scan finds every checkout under a directory, and most are usually not meant to be
agents. It cannot be inferred from the `owns` line being filled in either, because an empty one
is a legitimate state - an agent with no line is listed with none - so a blank field would
otherwise mean both "boundary not agreed yet" and "I never looked at this row".

The picker needs a terminal. In a script or CI, pass `--all` or `--none`; without one, nothing
new is marked and the user is told to set `enroll` in the file.

It suggests an agent name from the directory name, carries over the `agent` and `owns` of any
repository that already has a `parley` entry in its `.mcp.json`, and **merges with an existing
manifest at that path** - so re-running after new repositories appear keeps every line already
written.

**A repository configured once before still has its old line in git history**, even if `.mcp.json`
was later deleted - which is the ordinary state of a deployment being reconfigured. `enroll`
recovers it: `scan` reads the repository's own history and puts the earlier registration into
`_evidence`, with the commit it came from and the name it used.

That row is the strongest evidence there is and is not a suggestion at all - it is the user's own
sentence, recovered rather than composed. Prefer it over anything you would write, say which
commit it came from, and note that a boundary may have moved since. It still needs their yes:
`owns_approved` is theirs to set.

If git is missing or the repository has no such history the row is simply absent, and the rest of
the evidence stands. Evidence is optional by design; nothing fails because it is thin.

## 3. Have the user fill in the manifest

**This is the step that is theirs, and the reason this command is not a single button.** The
manifest arrives with every `owns` line empty and a `_evidence` list beside it: the repository's
README line, its top-level directories, and any earlier registration recovered from its own git
history.

**Draft a suggestion into `owns`, and leave `owns_approved` false.** SPEC.md 17 question 10, as
amended, permits this on three conditions, and the amendment is only honest if all three hold:

1. **Cite the evidence for each suggestion** when you put it to the user - which line of which
   README, which prior commit. They are approving a sentence against material, not against your
   confidence. `_evidence` already carries it; quote from there rather than from your reading of
   the tree.
2. **`owns_approved` is theirs to set, never yours to assume.** `enroll` refuses any filled-in
   `owns` that does not carry it, which is what keeps the field's claim true: the registry no
   longer promises a person composed the line, only that a person read it and said yes. Set it
   only after they actually say so, per repository, and never in a batch they did not look at.
3. **Never invent the exclusion half.** A repository can show what it contains and cannot show
   what it has decided not to own - no tax code in the tree does not mean tax belongs to somebody
   else, only that nobody has written it yet. Draft the ownership half, leave the exclusion blank,
   and say plainly that you have left it blank and why. Take "I do not know yet" for an answer:
   an agent with no line is listed with none, and askers fall back to its name.

**An earlier registration is not a suggestion at all** - it is the user's own sentence, recovered.
Prefer it over anything you would write, say which commit it came from, and note that a boundary
may have moved since. That is the strongest row in `_evidence` and usually ends the conversation
for that repository.

**A drafted line still anchors, and the amendment says so.** Somebody shown a plausible sentence
agrees more readily than somebody facing a blank. You cannot remove that, so do not paper over it:
put the evidence and the draft side by side and invite the correction rather than the nod.

Two other fields need the user, not this agent:

- **`agent`** is suggested from the directory name, which SPEC-wise is the wrong source - a name
  should say what the repository owns, not where it sits. Walk the list with them. `enroll`
  refuses duplicates, because two servers claiming one name means either may collect the other's
  mail, but it cannot tell that `acme-orders-api` names a directory rather than a domain.
- **`unattended`** is consent for a peer's question to start that repository with nobody watching,
  spending their tokens. Ask per repository and take no for an answer. It is `false` by default
  and reversible by removing the entry from `node.json`.

**Scan for a boundary that two agents both claim** while reading the list. Two `owns` lines that
overlap are worse than one that is vague: a peer with a question has two plausible agents and no
way to choose. Say so before enrolling, not after.

## 4. Dry run, then apply

```
parley enroll plan parley-agents.json      what would change, without changing it
parley enroll apply parley-agents.json     make the changes
```

`plan` prints what would change in each repository and writes nothing. Read it with the user
before running `apply`. Three behaviours are worth knowing so the output is not surprising:

- **An existing `## Peers` section is left exactly as found.** That block holds three kinds of
  line and only two of them belong to any tool: the frame, the roster, and whatever the project
  has added about how the roster is to be read. Nothing there can tell the third from the second,
  so it never rewrites any of them - it reports and moves on. Those repositories still need
  `/parley:write-peers` in step 5; they just do not get a placeholder they did not need.
- **`.mcp.json` is merged, never replaced.** Other servers are left alone and reported by count.
- **Validation is whole-manifest and happens before any write.** A bad name in the ninth entry
  stops the first eight from being written, because a half-enrolled set is worse than none: the
  repository that did not get its entry looks exactly like one nobody meant to include.

## 5. Restart, then read the roster once

`enroll apply` prints this, and it is the part that has to be done in order:

1. A broker running: `parley serve --db <path to the store>`
2. Restart Claude Code in every enrolled repository, and check `/mcp` shows `parley` connected.
3. **Only then**, `/parley:write-peers` in each one.

Step 3 last is the whole point of the command. Run it in the first repository before the last one
has registered and its block is stale on arrival - it will name a roster that has since grown, in
a file read on every turn, which is the failure `/parley:write-peers` exists to prevent wearing a
different hat.

**Repositories that were already agents before this run need it too.** Their blocks were true for
the old roster. Adding agents makes every one of them wrong by omission, and nothing tells them
so - the roster is pull, and a stale block reads exactly like a current one.
