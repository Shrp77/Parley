---
description: Configure several repositories as Parley agents in one pass, choosing them from a picker.
disable-model-invocation: true
---

# Set several repositories up as Parley agents

`/parley:setup` enrolls one repository, running the same `parley enroll` this command does, and ends with a restart. Running
it once per repository works, and stops working somewhere around the fourth: it costs a session,
a restart and a person's attention each time, and `/parley:write-peers` has to be run again in
every repository already done, because each new agent makes every block already written stale.

Onboarding *n* repositories one at a time does not cost *n* roster passes. The first repository's
block is rewritten *n* times. Registering everything first and reading the roster once at the end
costs *n*, and the blocks that come out are the only ones that were ever true.

This command does the part that batches. `parley enroll` writes launch configurations, node
entries and the placeholder peers frame - none of which need the tools bound,
which is why they can all happen before any restart. What it does not do is the two things that
cannot be batched: the `owns` line, which each agent is asked for once it is running, and
`/parley:write-peers`, which needs a live session per repository.

It is a verb on the `parley` command rather than a script beside this file, because .NET is the
only runtime a deployment is guaranteed to have - nothing is an agent without `parley-mcp` on
PATH, and that arrives through `dotnet tool install`. The `parley_waiting` hook is Python and is
written to fail silently when its interpreter is missing, which is survivable for a mail check
and not for a program that writes configuration into eleven repositories.

## 1. Check the server is installed

```
dotnet tool list --global
```

Look for `parley.mcp` and `parley.cli`. If either is missing, `/parley:setup` step 1 has the build-from-clone
instructions; this command does not install anything. A broker also has to be running before any
of these repositories can connect - `parley autostart install` serves every project this
machine names - but that is needed at restart time, not now.

**If this machine runs more than one Parley deployment**, `parley project list` says which, and
every command below takes `--project <name>` to say which one is being enrolled into. It matters
here because the key written into each repository is `parley-<project>`, so a run against the
wrong one leaves every repository answering in a deployment nobody meant. With one project
configured, or none, nothing needs saying.

## 2. Enrol them

```
parley enroll <directory holding the repos>
```

One verb. It lists every git repository directly under that directory and **asks which ones to
enroll** - arrow keys to move, space to tick, `a` all, `n` none, enter to confirm, escape to back
out. Then it configures the ticked ones, catalogues them, and starts each so that it registers
itself.

**Each agent is named from its repository's existing `PARLEY_AGENT`, or else from its folder name.** There is no option to set a name. If a folder-derived name is wrong, see `/parley:setup` step 3 for what to do.

**Nothing is ticked to begin with.** A scan finds every checkout under a directory and most are
usually not meant to be agents. Repositories that already have an `.mcp.json` are marked so,
because leaving a working agent unticked is how one quietly stops being offered.

**The picker needs a terminal.** In a script or CI, pass `--all` or `--none`. The terminal (`parley-terminal`)
has the same picker inside a project, as **Agent > Enroll agents from folders** (`Alt+f`), with a folder browser
in front of it - on a machine where the user is at a keyboard, that is the easier way in.

**`--describe` says what would change and changes nothing.** Read it with the user before the real
run. Three behaviours are worth knowing so the output is not surprising:

- **An existing `## Peers` section is left exactly as found.** That block holds three kinds of
  line and only two of them belong to any tool: the frame, the roster, and whatever the project
  has added about how the roster is to be read. Nothing there can tell the third from the second,
  so it never rewrites any of them - it reports and moves on. Those repositories still need
  `/parley:write-peers` in step 3; they just do not get a placeholder they did not need.
- **`.mcp.json` is merged, never replaced.** Other servers are left alone and reported by count.
- **Validation is whole-run and happens before any write.** A bad name in the ninth repository
  stops the first eight from being written, because a half-enrolled set is worse than none: the
  repository that did not get its entry looks exactly like one nobody meant to include.

**Enrolling is the consent to start it.** A catalogued repository can be started by a peer's
request with nobody watching, which spends whatever that agent costs to run. The command says so,
and names how many it is about to start, before it configures anything.

## 3. Read the roster once

Enrolment prints this, and it is the part that has to be done in order:

1. A broker running, and this machine's node with it. `parley autostart install` serves every
   project on this machine from one process that starts again at every logon. One process,
   because a node that is not running leaves every repository you just enrolled reading as
   startable and never started. (`parley serve --project <name>` serves one project alone and is
   for testing only.)

   `node.json` sits beside the projects at the root and every project starts from it.
2. Each repository started by the enrolment, and **registered**. Enrolment starts them itself and
   reports which ones registered - one reported as *not known* has not failed, it simply had not
   registered by the time the command stopped waiting. A session already open in a repository
   loads a new launch entry only when it restarts, which is the likeliest reason for one that is
   not seen.
3. **Only then**, `/parley:write-peers` in each one.

Step 3 last is the whole point of the command. Run it in the first repository before the last one
has registered and its block is stale on arrival - it will name a roster that has since grown, in
a file read on every turn, which is the failure `/parley:write-peers` exists to prevent wearing a
different hat.

**Repositories that were already agents before this run need it too.** Their blocks were true for
the old roster. Adding agents makes every one of them wrong by omission, and nothing tells them
so - the roster is pull, and a stale block reads exactly like a current one.
