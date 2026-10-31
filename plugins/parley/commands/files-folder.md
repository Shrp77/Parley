---
description: Open the folder holding a conversation's files, or the whole file location, or say which machine holds it.
disable-model-invocation: true
---

# Open a conversation's files folder

Opens the folder holding one conversation's files in this machine's file browser, or the whole file location when no
conversation is given.

Run, with any `--conversation <id>` or `--project <name>` the person gave - and, when this machine names more than one project and they named none, `--project` with the one this repository answers in (the `PARLEY_PROJECT` of its `parley-<project>` entry in `.mcp.json`; ask which if it has several):

```
parley files folder $ARGUMENTS
```

Show the person what it says. On the machine running the project's broker it opens the folder. Anywhere else it launches
nothing and says which machine holds the files and in which folder, because they are not on this machine to open. A
conversation with no files has no folder to open, and it says that instead.

Nothing the person does in that folder reaches Parley, and Parley puts the folder right the next time it checks the
file location. A file copied in by hand is not in Parley: it is never listed, `/parley:files-list` counts such files
without naming them, and the next check deletes it. A placed file whose bytes are changed there is moved to the
quarantine folder, and `/parley:files-problems` lists it. To hand an agent a file, place it with `/parley:files-place`.
