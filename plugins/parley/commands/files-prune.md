---
description: Remove every file in Parley's file location, after the person types the project's name.
disable-model-invocation: true
---

# Remove every file from Parley's file location

Removes every file every agent placed, in every conversation of the project. Conversations, messages and agents are
untouched, and every agent that fetched a file is told it is gone. It cannot be undone. This is the only cleanup that
removes everything, and nothing does it on a schedule.

**The confirmation is the person typing the project's name, and you pass it through rather than supplying it.**

## 1. Show what would go

Run, with any `--project <name>` the person gave:

```
parley files prune $ARGUMENTS
```

It says how many files and how much space, and asks for the project's name on its input. Nothing is typed there, so it
removes nothing and says so. Show the person what it said.

## 2. Ask the person to type the project's name

Ask the person to type what the command asked for if they want every file removed: the project's name, or
`remove every file` when no project was named. Go on only if what they type is exactly that. Do not type it for them,
and do not treat agreement in other words as it.

## 3. Remove every file

Only then, run the same command again with `--yes-remove-every-file` added, and show the person how many files were
removed and how much space was freed.
