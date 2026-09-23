---
description: Remove named files from Parley's file location, whichever agent placed them, after the person confirms.
disable-model-invocation: true
---

# Remove files from Parley's file location

Takes one or more file names. A removed file is gone for everybody, whichever agent placed it, and every agent that
fetched it is told it is gone the next time it checks its inbox. It cannot be undone.

**The confirmation is the person's, and you pass it through rather than answering it.**

## 1. Show what would go

Run, with the names and any `--conversation <id>` or `--project <name>` the person gave - and, when this machine names more than one project and they named none, `--project` with the one this repository answers in (the `PARLEY_PROJECT` of its `parley-<project>` entry in `.mcp.json`; ask which if it has several):

```
parley files remove $ARGUMENTS
```

It describes each file and asks for `yes` on its input. Nothing is typed there, so it removes nothing and says so. Show
the person what it described. If a name is in more than one conversation it says which; ask the person which one they
mean and use `--conversation`.

## 2. Ask the person

Ask whether to remove exactly those files. Go on only if the person says yes in their own words, now. A yes to
something earlier, or your own view that the files are no longer needed, is not their confirmation.

## 3. Remove them

Only after that yes, run the same command again with `--yes` added, and show the person the result: what was removed,
how much space was freed, and anything refused with its reason.
